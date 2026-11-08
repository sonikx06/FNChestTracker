using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ChestTracker.App.Services;

/// <summary>Draws the chest icon in code so the app ships without binary assets.</summary>
public static class IconFactory
{
    public static BitmapSource CreateBitmap(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 32.0;
            var bg = new LinearGradientBrush(Color.FromRgb(0xB4, 0x6C, 0xFF), Color.FromRgb(0x6D, 0x4A, 0xFF), 45);
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), 7 * s, 7 * s);

            var gold = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            var wood = new SolidColorBrush(Color.FromRgb(0x3B, 0x1F, 0x5E));
            var pen = new Pen(gold, 1.6 * s);
            // lid
            dc.DrawRoundedRectangle(wood, pen, new Rect(6 * s, 8 * s, 20 * s, 7 * s), 3 * s, 3 * s);
            // body
            dc.DrawRoundedRectangle(wood, pen, new Rect(6 * s, 15 * s, 20 * s, 10 * s), 2 * s, 2 * s);
            // lock
            dc.DrawRoundedRectangle(gold, null, new Rect(14 * s, 13 * s, 4 * s, 6 * s), 1 * s, 1 * s);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Round badge with one (solo) or three (team) player silhouettes, used as Discord small image.</summary>
    public static BitmapSource CreateBadge(int size, bool team)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 64.0;
            var bg = team
                ? new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x63, 0x66, 0xF1), 45)
                : new LinearGradientBrush(Color.FromRgb(0xB4, 0x6C, 0xFF), Color.FromRgb(0x6D, 0x4A, 0xFF), 45);
            dc.DrawEllipse(bg, null, new Point(32 * s, 32 * s), 32 * s, 32 * s);

            void Person(double cx, double scale, Brush brush)
            {
                dc.DrawEllipse(brush, null, new Point(cx * s, 25 * s), 7 * s * scale, 7 * s * scale);
                var body = new RectangleGeometry(new Rect((cx - 12 * scale) * s, 35 * s, 24 * scale * s, 15 * scale * s), 9 * scale * s, 9 * scale * s);
                dc.DrawGeometry(brush, null, body);
            }

            if (team)
            {
                var side = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
                Person(19, 0.8, side);
                Person(45, 0.8, side);
            }

            Person(32, team ? 1.0 : 1.15, Brushes.White);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Writes the Rich Presence art assets for upload in the Discord Developer Portal.</summary>
    public static IReadOnlyList<string> ExportDiscordAssets(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var files = new List<string>();
        void Save(BitmapSource bmp, string name)
        {
            var path = System.IO.Path.Combine(directory, name);
            using var stream = System.IO.File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(stream);
            files.Add(path);
        }

        Save(CreateBitmap(1024), "logo.png");
        Save(CreateBadge(512, team: false), "solo.png");
        Save(CreateBadge(512, team: true), "team.png");
        return files;
    }

    public static Drawing.Icon CreateTrayIcon()
    {
        const int size = 32;
        var source = CreateBitmap(size);
        var pixels = new byte[size * size * 4];
        source.CopyPixels(pixels, size * 4, 0);

        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, size, size), Drawing.Imaging.ImageLockMode.WriteOnly,
            Drawing.Imaging.PixelFormat.Format32bppPArgb);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon owns its handle and the temporary HICON can be destroyed.
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
