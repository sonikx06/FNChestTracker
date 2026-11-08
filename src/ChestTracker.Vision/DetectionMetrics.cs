using System.Globalization;

namespace ChestTracker.Vision;

public sealed record GroundTruthBox(int ClassId, Box Box);

public sealed record ClassMetrics(int ClassId, string Label, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : TruePositives / (double)(TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0 ? 0 : TruePositives / (double)(TruePositives + FalseNegatives);
}

/// <summary>Counting quality on footage: predicted chest openings vs. annotated openings.</summary>
public sealed record EventMetrics(
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    IReadOnlyList<TimeSpan> FalsePositiveTimes,
    IReadOnlyList<TimeSpan> MissedTimes)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : TruePositives / (double)(TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0 ? 0 : TruePositives / (double)(TruePositives + FalseNegatives);
}

public static class DetectionMetrics
{
    /// <summary>
    /// Frame-level detection metrics per class at an IoU threshold (greedy one-to-one matching by confidence).
    /// A prediction of the wrong class is a false positive for its class and the box a miss for the true class.
    /// </summary>
    public static IReadOnlyList<ClassMetrics> EvaluateFrames(
        IEnumerable<(IReadOnlyList<Detection> Predicted, IReadOnlyList<GroundTruthBox> Truth)> frames, ModelLabels labels, float iou = 0.5f)
    {
        var tp = new int[labels.Count];
        var fp = new int[labels.Count];
        var fn = new int[labels.Count];
        foreach (var (predicted, truth) in frames)
        {
            var matched = new bool[truth.Count];
            foreach (var p in predicted.OrderByDescending(p => p.Confidence))
            {
                var best = -1;
                var bestIoU = iou;
                for (var i = 0; i < truth.Count; i++)
                {
                    if (matched[i] || truth[i].ClassId != p.ClassId)
                    {
                        continue;
                    }

                    var v = truth[i].Box.IoU(p.Box);
                    if (v >= bestIoU)
                    {
                        bestIoU = v;
                        best = i;
                    }
                }

                if (p.ClassId < 0 || p.ClassId >= labels.Count)
                {
                    continue;
                }

                if (best >= 0)
                {
                    matched[best] = true;
                    tp[p.ClassId]++;
                }
                else
                {
                    fp[p.ClassId]++;
                }
            }

            for (var i = 0; i < truth.Count; i++)
            {
                if (!matched[i] && truth[i].ClassId >= 0 && truth[i].ClassId < labels.Count)
                {
                    fn[truth[i].ClassId]++;
                }
            }
        }

        return Enumerable.Range(0, labels.Count).Select(c => new ClassMetrics(c, labels.Name(c), tp[c], fp[c], fn[c])).ToList();
    }

    /// <summary>
    /// Matches predicted openings to annotated ones within <paramref name="tolerance"/> (one-to-one, nearest first).
    /// Unmatched predictions are false positives (over-counting), unmatched annotations are misses.
    /// </summary>
    public static EventMetrics EvaluateEvents(IReadOnlyList<TimeSpan> predicted, IReadOnlyList<TimeSpan> truth, TimeSpan tolerance)
    {
        var pairs = new List<(TimeSpan Distance, int P, int T)>();
        for (var p = 0; p < predicted.Count; p++)
        {
            for (var t = 0; t < truth.Count; t++)
            {
                var d = (predicted[p] - truth[t]).Duration();
                if (d <= tolerance)
                {
                    pairs.Add((d, p, t));
                }
            }
        }

        var usedP = new bool[predicted.Count];
        var usedT = new bool[truth.Count];
        var tp = 0;
        foreach (var (_, p, t) in pairs.OrderBy(x => x.Distance))
        {
            if (!usedP[p] && !usedT[t])
            {
                usedP[p] = usedT[t] = true;
                tp++;
            }
        }

        var falsePositives = predicted.Where((_, i) => !usedP[i]).ToList();
        var missed = truth.Where((_, i) => !usedT[i]).ToList();
        return new EventMetrics(tp, falsePositives.Count, missed.Count, falsePositives, missed);
    }

    /// <summary>Parses a YOLO label file ("class cx cy w h", normalized 0..1) into pixel boxes.</summary>
    public static IReadOnlyList<GroundTruthBox> ParseYoloLabels(IEnumerable<string> lines, int imageWidth, int imageHeight)
    {
        var result = new List<GroundTruthBox>();
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cls))
            {
                continue;
            }

            var v = parts.Skip(1).Take(4).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            result.Add(new GroundTruthBox(cls, Box.FromCenter(v[0] * imageWidth, v[1] * imageHeight, v[2] * imageWidth, v[3] * imageHeight)));
        }

        return result;
    }

    /// <summary>Annotated openings: one time per line, seconds ("12.5") or "mm:ss.f"; '#' starts a comment.</summary>
    public static IReadOnlyList<TimeSpan> ParseEventTimes(IEnumerable<string> lines)
    {
        var result = new List<TimeSpan>();
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Split(',')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                result.Add(TimeSpan.FromSeconds(seconds));
            }
            else if (line.Split(':') is [var m, var s]
                     && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                     && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                result.Add(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(sec));
            }
            else
            {
                throw new FormatException($"Cannot read time '{raw}'.");
            }
        }

        return result.OrderBy(t => t).ToList();
    }
}
