using System.Diagnostics;
using System.IO;
using ChestTracker.Core.Detection;
using ChestTracker.Core.Settings;
using OpenCvSharp;

namespace ChestTracker.App.Capture;

public sealed record PreviewImage(byte[] Bgra, int Width, int Height);

public sealed record FrameAnalysis(FrameFeatures Features, PreviewImage? Preview, double ElapsedMs, int SourceWidth, int SourceHeight);

/// <summary>
/// EXPERIMENTAL. Extracts <see cref="FrameFeatures"/> from a frame with OpenCV:
/// the region around the crosshair is downscaled, converted to HSV and measured for
/// gold pixels, brightness and motion; an optional prompt template is matched in its own region.
/// Everything runs on small images (≤ 256 px wide) to keep CPU usage low.
/// </summary>
public sealed class FrameAnalyzer : IDisposable
{
    private const int WorkWidth = 256;
    private const int PromptWorkWidth = 200;

    private readonly object _templateGate = new();
    private Mat? _previousGray;
    private Mat? _template;

    public bool HasTemplate
    {
        get { lock (_templateGate) return _template is not null; }
    }

    public FrameAnalysis Analyze(CapturedFrame frame, DetectionSettings settings, bool wantPreview)
    {
        var sw = Stopwatch.StartNew();
        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels, frame.Stride);

        var roi = RelativeRect(frame.Width, frame.Height,
            0.5 - (settings.RoiWidth / 2), settings.RoiCenterY - (settings.RoiHeight / 2), settings.RoiWidth, settings.RoiHeight);

        using var roiView = new Mat(source, roi);
        var workHeight = Math.Max(8, (int)Math.Round(roi.Height * (WorkWidth / (double)roi.Width)));
        using var small = new Mat();
        Cv2.Resize(roiView, small, new Size(WorkWidth, workHeight), 0, 0, InterpolationFlags.Area);

        using var bgr = new Mat();
        Cv2.CvtColor(small, bgr, ColorConversionCodes.BGRA2BGR);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var goldMask = new Mat();
        Cv2.InRange(hsv,
            new Scalar(settings.GoldHueMin, settings.GoldSaturationMin, settings.GoldValueMin),
            new Scalar(settings.GoldHueMax, 255, 255),
            goldMask);
        var goldRatio = Cv2.CountNonZero(goldMask) / (double)(goldMask.Rows * goldMask.Cols);

        var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var brightness = Cv2.Mean(gray).Val0;

        double motion = 0;
        if (_previousGray is not null && _previousGray.Size() == gray.Size())
        {
            using var diff = new Mat();
            Cv2.Absdiff(gray, _previousGray, diff);
            motion = Cv2.Mean(diff).Val0 / 255.0;
        }

        _previousGray?.Dispose();
        _previousGray = gray;

        double? templateScore = null;
        if (settings.UseTemplate)
        {
            lock (_templateGate)
            {
                if (_template is not null)
                {
                    using var prompt = ExtractPromptGray(source, settings);
                    if (prompt.Width >= _template.Width && prompt.Height >= _template.Height)
                    {
                        using var result = new Mat();
                        Cv2.MatchTemplate(prompt, _template, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double max);
                        templateScore = double.IsFinite(max) ? max : 0;
                    }
                }
            }
        }

        PreviewImage? preview = null;
        if (wantPreview)
        {
            preview = BuildPreview(small, goldMask);
        }

        sw.Stop();
        var features = new FrameFeatures(frame.Timestamp, goldRatio, brightness, motion, templateScore);
        return new FrameAnalysis(features, preview, sw.Elapsed.TotalMilliseconds, frame.Width, frame.Height);
    }

    /// <summary>Stores the central part of the current prompt region as the template (grayscale PNG).</summary>
    public void CaptureTemplate(CapturedFrame frame, DetectionSettings settings, string path)
    {
        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels, frame.Stride);
        using var prompt = ExtractPromptGray(source, settings);
        // Keep the centre 80 % so matching tolerates small shifts of the prompt.
        var inner = new Rect((int)(prompt.Width * 0.1), (int)(prompt.Height * 0.1),
            Math.Max(4, (int)(prompt.Width * 0.8)), Math.Max(4, (int)(prompt.Height * 0.8)));
        using var crop = new Mat(prompt, inner);
        var template = crop.Clone();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cv2.ImWrite(path, template);
        lock (_templateGate)
        {
            _template?.Dispose();
            _template = template;
        }
    }

    public bool LoadTemplate(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (mat.Empty())
        {
            mat.Dispose();
            return false;
        }

        lock (_templateGate)
        {
            _template?.Dispose();
            _template = mat;
        }

        return true;
    }

    public void ClearTemplate(string path)
    {
        lock (_templateGate)
        {
            _template?.Dispose();
            _template = null;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public void ResetMotion()
    {
        _previousGray?.Dispose();
        _previousGray = null;
    }

    private static Mat ExtractPromptGray(Mat source, DetectionSettings s)
    {
        var rect = RelativeRect(source.Width, source.Height, s.PromptX, s.PromptY, s.PromptWidth, s.PromptHeight);
        using var view = new Mat(source, rect);
        var height = Math.Max(8, (int)Math.Round(rect.Height * (PromptWorkWidth / (double)rect.Width)));
        using var resized = new Mat();
        Cv2.Resize(view, resized, new Size(PromptWorkWidth, height), 0, 0, InterpolationFlags.Area);
        var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGRA2GRAY);
        return gray;
    }

    private static PreviewImage BuildPreview(Mat smallBgra, Mat goldMask)
    {
        using var preview = smallBgra.Clone();
        // Tint gold pixels purple so the user can see what the detector counts as gold.
        preview.SetTo(new Scalar(247, 85, 168, 255), goldMask);
        var bytes = new byte[preview.Rows * preview.Cols * 4];
        using (var continuous = preview.IsContinuous() ? preview.Clone() : preview.Clone())
        {
            System.Runtime.InteropServices.Marshal.Copy(continuous.Data, bytes, 0, bytes.Length);
        }

        // Force opaque alpha for display.
        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return new PreviewImage(bytes, preview.Cols, preview.Rows);
    }

    private static Rect RelativeRect(int width, int height, double x, double y, double w, double h)
    {
        var rw = Math.Clamp((int)(width * w), 8, width);
        var rh = Math.Clamp((int)(height * h), 8, height);
        var rx = Math.Clamp((int)(width * x), 0, width - rw);
        var ry = Math.Clamp((int)(height * y), 0, height - rh);
        return new Rect(rx, ry, rw, rh);
    }

    public void Dispose()
    {
        _previousGray?.Dispose();
        lock (_templateGate)
        {
            _template?.Dispose();
            _template = null;
        }
    }
}
