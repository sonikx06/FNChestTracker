using System.Runtime.InteropServices.WindowsRuntime;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestTracker.App.Capture;

/// <summary>
/// Reads Fortnite's spectator banner ("SPECTATING" above the watched player's name, top centre) with the OCR engine
/// built into Windows (Windows.Media.Ocr). Only a small crop of the captured frame is read, on the inference thread;
/// nothing leaves the PC. English OCR is preferred (the banner text is Latin); any installed OCR language is used
/// otherwise.
/// </summary>
public sealed class OcrSpectatorProbe : ISpectatorProbe
{
    private readonly OcrEngine? _engine;
    private byte[] _crop = [];

    public OcrSpectatorProbe()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            Log.Warn("Windows OCR is not available", ex);
        }

        Description = _engine is null ? "unavailable" : $"Windows OCR ({_engine.RecognizerLanguage.LanguageTag})";
    }

    public bool Available => _engine is not null;

    public string Description { get; }

    public bool? IsSpectating(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (_engine is null || width < 64 || height < 64)
        {
            return null;
        }

        try
        {
            var (x, y, w, h) = SpectatorBanner.Region(width, height);
            var length = w * h * 4;
            if (_crop.Length != length)
            {
                _crop = new byte[length];
            }

            for (var row = 0; row < h; row++)
            {
                bgra.Slice(((y + row) * stride) + (x * 4), w * 4).CopyTo(_crop.AsSpan(row * w * 4, w * 4));
            }

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(_crop.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            return SpectatorBanner.Matches(result.Text);
        }
        catch (Exception ex)
        {
            Log.Warn("Spectator check failed", ex);
            return null;
        }
    }
}
