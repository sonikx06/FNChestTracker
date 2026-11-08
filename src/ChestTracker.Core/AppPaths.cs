namespace ChestTracker.Core;

/// <summary>Locations of all files the application writes.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        DatabaseFile = Path.Combine(root, "chests.db");
        BackupDirectory = Path.Combine(root, "backups");
        SettingsFile = Path.Combine(root, "settings.json");
        LogDirectory = Path.Combine(root, "logs");
        TemplateFile = Path.Combine(root, "detection-template.png");
    }

    public string Root { get; }
    public string DatabaseFile { get; }
    public string BackupDirectory { get; }
    public string SettingsFile { get; }
    public string LogDirectory { get; }
    public string TemplateFile { get; }

    public static AppPaths CreateDefault()
    {
        // CHESTTRACKER_DATA_DIR: separate data folder (testing, portable use); default is %LOCALAPPDATA%.
        var root = Environment.GetEnvironmentVariable("CHESTTRACKER_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteChestTracker");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        return paths;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
