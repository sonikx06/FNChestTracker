namespace ChestTracker.Core.Updates;

/// <summary>
/// Replaces the application folder with a verified package, with rollback. Runs in a separate updater process after
/// the application has exited, so no running file is overwritten. Only the application folder is touched: settings,
/// databases, backups and logs live in %LOCALAPPDATA%\FortniteChestTracker and are never part of an update.
/// <list type="number">
/// <item>everything currently in the application folder is moved to a backup folder;</item>
/// <item>the new files are copied in;</item>
/// <item>if anything fails, the partial new files are removed and the backup is moved back.</item>
/// </list>
/// </summary>
public static class UpdateInstaller
{
    public static void Apply(string source, string target, string backup, Action<string>? log = null)
    {
        if (!File.Exists(Path.Combine(source, UpdatePackage.ExecutableName)))
        {
            throw new UpdateVerificationException("The staged package has no executable.");
        }

        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        Directory.CreateDirectory(backup);
        var moved = false;
        try
        {
            MoveContents(target, backup);
            moved = true;
            log?.Invoke($"Old version moved to {backup}");
            CopyContents(source, target);
            log?.Invoke($"New version copied to {target}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Install failed ({ex.Message}); restoring the previous version");
            if (moved || Directory.EnumerateFileSystemEntries(backup).Any())
            {
                Rollback(target, backup, log);
            }

            throw;
        }
    }

    /// <summary>Removes whatever is in <paramref name="target"/> and moves the backup back.</summary>
    public static void Rollback(string target, string backup, Action<string>? log = null)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(target).ToList())
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        MoveContents(backup, target);
        log?.Invoke("Previous version restored");
    }

    private static void MoveContents(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Move(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(dir));
            if (string.Equals(Path.GetPathRoot(dir), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(dir, dest);
            }
            else
            {
                MoveContents(dir, dest);
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void CopyContents(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            CopyContents(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
