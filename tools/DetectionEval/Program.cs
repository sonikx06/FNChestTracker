using System.Globalization;
using System.Runtime.InteropServices;
using ChestTracker.Core.Detection;
using ChestTracker.Vision;
using OpenCvSharp;

// Evaluates a YOLOv8 chest model:
//   video  --model m.onnx --video match.mp4 --events events.txt [--fps 8] [--tolerance 2] [--conf 0.35] [--csv out.csv]
//   frames --model m.onnx --images dir --labels dir [--conf 0.25]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is not ("video" or "frames"))
{
    Console.Error.WriteLine("usage: DetectionEval video --model m.onnx --video match.mp4 --events events.txt [--fps 8] [--tolerance 2] [--conf 0.35] [--csv out.csv]");
    Console.Error.WriteLine("       DetectionEval frames --model m.onnx --images dir --labels dir [--conf 0.25]");
    return 2;
}

var options = ParseOptions(args.Skip(1).ToArray());
string Req(string key) => options.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");
double Num(string key, double fallback) => options.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

using var detector = OnnxYoloDetector.Load(Req("model"), Environment.ProcessorCount);
Console.WriteLine($"model: {detector.Info.Path} · {detector.Info.InputSize}px · classes: {string.Join(", ", detector.Labels.Names)} ({detector.Info.LabelSource})");

return args[0] == "video" ? EvaluateVideo() : EvaluateFrames();

int EvaluateVideo()
{
    detector.ConfidenceThreshold = (float)Num("conf", 0.35);
    var truth = DetectionMetrics.ParseEventTimes(File.ReadAllLines(Req("events")));
    var fps = Num("fps", 8);
    var tolerance = TimeSpan.FromSeconds(Num("tolerance", 2));
    var pipeline = new YoloChestPipeline(detector, new ChestTrackingOptions { MinDetectionConfidence = detector.ConfidenceThreshold });

    using var capture = new VideoCapture(Req("video"));
    if (!capture.IsOpened())
    {
        Console.Error.WriteLine("cannot open video");
        return 1;
    }

    var videoFps = capture.Fps > 0 ? capture.Fps : 30;
    var step = Math.Max(1, (int)Math.Round(videoFps / fps));
    var origin = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var events = new List<(TimeSpan Time, ChestOpenEvent Event)>();
    using var frame = new Mat();
    using var bgra = new Mat();
    var index = 0;
    var processed = 0;
    double inferenceMs = 0;
    byte[] buffer = [];
    while (capture.Read(frame) && !frame.Empty())
    {
        if (index++ % step != 0)
        {
            continue;
        }

        var time = TimeSpan.FromSeconds((index - 1) / videoFps);
        Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
        var size = bgra.Width * bgra.Height * 4;
        if (buffer.Length != size)
        {
            buffer = new byte[size];
        }

        Marshal.Copy(bgra.Data, buffer, 0, size);
        var result = pipeline.Process(buffer, bgra.Width, bgra.Height, bgra.Width * 4, origin + time);
        inferenceMs += result.ElapsedMs;
        processed++;
        foreach (var ev in result.Update.Events)
        {
            events.Add((time, ev));
            Console.WriteLine($"{Fmt(time)}  {ev.Outcome,-9} conf={ev.Confidence:0.00}  {ev.Evidence}");
        }
    }

    var counted = events.Where(e => e.Event.Outcome == DetectionOutcome.Confirmed).Select(e => e.Time).ToList();
    var withReview = events.Where(e => e.Event.Outcome != DetectionOutcome.Discarded).Select(e => e.Time).ToList();
    var m = DetectionMetrics.EvaluateEvents(counted, truth, tolerance);
    var mr = DetectionMetrics.EvaluateEvents(withReview, truth, tolerance);

    Console.WriteLine();
    Console.WriteLine($"frames analysed: {processed} at {fps:0.#} fps · mean {inferenceMs / Math.Max(1, processed):0} ms/frame");
    Console.WriteLine($"annotated openings: {truth.Count} · tolerance ±{tolerance.TotalSeconds:0.#} s");
    Console.WriteLine($"counted automatically : TP {m.TruePositives}  FP {m.FalsePositives}  FN {m.FalseNegatives}  precision {m.Precision:P1}  recall {m.Recall:P1}");
    Console.WriteLine($"counted + review queue: TP {mr.TruePositives}  FP {mr.FalsePositives}  FN {mr.FalseNegatives}  precision {mr.Precision:P1}  recall {mr.Recall:P1}");
    if (m.FalsePositiveTimes.Count > 0) Console.WriteLine("false positives at: " + string.Join(", ", m.FalsePositiveTimes.Select(Fmt)));
    if (m.MissedTimes.Count > 0) Console.WriteLine("missed openings at: " + string.Join(", ", m.MissedTimes.Select(Fmt)));

    if (options.TryGetValue("csv", out var csv))
    {
        File.WriteAllLines(csv, new[] { "time_s,outcome,confidence,evidence" }.Concat(events.Select(e =>
            $"{e.Time.TotalSeconds:0.00},{e.Event.Outcome},{e.Event.Confidence:0.000},\"{e.Event.Evidence}\"")));
        Console.WriteLine($"events written to {csv}");
    }

    return 0;
}

int EvaluateFrames()
{
    detector.ConfidenceThreshold = (float)Num("conf", 0.25);
    var images = Directory.EnumerateFiles(Req("images"))
        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f)
        .ToList();
    var labelsDir = Req("labels");
    var frames = new List<(IReadOnlyList<Detection>, IReadOnlyList<GroundTruthBox>)>();
    using var bgra = new Mat();
    foreach (var file in images)
    {
        using var img = Cv2.ImRead(file, ImreadModes.Color);
        Cv2.CvtColor(img, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[bgra.Width * bgra.Height * 4];
        Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        var predicted = detector.Detect(bytes, bgra.Width, bgra.Height, bgra.Width * 4);
        var labelFile = Path.Combine(labelsDir, Path.GetFileNameWithoutExtension(file) + ".txt");
        var truth = File.Exists(labelFile) ? DetectionMetrics.ParseYoloLabels(File.ReadAllLines(labelFile), bgra.Width, bgra.Height) : [];
        frames.Add((predicted, truth));
    }

    Console.WriteLine($"images: {images.Count} · IoU 0.5 · conf ≥ {detector.ConfidenceThreshold:0.00}");
    Console.WriteLine($"{"class",-18}{"TP",6}{"FP",6}{"FN",6}{"precision",11}{"recall",9}");
    foreach (var c in DetectionMetrics.EvaluateFrames(frames, detector.Labels))
    {
        Console.WriteLine($"{c.Label,-18}{c.TruePositives,6}{c.FalsePositives,6}{c.FalseNegatives,6}{c.Precision,11:P1}{c.Recall,9:P1}");
    }

    return 0;
}

static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length - 1; i += 2)
    {
        if (!a[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"unexpected '{a[i]}'");
        }

        d[a[i][2..]] = a[i + 1];
    }

    return d;
}
