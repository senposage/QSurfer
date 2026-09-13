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
            TryMigrateLegacyLinuxRoots(home, root);
            return root;
        }

        var fallbackLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(fallbackLocalAppData) ? AppContext.BaseDirectory : fallbackLocalAppData, "QSurfer");
    }

    internal static IReadOnlyList<string> LegacyLinuxRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? [] : FindLegacyLinuxRoots(home, Path.Combine(home, ".qsurfer"));
    }

    private static void TryMigrateLegacyLinuxRoots(string home, string root)
    {
        foreach (var legacyRoot in FindLegacyLinuxRoots(home, root))
        {
            TryMergeLegacyLinuxRoot(legacyRoot, root);
        }
    }

    private static IReadOnlyList<string> FindLegacyLinuxRoots(string home, string root)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var localShare = Path.Combine(home, ".local", "share");

        AddCandidate(Path.Combine(localShare, "QSurfer"));
        AddCandidate(Path.Combine(localShare, "qsurfer"));
        AddCaseVariants(localShare, "QSurfer");
        AddCaseVariants(home, ".qsurfer");

        return roots.ToList();

        void AddCaseVariants(string parent, string expectedName)
        {
            try
            {
                if (!Directory.Exists(parent))
                {
                    return;
                }

                foreach (var candidate in Directory.EnumerateDirectories(parent))
                {
                    if (Path.GetFileName(candidate).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        AddCandidate(candidate);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        void AddCandidate(string candidate)
        {
            try
            {
                if (!Directory.Exists(candidate))
                {
                    return;
                }

                var fullPath = Path.GetFullPath(candidate);
                if (!fullPath.Equals(Path.GetFullPath(root), StringComparison.Ordinal))
                {
                    roots.Add(fullPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryMergeLegacyLinuxRoot(string legacyRoot, string root)
    {
        try
        {
            MergeMissingEntries(legacyRoot, root);
        }
        catch (IOException)
        {
            // A second QSurfer instance or filesystem policy can prevent a move.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep startup resilient when a legacy directory is read-only.
        }
    }

    private static void MergeMissingEntries(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var sourceDirectory in Directory.EnumerateDirectories(source).ToList())
        {
            var targetDirectory = Path.Combine(destination, Path.GetFileName(sourceDirectory));
            if (!Directory.Exists(targetDirectory))
            {
                Directory.Move(sourceDirectory, targetDirectory);
            }
            else
            {
                MergeMissingEntries(sourceDirectory, targetDirectory);
            }
        }

        foreach (var sourceFile in Directory.EnumerateFiles(source).ToList())
        {
            var targetFile = Path.Combine(destination, Path.GetFileName(sourceFile));
            if (!File.Exists(targetFile))
            {
                File.Move(sourceFile, targetFile);
            }
        }
    }
}
