namespace QSurfer.Core.Services;

public static class UserDataPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string Subdirectory(params string[] segments) => Path.Combine([Root, .. segments]);

    private static string ResolveRoot()
    {
        if (RuntimeMode.IsDemo)
        {
            return Path.Combine(RuntimeMode.DemoRoot, "state");
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData, "QSurfer");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var root = Path.Combine(home, ".qsurfer");
            var legacyRoot = Path.Combine(home, ".local", "share", "QSurfer");
            TryMigrateLegacyLinuxRoot(legacyRoot, root);
            return root;
        }

        var fallbackLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(fallbackLocalAppData) ? AppContext.BaseDirectory : fallbackLocalAppData, "QSurfer");
    }

    private static void TryMigrateLegacyLinuxRoot(string legacyRoot, string root)
    {
        if (Directory.Exists(root) || !Directory.Exists(legacyRoot))
        {
            return;
        }

        try
        {
            Directory.Move(legacyRoot, root);
        }
        catch (IOException)
        {
            // A second QSurfer instance or filesystem policy can prevent a move.
            // The new home-relative directory still works as a clean fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep startup resilient when a legacy directory is read-only.
        }
    }
}
