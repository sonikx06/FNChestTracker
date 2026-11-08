using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChestTracker.Core.Data;
using ChestTracker.Core.Diagnostics;

namespace ChestTracker.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public SettingsStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings { Hotkeys = { Bindings = HotkeySettings.Defaults() } }.Normalize();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
            return (settings ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // Keep the broken file for inspection and continue with defaults.
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Copy(FilePath, $"{FilePath}.bad-{stamp}", overwrite: true);
            }
            catch (IOException)
            {
            }

            Log.Warn("Settings file is invalid, defaults restored", ex);
            return new AppSettings().Normalize();
        }
    }

    public void Save(AppSettings settings)
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(settings, Options);
            DataExporter.WriteAtomically(FilePath, json);
        }
    }

    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Options), Options)!.Normalize();
}
