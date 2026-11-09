using System.Text;
using ChestTracker.Core.Detection;
using ChestTracker.Vision;

namespace ChestTracker.Tests;

/// <summary>Change 3 (EXPERIMENTAL): YOLOv8 pipeline pieces with synthetic data. These do not measure model accuracy.</summary>
public class YoloDecodingTests
{
    private static readonly ModelLabels Labels = new(ModelLabels.Default);

    [Fact]
    public void Letterbox_maps_input_boxes_back_to_the_frame()
    {
        var lb = Letterbox.For(1280, 720, 640);
        Assert.Equal(0.5f, lb.Scale);
        Assert.Equal(0f, lb.PadX);
        Assert.Equal(140f, lb.PadY);
        var box = lb.ToSource(new Box(270, 280, 370, 360));
        Assert.Equal(new Box(540, 280, 740, 440), box);
    }

    [Fact]
    public void Preprocessing_letterboxes_rgb_planes_with_gray_padding()
    {
        // 4×2 pure red BGRA frame into an 8×8 input: 2 rows of padding top and bottom.
        var px = new byte[4 * 2 * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i + 2] = 255;
            px[i + 3] = 255;
        }

        var tensor = new float[3 * 64];
        var lb = YoloPreprocessing.ToTensor(px, 4, 2, 16, 8, tensor);
        Assert.Equal(2f, lb.PadY);
        Assert.Equal(YoloPreprocessing.PadValue, tensor[0], 3);            // padding row
        Assert.Equal(1f, tensor[(3 * 8) + 4], 3);                          // R plane inside
        Assert.Equal(0f, tensor[64 + (3 * 8) + 4], 3);                     // G plane inside
        Assert.Equal(0f, tensor[128 + (3 * 8) + 4], 3);                    // B plane inside
    }

    private static float[] Head(int anchors, params (int Anchor, float Cx, float Cy, float W, float H, int Cls, float Score)[] boxes)
    {
        var channels = 4 + Labels.Count;
        var data = new float[channels * anchors];
        foreach (var b in boxes)
        {
            data[(0 * anchors) + b.Anchor] = b.Cx;
            data[(1 * anchors) + b.Anchor] = b.Cy;
            data[(2 * anchors) + b.Anchor] = b.W;
            data[(3 * anchors) + b.Anchor] = b.H;
            data[((4 + b.Cls) * anchors) + b.Anchor] = b.Score;
        }

        return data;
    }

    [Fact]
    public void Decoder_reads_channels_first_output_and_applies_threshold_and_nms()
    {
        var lb = Letterbox.For(640, 640, 640);
        var data = Head(6,
            (0, 100, 100, 50, 50, 0, 0.90f),
            (1, 102, 101, 50, 50, 1, 0.70f),  // same chest, other class: kept (class-aware NMS), resolved by the tracker
            (4, 101, 100, 50, 50, 0, 0.60f),  // same chest, same class: suppressed
            (2, 400, 400, 60, 40, 3, 0.80f),  // ammo box
            (3, 300, 300, 30, 30, 2, 0.10f)); // below threshold
        var dets = YoloDecoder.Decode(data, [1, 9, 6], Labels, lb, 0.25f, 0.45f);

        Assert.Equal(3, dets.Count);
        Assert.Equal(("chest_closed", ObjectRole.ChestClosed), (dets[0].Label, dets[0].Role));
        Assert.Equal(ObjectRole.OtherContainer, dets[1].Role);
        Assert.False(dets[1].IsChest);
        Assert.Equal(ObjectRole.ChestOpening, dets[2].Role);
        Assert.Equal(new Box(75, 75, 125, 125), dets[0].Box);

        // One chest, two states in one frame: the tracker keeps only the more confident one.
        var update = new ChestTrackManager().Update(dets, DateTimeOffset.UnixEpoch);
        Assert.Single(update.Tracks);
        Assert.Contains(update.Rejected!, r => r.Detection.Role == ObjectRole.ChestOpening && r.Reason.Contains("same chest"));
    }

    [Fact]
    public void Decoder_reads_transposed_output()
    {
        var channelsFirst = Head(3, (1, 320, 320, 100, 100, 2, 0.8f));
        var transposed = new float[channelsFirst.Length];
        for (var c = 0; c < 9; c++)
        {
            for (var a = 0; a < 3; a++)
            {
                transposed[(a * 9) + c] = channelsFirst[(c * 3) + a];
            }
        }

        var det = Assert.Single(YoloDecoder.Decode(transposed, [1, 3, 9], Labels, Letterbox.For(640, 640, 640), 0.25f, 0.45f));
        Assert.Equal(ObjectRole.ChestOpened, det.Role);
    }

    [Fact]
    public void Labels_come_from_ultralytics_metadata_or_lines()
    {
        var labels = ModelLabels.ParseUltralyticsNames("{0: 'chest_closed', 1: 'chest_opening', 2: 'chest_opened', 3: 'ammo_box'}")!;
        Assert.Equal(4, labels.Count);
        Assert.Equal(ObjectRole.ChestOpening, labels.Role(1));
        Assert.Equal(ObjectRole.OtherContainer, labels.Role(3));
        Assert.Null(ModelLabels.ParseUltralyticsNames("garbage"));
        Assert.Equal(ObjectRole.Ignore, ModelLabels.FromLines(["person"])!.Role(0));
        Assert.Equal(ObjectRole.OtherContainer, ModelLabels.RoleOf("Supply Drop"));
    }
}

public class ChestStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Box ChestBox = new(500, 400, 600, 480);

    private static Detection D(ObjectRole role, float conf = 0.85f, Box? box = null) =>
        new((int)role, role.ToString(), role, conf, box ?? ChestBox);

    /// <summary>Feeds one detection list per frame at 8 FPS; returns all events.</summary>
    private static List<ChestOpenEvent> Run(ChestTrackManager m, params Detection[][] frames)
    {
        var events = new List<ChestOpenEvent>();
        for (var i = 0; i < frames.Length; i++)
        {
            events.AddRange(m.Update(frames[i], T0 + TimeSpan.FromMilliseconds(125 * i)).Events);
        }

        return events;
    }

    private static Detection[] F(params Detection[] d) => d;

    [Fact]
    public void Closed_opening_opened_is_counted_exactly_once_with_evidence()
    {
        var m = new ChestTrackManager();
        var events = Run(m,
            F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)),
            F(D(ObjectRole.ChestOpening, 0.8f)), F(D(ObjectRole.ChestOpening, 0.9f)),
            F(D(ObjectRole.ChestOpened, 0.9f)), F(D(ObjectRole.ChestOpened, 0.8f)),
            F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened)));

        var ev = Assert.Single(events);
        Assert.Equal(DetectionOutcome.Confirmed, ev.Outcome);
        Assert.Equal(0.85, ev.Confidence, 2);
        Assert.Equal(2, ev.Evidence.ClosedFrames);
        Assert.Equal(2, ev.Evidence.OpeningFrames);
        Assert.Equal(2, ev.Evidence.OpenedFrames);
        Assert.Equal([ChestState.Closed, ChestState.Opening, ChestState.Opened, ChestState.Counted], ev.Evidence.Transitions.Select(t => t.To));
        Assert.Equal(ChestState.Counted, Assert.Single(m.Tracks).State);
    }

    [Fact]
    public void A_chest_that_disappears_is_never_counted()
    {
        var m = new ChestTrackManager();
        var frames = new List<Detection[]> { F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestOpening)) };
        frames.AddRange(Enumerable.Repeat(F(), 30)); // gone (looked away, chest destroyed, out of view)
        Assert.Empty(Run(m, frames.ToArray()));
        Assert.Empty(m.Tracks);
    }

    [Fact]
    public void An_already_open_chest_is_never_counted()
    {
        var m = new ChestTrackManager();
        Assert.Empty(Run(m, Enumerable.Repeat(F(D(ObjectRole.ChestOpened, 0.95f)), 20).ToArray()));
        Assert.Equal(ChestState.Opened, Assert.Single(m.Tracks).State);
    }

    [Fact]
    public void Opening_seen_in_a_single_frame_is_not_enough()
    {
        var m = new ChestTrackManager();
        var events = Run(m, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestOpening)), F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened)));
        // Closed → (1 opening frame) → opened: real transition but weak evidence → review only.
        var ev = Assert.Single(events);
        Assert.NotEqual(DetectionOutcome.Confirmed, ev.Outcome);
    }

    [Fact]
    public void Closed_then_opened_without_animation_goes_to_review()
    {
        var m = new ChestTrackManager();
        var ev = Assert.Single(Run(m, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)),
            F(D(ObjectRole.ChestOpened, 0.9f)), F(D(ObjectRole.ChestOpened, 0.9f))));
        Assert.Equal(DetectionOutcome.Uncertain, ev.Outcome);
    }

    private sealed class FixedDetector(ModelLabels labels) : IObjectDetector
    {
        public ModelLabels Labels { get; } = labels;
        public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride) => [];
        public void Dispose() { }
    }

    [Fact]
    public void Two_class_model_counts_closed_then_opened_once_and_still_ignores_already_open_chests()
    {
        // e.g. a model trained with names {0: chest_closed, 1: chest_opened} and no opening class.
        using var pipeline = new YoloChestPipeline(new FixedDetector(new ModelLabels(["chest_closed", "chest_opened"])));
        Assert.False(pipeline.Options.ModelHasOpeningClass);
        pipeline.Options = new ChestTrackingOptions();          // settings change keeps the model adaptation
        Assert.False(pipeline.Options.ModelHasOpeningClass);
        var m = pipeline.Tracker;

        var ev = Assert.Single(Run(m, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)),
            F(D(ObjectRole.ChestOpened, 0.9f)), F(D(ObjectRole.ChestOpened, 0.8f)),
            F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened))));
        Assert.Equal(DetectionOutcome.Confirmed, ev.Outcome);
        Assert.Equal(0.85, ev.Confidence, 2);
        Assert.Equal([ChestState.Closed, ChestState.Opened, ChestState.Counted], ev.Evidence.Transitions.Select(t => t.To));

        var fresh = new YoloChestPipeline(new FixedDetector(new ModelLabels(["chest_closed", "chest_opened"]))).Tracker;
        Assert.Empty(Run(fresh, Enumerable.Repeat(F(D(ObjectRole.ChestOpened, 0.95f)), 20).ToArray()));
        Assert.Empty(Run(fresh, F(), F(), F()));
        var single = new YoloChestPipeline(new FixedDetector(new ModelLabels(["chest_closed", "chest_opened"]))).Tracker;
        Assert.Empty(Run(single, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestOpened)))); // one open frame is not enough
    }

    [Fact]
    public void Model_with_opening_class_keeps_closed_then_opened_as_review_only()
    {
        using var pipeline = new YoloChestPipeline(new FixedDetector(new ModelLabels(ModelLabels.Default)));
        Assert.True(pipeline.Options.ModelHasOpeningClass);
        var ev = Assert.Single(Run(pipeline.Tracker, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)),
            F(D(ObjectRole.ChestOpened, 0.9f)), F(D(ObjectRole.ChestOpened, 0.9f))));
        Assert.Equal(DetectionOutcome.Uncertain, ev.Outcome);
    }

    [Fact]
    public void Ammo_boxes_and_other_containers_are_never_counted()
    {
        var m = new ChestTrackManager();
        var ammo = new Detection(3, "ammo_box", ObjectRole.OtherContainer, 0.95f, ChestBox);
        var update = m.Update([ammo], T0);
        Assert.Single(update.Containers);
        Assert.Empty(Run(m, Enumerable.Repeat(F(ammo), 20).ToArray()));
        Assert.Empty(m.Tracks);
    }

    [Fact]
    public void Low_confidence_openings_are_discarded_and_medium_ones_reviewed()
    {
        var permissive = new ChestTrackingOptions { MinClosedConfidence = 0, MinOpenedConfidence = 0 };
        var low = Run(new ChestTrackManager(permissive), F(D(ObjectRole.ChestClosed, 0.5f)), F(D(ObjectRole.ChestClosed, 0.5f)),
            F(D(ObjectRole.ChestOpening, 0.36f)), F(D(ObjectRole.ChestOpening, 0.36f)), F(D(ObjectRole.ChestOpened, 0.36f)), F(D(ObjectRole.ChestOpened, 0.36f)));
        Assert.Equal(DetectionOutcome.Discarded, Assert.Single(low).Outcome);

        // With the default per-class floors (0.40) such weak detections are not used at all.
        Assert.Empty(Run(new ChestTrackManager(), F(D(ObjectRole.ChestClosed, 0.5f)), F(D(ObjectRole.ChestClosed, 0.5f)),
            F(D(ObjectRole.ChestOpening, 0.36f)), F(D(ObjectRole.ChestOpening, 0.36f)), F(D(ObjectRole.ChestOpened, 0.36f)), F(D(ObjectRole.ChestOpened, 0.36f))));

        var mid = Run(new ChestTrackManager(), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)),
            F(D(ObjectRole.ChestOpening, 0.5f)), F(D(ObjectRole.ChestOpening, 0.5f)), F(D(ObjectRole.ChestOpened, 0.5f)), F(D(ObjectRole.ChestOpened, 0.5f)));
        Assert.Equal(DetectionOutcome.Uncertain, Assert.Single(mid).Outcome);
    }

    [Fact]
    public void Interrupted_opening_returns_to_closed_and_can_be_counted_later_once()
    {
        var m = new ChestTrackManager();
        var events = Run(m,
            F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestOpening)), F(D(ObjectRole.ChestOpening)),
            F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), // walked away mid-search
            F(D(ObjectRole.ChestOpening)), F(D(ObjectRole.ChestOpening)), F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened)));
        var ev = Assert.Single(events);
        Assert.Equal(DetectionOutcome.Confirmed, ev.Outcome);
        Assert.Contains(ev.Evidence.Transitions, t => t.Reason == "opening interrupted");
    }

    [Fact]
    public void Lost_and_recreated_track_does_not_count_the_same_chest_twice()
    {
        var m = new ChestTrackManager(new ChestTrackingOptions { MaxMissedFrames = 1 });
        var first = Run(m, F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestClosed)), F(D(ObjectRole.ChestOpening)), F(D(ObjectRole.ChestOpening)),
            F(D(ObjectRole.ChestOpened)), F(D(ObjectRole.ChestOpened)));
        Assert.Single(first);

        // Track lost (occluded), then the detector flickers back to "closed/opening/opened" at the same place.
        var later = new List<ChestOpenEvent>();
        var t = T0 + TimeSpan.FromSeconds(2);
        foreach (var role in new[] { ObjectRole.ChestClosed, ObjectRole.ChestOpening, ObjectRole.ChestOpening, ObjectRole.ChestOpened, ObjectRole.ChestOpened })
        {
            later.AddRange(m.Update([D(role)], t).Events);
            t += TimeSpan.FromMilliseconds(125);
        }

        Assert.Empty(later);
        Assert.All(m.Tracks, tr => Assert.Equal(ChestState.Counted, tr.State));
    }

    [Fact]
    public void Two_chests_side_by_side_are_tracked_and_counted_separately()
    {
        var left = new Box(100, 400, 200, 480);
        var right = new Box(900, 400, 1000, 480);
        var m = new ChestTrackManager();
        var events = Run(m,
            F(D(ObjectRole.ChestClosed, box: left), D(ObjectRole.ChestClosed, box: right)),
            F(D(ObjectRole.ChestClosed, box: left), D(ObjectRole.ChestClosed, box: right)),
            F(D(ObjectRole.ChestOpening, box: left), D(ObjectRole.ChestClosed, box: right)),
            F(D(ObjectRole.ChestOpening, box: left), D(ObjectRole.ChestClosed, box: right)),
            F(D(ObjectRole.ChestOpened, box: left), D(ObjectRole.ChestOpening, box: right)),
            F(D(ObjectRole.ChestOpened, box: left), D(ObjectRole.ChestOpening, box: right)),
            F(D(ObjectRole.ChestOpened, box: left), D(ObjectRole.ChestOpened, box: right)),
            F(D(ObjectRole.ChestOpened, box: left), D(ObjectRole.ChestOpened, box: right)));
        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].TrackId, events[1].TrackId);
    }
}

public class DetectionMetricsTests
{
    [Fact]
    public void Event_metrics_match_one_to_one_within_tolerance()
    {
        static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        var m = DetectionMetrics.EvaluateEvents([S(10.4), S(10.9), S(31), S(80)], [S(10), S(30), S(55)], S(2));
        Assert.Equal(2, m.TruePositives);   // 10.4↔10, 31↔30
        Assert.Equal(2, m.FalsePositives);  // 10.9 (double count), 80
        Assert.Equal(1, m.FalseNegatives);  // 55 missed
        Assert.Equal(0.5, m.Precision);
        Assert.Equal(2 / 3.0, m.Recall, 3);
        Assert.Equal([S(55)], m.MissedTimes);
    }

    [Fact]
    public void Frame_metrics_count_per_class_at_iou_half()
    {
        var labels = new ModelLabels(ModelLabels.Default);
        var truth = DetectionMetrics.ParseYoloLabels(["0 0.5 0.5 0.1 0.1", "3 0.2 0.2 0.1 0.1"], 1000, 1000);
        Detection[] predicted =
        [
            new(0, "chest_closed", ObjectRole.ChestClosed, 0.9f, new Box(452, 452, 552, 552)), // TP
            new(1, "chest_opening", ObjectRole.ChestOpening, 0.6f, new Box(700, 700, 750, 750)), // FP
        ];
        var result = DetectionMetrics.EvaluateFrames([(predicted, truth)], labels);
        Assert.Equal((1, 0, 0), (result[0].TruePositives, result[0].FalsePositives, result[0].FalseNegatives));
        Assert.Equal(1, result[1].FalsePositives);
        Assert.Equal(1, result[3].FalseNegatives); // ammo box missed
    }

    [Fact]
    public void Event_times_parse_seconds_and_minutes()
    {
        var t = DetectionMetrics.ParseEventTimes(["# chest openings", "12.5", "01:03.5  # second", "", "2:00"]);
        Assert.Equal([TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(63.5), TimeSpan.FromSeconds(120)], t);
    }
}

/// <summary>Runs the real ONNX Runtime path with a tiny hand-written model whose output is a constant YOLO head.</summary>
public class OnnxRuntimeIntegrationTests
{
    [Fact]
    public void Onnx_model_is_loaded_and_its_output_decoded_through_the_pipeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct-yolo-{Guid.NewGuid():N}.onnx");
        // 3 anchors, 5 classes: opening chest at the center, ammo box top-left, one empty anchor.
        var head = new float[9 * 3];
        void Set(int anchor, float cx, float cy, float w, float h, int cls, float score)
        {
            head[anchor] = cx;
            head[3 + anchor] = cy;
            head[6 + anchor] = w;
            head[9 + anchor] = h;
            head[((4 + cls) * 3) + anchor] = score;
        }

        Set(0, 320, 320, 100, 80, 1, 0.9f);
        Set(2, 100, 200, 40, 40, 3, 0.8f);
        File.WriteAllBytes(path, TinyOnnx.ConstantModel(head, [1, 9, 3],
            "{0: 'chest_closed', 1: 'chest_opening', 2: 'chest_opened', 3: 'ammo_box', 4: 'other_container'}"));
        try
        {
            using var detector = OnnxYoloDetector.Load(path, threads: 1);
            Assert.Equal(640, detector.Info.InputSize);
            Assert.Equal("model metadata", detector.Info.LabelSource);
            Assert.Equal("chest_opening", detector.Labels.Name(1));

            var frame = new byte[1280 * 720 * 4];
            var dets = detector.Detect(frame, 1280, 720, 1280 * 4);
            Assert.Equal(2, dets.Count);
            var chest = dets.Single(d => d.IsChest);
            Assert.Equal(ObjectRole.ChestOpening, chest.Role);
            Assert.Equal(new Box(540, 280, 740, 440), chest.Box); // mapped back through the letterbox
            Assert.Equal(ObjectRole.OtherContainer, dets.Single(d => !d.IsChest).Role);

            using var pipeline = new YoloChestPipeline(OnnxYoloDetector.Load(path, threads: 1));
            var result = pipeline.Process(frame, 1280, 720, 1280 * 4, DateTimeOffset.UtcNow);
            Assert.Single(result.Update.Tracks);
            Assert.Single(result.Update.Containers);
            Assert.Empty(result.Update.Events); // opening without a closed chest first: not counted
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_model_fails_clearly()
    {
        Assert.Throws<FileNotFoundException>(() => OnnxYoloDetector.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.onnx")));
    }
}

/// <summary>Minimal protobuf writer for an ONNX model: input "images" [1,3,640,640], output = Constant tensor.</summary>
internal static class TinyOnnx
{
    public static byte[] ConstantModel(float[] values, long[] dims, string names)
    {
        var raw = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        var tensor = Msg(b =>
        {
            foreach (var d in dims) Int(b, 1, d);
            Int(b, 2, 1); // FLOAT
            Bytes(b, 9, raw);
        });
        var attribute = Msg(b =>
        {
            Str(b, 1, "value");
            Bytes(b, 5, tensor);
            Int(b, 20, 4); // AttributeType.TENSOR
        });
        var node = Msg(b =>
        {
            Str(b, 2, "output0");
            Str(b, 3, "head");
            Str(b, 4, "Constant");
            Bytes(b, 5, attribute);
        });
        var graph = Msg(b =>
        {
            Bytes(b, 1, node);
            Str(b, 2, "tiny-yolo");
            Bytes(b, 11, ValueInfo("images", [1, 3, 640, 640]));
            Bytes(b, 12, ValueInfo("output0", dims));
        });
        return Msg(b =>
        {
            Int(b, 1, 8); // ir_version
            Str(b, 2, "chesttracker-tests");
            Bytes(b, 7, graph);
            Bytes(b, 8, Msg(o => Int(o, 2, 13))); // opset 13
            Bytes(b, 14, Msg(p =>
            {
                Str(p, 1, "names");
                Str(p, 2, names);
            }));
        });
    }

    private static byte[] ValueInfo(string name, long[] dims) => Msg(b =>
    {
        Str(b, 1, name);
        Bytes(b, 2, Msg(type => Bytes(type, 1, Msg(t =>
        {
            Int(t, 1, 1); // FLOAT
            Bytes(t, 2, Msg(shape =>
            {
                foreach (var d in dims) Bytes(shape, 1, Msg(dim => Int(dim, 1, d)));
            }));
        }))));
    });

    private static byte[] Msg(Action<List<byte>> build)
    {
        var b = new List<byte>();
        build(b);
        return b.ToArray();
    }

    private static void Varint(List<byte> b, ulong v)
    {
        while (v >= 0x80)
        {
            b.Add((byte)(v | 0x80));
            v >>= 7;
        }

        b.Add((byte)v);
    }

    private static void Int(List<byte> b, int field, long v)
    {
        Varint(b, (ulong)(field << 3));
        Varint(b, (ulong)v);
    }

    private static void Bytes(List<byte> b, int field, byte[] data)
    {
        Varint(b, (ulong)((field << 3) | 2));
        Varint(b, (ulong)data.Length);
        b.AddRange(data);
    }

    private static void Str(List<byte> b, int field, string s) => Bytes(b, field, Encoding.UTF8.GetBytes(s));
}
