namespace ChestTracker.Vision;

/// <summary>How a source frame was scaled and padded into the square model input (Ultralytics letterbox).</summary>
public readonly record struct Letterbox(int SourceWidth, int SourceHeight, int InputSize, float Scale, float PadX, float PadY)
{
    public static Letterbox For(int sourceWidth, int sourceHeight, int inputSize)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || inputSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Frame and input sizes must be positive.");
        }

        var scale = Math.Min(inputSize / (float)sourceWidth, inputSize / (float)sourceHeight);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return new Letterbox(sourceWidth, sourceHeight, inputSize, scale, (inputSize - w) / 2, (inputSize - h) / 2);
    }

    /// <summary>Maps a box from model-input pixels back to source pixels, clamped to the frame.</summary>
    public Box ToSource(Box input)
    {
        var (padX, padY, scale, w, h) = (PadX, PadY, Scale, SourceWidth, SourceHeight);
        float X(float v) => Math.Clamp((v - padX) / scale, 0, w);
        float Y(float v) => Math.Clamp((v - padY) / scale, 0, h);
        return new Box(X(input.X1), Y(input.Y1), X(input.X2), Y(input.Y2));
    }
}

public static class YoloPreprocessing
{
    /// <summary>Gray padding value Ultralytics uses (114/255).</summary>
    public const float PadValue = 114f / 255f;

    /// <summary>
    /// Letterboxes a BGRA frame into a 1×3×S×S float tensor (RGB, 0..1, CHW) with bilinear sampling.
    /// </summary>
    /// <param name="destination">Length 3·S·S; reused between frames to avoid allocations.</param>
    public static Letterbox ToTensor(ReadOnlySpan<byte> bgra, int width, int height, int stride, int inputSize, Span<float> destination)
    {
        var plane = inputSize * inputSize;
        if (destination.Length < 3 * plane)
        {
            throw new ArgumentException("Destination tensor is too small.", nameof(destination));
        }

        if (stride < width * 4 || bgra.Length < ((height - 1) * stride) + (width * 4))
        {
            throw new ArgumentException("Pixel buffer does not match the frame size.", nameof(bgra));
        }

        var lb = Letterbox.For(width, height, inputSize);
        destination[..(3 * plane)].Fill(PadValue);

        var x0 = (int)Math.Round(lb.PadX);
        var y0 = (int)Math.Round(lb.PadY);
        var w = Math.Min(inputSize - x0, (int)Math.Round(width * lb.Scale));
        var h = Math.Min(inputSize - y0, (int)Math.Round(height * lb.Scale));
        var inv = 1f / lb.Scale;

        for (var y = 0; y < h; y++)
        {
            var sy = Math.Clamp(((y + 0.5f) * inv) - 0.5f, 0, height - 1);
            var yA = (int)sy;
            var yB = Math.Min(yA + 1, height - 1);
            var fy = sy - yA;
            var rowA = yA * stride;
            var rowB = yB * stride;
            var outRow = ((y + y0) * inputSize) + x0;

            for (var x = 0; x < w; x++)
            {
                var sx = Math.Clamp(((x + 0.5f) * inv) - 0.5f, 0, width - 1);
                var xA = (int)sx;
                var xB = Math.Min(xA + 1, width - 1);
                var fx = sx - xA;

                var a = rowA + (xA * 4);
                var b = rowA + (xB * 4);
                var c = rowB + (xA * 4);
                var d = rowB + (xB * 4);
                var o = outRow + x;
                destination[o] = Sample(bgra, a, b, c, d, fx, fy, 2) / 255f;               // R
                destination[plane + o] = Sample(bgra, a, b, c, d, fx, fy, 1) / 255f;       // G
                destination[(2 * plane) + o] = Sample(bgra, a, b, c, d, fx, fy, 0) / 255f; // B
            }
        }

        return lb;
    }

    private static float Sample(ReadOnlySpan<byte> px, int a, int b, int c, int d, float fx, float fy, int channel) =>
        ((1 - fy) * (((1 - fx) * px[a + channel]) + (fx * px[b + channel])))
        + (fy * (((1 - fx) * px[c + channel]) + (fx * px[d + channel])));
}

/// <summary>
/// Decodes the raw YOLOv8 detection head (export without NMS, end2end=False): [1, 4 + classes, anchors]
/// (Ultralytics default, e.g. [1, 6, 8400] for 2 classes at 640 px) or the transposed [1, anchors, 4 + classes].
/// Boxes are (cx, cy, w, h) in input pixels; class scores are already sigmoid-activated. Applies the confidence
/// threshold and class-aware NMS; overlapping boxes of different chest classes are resolved by the tracker.
/// </summary>
public static class YoloDecoder
{
    /// <summary>Classes of a [1, a, b] head: the smaller of a and b is 4 + classes. -1 when the shape cannot tell.</summary>
    public static int ClassCount(IReadOnlyList<int> dims) =>
        dims.Count == 3 && dims[1] > 0 && dims[2] > 0 && dims[1] != dims[2] && Math.Min(dims[1], dims[2]) > 4
            ? Math.Min(dims[1], dims[2]) - 4
            : -1;

    /// <summary>True when the head is [1, 4 + classes, anchors].</summary>
    public static bool IsChannelsFirst(IReadOnlyList<int> shape, int labelCount)
    {
        var first = shape[1] == 4 + labelCount;
        var second = shape[2] == 4 + labelCount;
        if (first != second)
        {
            return first;
        }

        // Neither (or both) match the label count: anchors are always the larger dimension.
        return shape[1] <= shape[2];
    }

    public static List<Detection> Decode(ReadOnlySpan<float> output, int[] shape, ModelLabels labels, Letterbox letterbox,
        float confidenceThreshold, float iouThreshold, int maxDetections = 100)
    {
        if (shape.Length != 3 || shape[0] != 1)
        {
            throw new ArgumentException($"Unexpected YOLO output shape [{string.Join(",", shape)}].", nameof(shape));
        }

        var channelsFirst = IsChannelsFirst(shape, labels.Count);
        var channels = channelsFirst ? shape[1] : shape[2];
        var anchors = channelsFirst ? shape[2] : shape[1];
        var classes = channels - 4;
        if (classes <= 0 || output.Length < channels * anchors)
        {
            throw new ArgumentException("YOLO output does not match its shape.", nameof(output));
        }

        var candidates = new List<Detection>();
        for (var i = 0; i < anchors; i++)
        {
            var best = -1;
            var bestScore = confidenceThreshold;
            for (var c = 0; c < classes; c++)
            {
                var score = At(output, channelsFirst, anchors, channels, i, 4 + c);
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            if (best < 0)
            {
                continue;
            }

            var input = Box.FromCenter(At(output, channelsFirst, anchors, channels, i, 0), At(output, channelsFirst, anchors, channels, i, 1), At(output, channelsFirst, anchors, channels, i, 2), At(output, channelsFirst, anchors, channels, i, 3));
            var box = letterbox.ToSource(input);
            if (box.Width < 1 || box.Height < 1)
            {
                continue;
            }

            candidates.Add(new Detection(best, labels.Name(best), labels.Role(best), bestScore, box));
        }

        return Nms(candidates, iouThreshold, maxDetections);
    }

    private static float At(ReadOnlySpan<float> output, bool channelsFirst, int anchors, int channels, int anchor, int channel) =>
        channelsFirst ? output[(channel * anchors) + anchor] : output[(anchor * channels) + channel];

    /// <summary>Class-aware NMS: a box only suppresses lower-scoring boxes of the same class.</summary>
    public static List<Detection> Nms(List<Detection> detections, float iouThreshold, int maxDetections = 100)
    {
        detections.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var kept = new List<Detection>();
        foreach (var d in detections)
        {
            if (kept.Count >= maxDetections)
            {
                break;
            }

            if (kept.All(k => k.ClassId != d.ClassId || k.Box.IoU(d.Box) < iouThreshold))
            {
                kept.Add(d);
            }
        }

        return kept;
    }
}
