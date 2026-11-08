using System.Globalization;
using System.Windows;

namespace ChestTracker.App.Localization;

/// <summary>
/// Runtime language switching. XAML uses {DynamicResource Key}; code uses <see cref="T"/>.
/// Swapping the merged dictionary updates every DynamicResource immediately.
/// </summary>
public static class Loc
{
    private static ResourceDictionary? _current;

    public static event EventHandler? LanguageChanged;

    public static string Language { get; private set; } = "ru";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    public static void SetLanguage(string language)
    {
        language = language == "en" ? "en" : "ru";
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/FortniteChestTracker;component/Localization/Strings.{language}.xaml"),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        merged.Add(dictionary);
        _current = dictionary;
        Language = language;
        Culture = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : "ru-RU");
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) =>
        string.Format(Culture, T(key), args);

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes:00}:{d.Seconds:00}";
}
