using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChestTracker.App.Capture;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.App.ViewModels;
using ChestTracker.App.Views;
using ChestTracker.Core;
using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Models;
using ChestTracker.Core.Settings;
using ChestTracker.Core.Tracking;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using Xunit.Abstractions;

namespace ChestTracker.App.Tests;

/// <summary>Real WPF controls and deployed Windows OCR; fixtures are not live gameplay accuracy measurements.</summary>
public sealed class WindowsRenderingTests(ITestOutputHelper output)
{
    [Fact]
    public void Real_overlay_layout_and_bilingual_OCR()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Validate(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "WPF/OCR validation timed out");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void Validate()
    {
        var app = new ChestTracker.App.App();
        app.InitializeComponent(); // resources only; no startup, user files, global hotkeys, updater or Discord
        var root = Path.Combine(Path.GetTempPath(), "ct-wpf-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        var backups = new BackupService(paths.BackupDirectory);
        var (db, _) = DatabaseBootstrapper.Open(paths.DatabaseFile, backups);
        using var database = db;
        var repo = new ChestRepository(db);
        var engine = new TrackingEngine(repo, SystemClock.Instance);
        engine.Initialize();
        var settings = new AppSettings();
        settings.General.SoundsEnabled = false;
        settings.Overlay.Visible = false;
        settings.Multiplayer.AutoConnect = false;
        var store = new SettingsStore(paths.SettingsFile);
        var accounts = new DpapiAccountStore(settings, store);
        var sync = new SyncClient(engine, new SyncOutbox(db), accounts, () => new SyncPreferences { AutoConnect = false });
        using var detector = new DetectionService(engine, settings.Detection, TrackingMode.Manual);
        using var hotkeys = new HotkeyService();
        using var sounds = new SoundService { Enabled = false };
        using var main = new MainViewModel(new AppServices {
            Paths = paths, Database = db, Repository = repo, Backups = backups, Engine = engine,
            SettingsStore = store, Settings = settings, Accounts = accounts, Sync = sync,
            Hotkeys = hotkeys, Detection = detector, Sounds = sounds, Dialogs = new DialogService()
        });
        try
        {
            var artifacts = Environment.GetEnvironmentVariable("CHESTTRACKER_VALIDATION_DIR") ?? Path.Combine(AppContext.BaseDirectory, "validation");
            Directory.CreateDirectory(artifacts);
            var vm = main.Overlay;
            vm.TeamMode = true;
            vm.Scale = 1;
            vm.ExcludeFromCapture = false;
            var me = Guid.NewGuid();
            var ids = new[] { me, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var names = new[] { "A", "ДлинноеИмяИгрокаКотороеНеПомещается", "Boris", "ОченьДлинныйНик" };
            var counts = new[] { new CountPair(1, 1), new CountPair(123, 145), new CountPair(9999, 12345), new CountPair(8, 8) };
            var members = ids.Select((id, i) => new MemberDto(id, names[i], i == 0 ? PartyRole.Owner : PartyRole.Member,
                i != 2, true, true, counts[i], counts[i], ParticipantResponse.Accepted)).ToArray();
            var state = new PartyStateDto(Guid.NewGuid(), "Layout test", me, 4, null, null, members,
                new SharedMatchDto(Guid.NewGuid(), SharedMatchStatus.Active, me, DateTimeOffset.UtcNow, null),
                new CountPair(13, 19), new CountPair(190, 200), DateTimeOffset.UtcNow);
            vm.ApplyParty(PartyStatistics.From(state, me));
            Loc.SetLanguage("en");
            var baseline = Path.Combine(artifacts, "overlay-before.xaml");
            if (File.Exists(baseline))
            {
                var xaml = File.ReadAllText(baseline).Replace("x:Class=\"ChestTracker.App.Views.OverlayWindow\"", "")
                    .Replace("clr-namespace:ChestTracker.App.Controls", "clr-namespace:ChestTracker.App.Controls;assembly=FortniteChestTracker");
                var before = (Window)XamlReader.Parse(xaml);
                before.DataContext = vm;
                Render(before, Path.Combine(artifacts, "overlay-before.png"), 96);
                output.WriteLine("Before nickname X: " + string.Join(", ", Names(before).Select(n => n.TranslatePoint(new Point(), before).X)));
                output.WriteLine("Before count right edge (wide): " + AssertCountColumns(before));
                vm.ApplyParty(PartyStatistics.From(state with { Members = members.Select(m => m with { Match = new CountPair(1, 1) }).ToArray() }, me));
                Render(before, Path.Combine(artifacts, "overlay-before-short-counts.png"), 96);
                output.WriteLine("Before count right edge (short): " + AssertCountColumns(before));
                vm.ApplyParty(PartyStatistics.From(state, me));
                before.Close();
            }
            var overlay = new OverlayWindow(vm);
            foreach (var language in new[] { "en", "ru" })
            foreach (var scale in new[] { .5, .75, 1.0, 1.5, 2.0, 3.0 })
            {
                Loc.SetLanguage(language);
                vm.Scale = scale;
                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    Render(overlay, Path.Combine(artifacts, $"overlay-{language}-{scale}-{dpi}.png"), dpi);
                    var texts = Names(overlay);
                    Assert.Equal(4, texts.Length);
                    var positions = texts.Select(n => n.TranslatePoint(new Point(), overlay).X).ToArray();
                    Assert.All(positions, x => Assert.Equal(positions[0], x, 3));
                    AssertCountColumns(overlay);
                    foreach (var name in texts)
                    {
                        var grid = (Grid)name.Parent;
                        var count = grid.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2);
                        Assert.True(name.TranslatePoint(new Point(name.ActualWidth, 0), grid).X <= count.TranslatePoint(new Point(), grid).X);
                        Assert.Equal(TextWrapping.NoWrap, name.TextWrapping);
                        Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
                    }
                }
            }
            // Count columns must retain their right edge and window width across live value/name/local-player updates.
            var countCases = new[] { new CountPair(0, 0), new CountPair(1, 1), new CountPair(8, 8), new CountPair(123, 123),
                new CountPair(9999, 9999), new CountPair(123, 145), new CountPair(9999, 12345) };
            var nameCases = new[] { "A", "Boris", "ОченьДлинныйНик", "ДлинноеИмяИгрокаКотороеНеВлезает", "qqwayne", "Sonik109" };
            foreach (var scale in new[] { .5, 1.0, 1.5, 3.0 })
            foreach (var formatting in new[] { TextFormattingMode.Ideal, TextFormattingMode.Display })
            {
                vm.Scale = scale;
                TextOptions.SetTextFormattingMode(overlay, formatting);
                double? right = null;
                double? width = null;
                for (var step = 0; step < countCases.Length; step++)
                {
                    var changing = members.Select((m, i) => m with {
                        DisplayName = nameCases[(step + i) % nameCases.Length],
                        Match = countCases[(step + i) % countCases.Length]
                    }).ToArray();
                    vm.ApplyParty(PartyStatistics.From(state with { Members = changing }, ids[step % ids.Length]));
                    Render(overlay, Path.Combine(artifacts, $"counts-{scale}-{formatting}-{step}.png"), 96);
                    var edge = AssertCountColumns(overlay);
                    right ??= edge;
                    width ??= overlay.ActualWidth;
                    Assert.Equal(right.Value, edge, 3);
                    Assert.Equal(width.Value, overlay.ActualWidth, 3);
                }
            }
            // Same authoritative ApplyParty update path used on join/leave/rename/count updates.
            double? memberEdge = null;
            for (var size = 1; size <= 4; size++)
            {
                vm.ApplyParty(PartyStatistics.From(state with { Members = members.Take(size).Select(m => m with { DisplayName = "ИзменённоеИмя" + m.DisplayName }).ToArray() }, me));
                Render(overlay, Path.Combine(artifacts, $"overlay-members-{size}.png"), 96);
                Assert.Equal(size, Names(overlay).Length);
                var updatedEdge = AssertCountColumns(overlay);
                memberEdge ??= updatedEdge;
                Assert.Equal(memberEdge.Value, updatedEdge, 3);
                Assert.Single(Names(overlay).Select(n => n.TranslatePoint(new Point(), overlay).X).Distinct());
            }
            overlay.Close();
            var page = main.Party;
            page.InParty = true;
            page.PartyName = "History test";
            page.HasHistory = true;
            var history = new SharedMatchHistoryDto(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-30), DateTimeOffset.Now.AddMinutes(-5),
                new[] { new ParticipantStatsDto(me, "A", new CountPair(5, 7)), new ParticipantStatsDto(ids[1], names[1], new CountPair(8, 12)) }, new CountPair(13, 19), "History test", SharedMatchStatus.Ended);
            foreach (var language in new[] { "ru", "en" })
            {
                Loc.SetLanguage(language);
                page.InParty = page.IsRegistered = page.HasHistory = true;
                page.Status = SyncStatus.Online;
                page.Members.Clear();
                foreach (var member in members) page.Members.Add(new PartyMemberItem(member, member.PlayerId == me, false, false));
                page.History.Clear();
                page.History.Add(new SharedHistoryItem(history, me));
                page.HistorySummary = Loc.F("Party.History.Summary", 2,
                    Loc.F("Party.History.SummaryValues", 19, 9.5, 13), Loc.F("Party.History.SummaryValues", 25, 12.5, 19));
                var partyWindow = new Window { Content = new PartyView { DataContext = page }, Width = 1100, Height = 1000,
                    Background = (Brush)app.FindResource("Brush.Bg"), Foreground = (Brush)app.FindResource("Brush.Text") };
                Render(partyWindow, Path.Combine(artifacts, $"history-{language}.png"), 96);
                foreach (var expander in Descendants(partyWindow).OfType<Expander>()) expander.IsExpanded = true;
                Descendants(partyWindow).OfType<ScrollViewer>().First().ScrollToBottom();
                Render(partyWindow, Path.Combine(artifacts, $"history-{language}-expanded.png"), 96);
                partyWindow.Close();
            }
            ValidateOcr();
        }
        finally { sync.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown(); }
    }

    private void ValidateOcr()
    {
        var probe = new OcrSpectatorProbe();
        output.WriteLine(probe.Description);
        Assert.True(probe.HasRequiredLanguages, "Install Windows English and Russian OCR to run the real OCR fixture checks.");
        var source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ru-spectator.png")));
        foreach (var appLanguage in new[] { "en", "ru" })
        {
            Loc.SetLanguage(appLanguage);
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                var bitmap = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Assert.True(probe.IsSpectating(pixels, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * 4), $"Russian screenshot; app={appLanguage}, scale={scale}");
                // Move the same captured banner outside the HUD region: it must not match.
                var shifted = new byte[pixels.Length];
                var rows = bitmap.PixelHeight / 2;
                Array.Copy(pixels, 0, shifted, rows * bitmap.PixelWidth * 4, rows * bitmap.PixelWidth * 4);
                Assert.False(probe.IsSpectating(shifted, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * 4));
            }
            // Synthetic English HUD fixture: exercises Windows OCR but is NOT footage validation.
            foreach (var width in new[] { 1280, 1920, 2560, 3840 })
            {
                var height = width * 9 / 16;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    var text = new FormattedText("SPECTATING", System.Globalization.CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                        new Typeface("Arial"), height * .025, Brushes.White, 1);
                    dc.DrawText(text, new Point((width - text.Width) / 2, height * .005));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                Assert.True(probe.IsSpectating(pixels, width, height, width * 4), $"Synthetic English; app={appLanguage}, width={width}");
            }
        }
    }

    private static double AssertCountColumns(Window overlay)
    {
        var names = Names(overlay);
        var counts = names.Select(n => ((Grid)n.Parent).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2)).ToArray();
        var rights = counts.Select(c => c.TranslatePoint(new Point(c.ActualWidth, 0), overlay).X).ToArray();
        Assert.All(rights, x => Assert.Equal(rights[0], x, 3));
        foreach (var count in counts)
        {
            var row = (Grid)count.Parent;
            Assert.Equal(TextAlignment.Right, count.TextAlignment);
            Assert.Equal(TextWrapping.NoWrap, count.TextWrapping);
            Assert.True(count.TranslatePoint(new Point(count.ActualWidth, 0), row).X <= row.ActualWidth + .01);
            var text = new FormattedText(count.Text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(count.FontFamily, count.FontStyle, count.FontWeight, count.FontStretch),
                count.FontSize, Brushes.White, VisualTreeHelper.GetDpi(count).PixelsPerDip);
            Assert.True(text.WidthIncludingTrailingWhitespace <= count.ActualWidth, $"Count does not fit: {count.Text}");
        }
        return rights[0];
    }

    private static TextBlock[] Names(DependencyObject parent) => Descendants(parent).OfType<TextBlock>().Where(t => t.Name == "Name" && t.IsVisible).ToArray();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Render(Window window, string file, double dpi)
    {
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        // Let the real page entrance animation and layout settle before capturing.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96),
            (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
