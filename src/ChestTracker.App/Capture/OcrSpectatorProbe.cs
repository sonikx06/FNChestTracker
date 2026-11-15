using System.Runtime.InteropServices.WindowsRuntime;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Vision;
using OpenCvSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestTracker.App.Capture;

/// <summary>Screen-only, bilingual Windows OCR of the white top-centre spectator title. Independent of app language.</summary>
public sealed class OcrSpectatorProbe : ISpectatorProbe
{
    private readonly List<OcrEngine> _engines = [];
    private byte[] _crop = [];

    public OcrSpectatorProbe()
    {
        foreach (var language in new[] { "en-US", "ru-RU" })
        {
            try
            {
                if (OcrEngine.TryCreateFromLanguage(new Language(language)) is { } engine) _engines.Add(engine);
            }
            catch (Exception ex) { Log.Warn($"Windows OCR ({language}) is unavailable", ex); }
        }
        Description = "Windows OCR: " + string.Join(", ", _engines.Select(e => e.RecognizerLanguage.LanguageTag));
    }

    // One engine can identify a positive banner; only both can establish a bilingual negative check.
    public bool Available => _engines.Count > 0;
    public bool HasRequiredLanguages => _engines.Count == 2;
    public string Description { get; }

    public bool? IsSpectating(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (!Available || width < 64 || height < 64 || stride < width * 4 || bgra.Length < (long)stride * height) return null;
        try
        {
            var (x, y, w, h) = SpectatorBanner.Region(width, height);
            if (_crop.Length != w * h * 4) _crop = new byte[w * h * 4];
            for (var row = 0; row < h; row++)
                bgra.Slice(((y + row) * stride) + x * 4, w * 4).CopyTo(_crop.AsSpan(row * w * 4));

            // Titles are white; watched-player names are yellow. Exclude coloured names before OCR.
            for (var i = 0; i < _crop.Length; i += 4)
            {
                var min = Math.Min(_crop[i], Math.Min(_crop[i + 1], _crop[i + 2]));
                var max = Math.Max(_crop[i], Math.Max(_crop[i + 1], _crop[i + 2]));
                var pixel = min >= 150 && max - min <= 55 ? (byte)0 : (byte)255;
                _crop[i] = _crop[i + 1] = _crop[i + 2] = pixel;
                _crop[i + 3] = 255;
            }
            var scale = Math.Min((double)OcrEngine.MaxImageDimension / w, Math.Clamp(1080.0 / height, 1, 3));
            using var source = Mat.FromPixelData(h, w, MatType.CV_8UC4, _crop);
            using var resized = new Mat();
            Cv2.Resize(source, resized, new Size(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale))), 0, 0, InterpolationFlags.Cubic);
            var pixels = new byte[resized.Width * resized.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                resized.Width, resized.Height, BitmapAlphaMode.Ignore);
            foreach (var engine in _engines)
            {
                var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                if (result.Lines.Any(line => SpectatorBanner.MatchesTitleLine(line.Text))) return true;
            }
            return HasRequiredLanguages ? false : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Spectator check failed", ex);
            return null;
        }
    }
}
