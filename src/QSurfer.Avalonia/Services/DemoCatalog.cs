using System.Globalization;
using System.Text;
using QSurfer.Core.Models;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

internal static class DemoCatalog
{
    private const string ArchiveName = "Northstar Archive";
    public const string VirtualDriveRoot = "N:\\";
    public static string ArchiveDisplayName => ArchiveName;
    public static string NavigationDisplayName => $"{ArchiveName} (N:)";
    private static readonly string[] MatterFolders =
    [
        "Active Matters\\Arbor Holdings", "Active Matters\\Cedar Point", "Active Matters\\Juniper Labs",
        "Active Matters\\Northstar Ventures", "Active Matters\\Pinecrest Group", "Active Matters\\Summit Works",
        "Client Services\\New Client Intake", "Client Services\\Engagement Letters", "Client Services\\Billing Support",
        "Corporate\\Board Materials", "Corporate\\Entity Formation", "Corporate\\Annual Reports",
        "Property & Facilities\\Lease Portfolio", "Property & Facilities\\Site Reviews", "Property & Facilities\\Vendor Files",
        "Research Library\\Reference Guides", "Research Library\\Policy Updates", "Research Library\\Training Materials",
        "Operations\\Team Planning", "Operations\\Templates", "Operations\\Meeting Notes",
        "People & Culture\\Onboarding", "People & Culture\\Benefits", "People & Culture\\Recruiting",
    ];

    private static readonly (string Name, string Extension, string Terms)[] FileTemplates =
    [
        ("Legal Services Agreement", "docx", "legal services agreement client engagement"),
        ("Lease Review Checklist", "pdf", "lease property review renewal"),
        ("Board Meeting Notes", "docx", "board meeting corporate notes"),
        ("Court Filing Preparation", "pdf", "court filing matter preparation"),
        ("Project Timeline", "xlsx", "project timeline planning schedule"),
        ("Research Brief", "docx", "research policy analysis reference"),
        ("Service Proposal", "pdf", "service proposal client engagement"),
        ("Operations Playbook", "pptx", "operations training team guide"),
        ("Budget Forecast", "xlsx", "budget forecast finance planning"),
        ("Matter Status Update", "docx", "matter update review client"),
    ];

    private static readonly List<DemoRecord> Records = [];
    private static bool _initialized;

    public static AppConfig CreateConfig() => new()
    {
        Host = "demo.qsurfer.local",
        Port = 443,
        Ssl = true,
        User = "demo-user",
        Password = "demo-only",
        PathMappings = [new PathMapping { ShareRoot = VirtualDriveRoot.TrimEnd('\\'), MappedRoot = RuntimeMode.DemoArchiveRoot }],
        Behavior = new BehaviorConfig
        {
            Theme = "dark",
            PreviewPane = true,
            ShowNavigationPane = true,
            ShowLocalNavigationFolders = false,
            ShowLocalNavigationDrives = false,
            FlattenRecycleBin = true,
            FirstPageSize = 20,
            NextPageSize = 40,
            MaxSearchResults = 240,
        },
    };

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(RuntimeMode.DemoArchiveRoot);
        Records.Clear();
        var now = DateTime.Today.AddHours(10);
        var recordNumber = 0;
        foreach (var folder in MatterFolders)
        {
            var actualFolder = Path.Combine(RuntimeMode.DemoArchiveRoot, folder);
            Directory.CreateDirectory(actualFolder);
            for (var index = 0; index < FileTemplates.Length; index++)
            {
                var template = FileTemplates[index];
                var numberedName = $"{template.Name} {index + 1:00}.{template.Extension}";
                var actualPath = Path.Combine(actualFolder, numberedName);
                WriteDemoFile(actualPath, template.Extension, template.Name, folder);
                var relativePath = $"{VirtualDriveRoot}{folder}\\{numberedName}";
                Records.Add(new DemoRecord(
                    relativePath,
                    actualPath,
                    numberedName,
                    template.Extension,
                    template.Terms + " demo legal",
                    now.AddDays(-recordNumber).AddMinutes(-(recordNumber % 13) * 7),
                    18_000 + recordNumber * 913));
                recordNumber++;
            }
        }

        var overviewPath = Path.Combine(RuntimeMode.DemoArchiveRoot, "Northstar Archive Overview.pdf");
        WritePdf(overviewPath, "Northstar Archive", "Demo document - fictional sample data only");
        Records.Insert(0, new DemoRecord(
            $"{VirtualDriveRoot}Northstar Archive Overview.pdf",
            overviewPath,
            "Northstar Archive Overview.pdf",
            "pdf",
            "northstar archive overview demo search browse preview",
            now,
            32_400));

        CreateRecoverySamples(now);
        _initialized = true;
    }

    public static IReadOnlyList<SearchResult> Search(string query, bool searchContents = false)
    {
        Initialize();
        var terms = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Records
            .Where(record => terms.All(term => (searchContents ? record.SearchText + " " : "")
                .Contains(term, StringComparison.OrdinalIgnoreCase) ||
                record.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                record.DisplayPath.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(record => record.Modified)
            .Select(ToSearchResult)
            .ToList();
    }

    public static IReadOnlyList<SearchResult> FilterToScopes(
        IEnumerable<SearchResult> results,
        IEnumerable<string> includedScopes,
        IEnumerable<string> excludedScopes)
    {
        var included = includedScopes.Select(NormalizePath).Where(path => path.Length > 0).ToList();
        var excluded = excludedScopes.Select(NormalizePath).Where(path => path.Length > 0).ToList();
        return results.Where(result =>
        {
            var path = NormalizePath(result.Path);
            var mostSpecificLength = -1;
            bool? decision = null;
            foreach (var (scope, isIncluded) in included.Select(scope => (scope, true))
                         .Concat(excluded.Select(scope => (scope, false))))
            {
                if (!IsWithinScope(path, scope))
                {
                    continue;
                }

                if (scope.Length > mostSpecificLength || (scope.Length == mostSpecificLength && !isIncluded))
                {
                    mostSpecificLength = scope.Length;
                    decision = isIncluded;
                }
            }

            return decision ?? included.Count == 0;
        }).ToList();
    }

    public static IReadOnlyList<SearchResult> FavoriteResults() => Search("client", searchContents: true).Take(4)
        .Concat(Search("lease", searchContents: true).Take(2))
        .ToList();

    public static string? ResolveScope(string input)
    {
        var normalized = input.Replace('/', '\\').Trim().Trim('\\');
        if (normalized.StartsWith(RuntimeMode.DemoArchiveRoot, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetRelativePath(RuntimeMode.DemoArchiveRoot, normalized).Trim('\\', '/').Replace('/', '\\');
        }
        else if (normalized.StartsWith(VirtualDriveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[VirtualDriveRoot.TrimEnd('\\').Length..].TrimStart('\\');
        }
        else if (normalized.StartsWith(ArchiveName + "\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(ArchiveName.Length + 1)..];
        }

        var isArchiveFolder = MatterFolders.Any(folder =>
            folder.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase));
        return isArchiveFolder ? $"{VirtualDriveRoot}{normalized}" : null;
    }

    public static string? ResolveActualPath(string input)
    {
        var normalized = input.Replace('/', '\\').Trim();
        if (normalized.StartsWith(RuntimeMode.DemoArchiveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.StartsWith(VirtualDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalized[VirtualDriveRoot.Length..].TrimStart('\\');
            return string.IsNullOrWhiteSpace(remainder)
                ? RuntimeMode.DemoArchiveRoot
                : Path.Combine(RuntimeMode.DemoArchiveRoot, remainder);
        }

        return normalized.Equals(ArchiveName, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(NavigationDisplayName, StringComparison.OrdinalIgnoreCase)
            ? RuntimeMode.DemoArchiveRoot
            : null;
    }

    public static string ToDisplayPath(string path)
    {
        if (!path.StartsWith(RuntimeMode.DemoArchiveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var remainder = Path.GetRelativePath(RuntimeMode.DemoArchiveRoot, path).Trim('\\', '/').Replace('/', '\\');
        return string.IsNullOrWhiteSpace(remainder) || remainder == "."
            ? VirtualDriveRoot
            : VirtualDriveRoot + remainder;
    }

    public static void SeedHistory(HistoryStore history)
    {
        foreach (var query in new[] { "lease", "client", "court", "board meeting" })
        {
            history.RecordSearch(query);
        }

        var existingNames = history.SavedSearches()
            .Select(search => search.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains("Active lease reviews"))
        {
            history.SaveSearch(new SavedSearch(0, "Active lease reviews", "lease",
                [$"{VirtualDriveRoot}Property & Facilities"], [], [], "details", "recent:desc", null, DateTime.Today, false, false));
        }
        if (!existingNames.Contains("Client matter updates"))
        {
            history.SaveSearch(new SavedSearch(0, "Client matter updates", "client",
                [$"{VirtualDriveRoot}Active Matters"], [], [], "details", "recent:desc", null, DateTime.Today, false, true));
        }
        history.SetStarred(FavoriteResults(), true);
    }

    public static bool IsDemoPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(RuntimeMode.DemoRoot, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinScope(string path, string scope) =>
        path.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        (path ?? "").Replace('/', '\\').Trim().TrimEnd('*').Trim('\\');

    private static SearchResult ToSearchResult(DemoRecord record) => new()
    {
        Name = Path.GetFileNameWithoutExtension(record.FileName),
        Extension = record.Extension,
        Path = record.DisplayPath,
        ResolvedPath = record.DisplayPath,
        WindowsPath = record.ActualPath,
        ShowInternalPath = true,
        Type = record.Extension.ToUpperInvariant() + " File",
        Size = record.Size,
        Modified = record.Modified.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture),
    };

    private static void CreateRecoverySamples(DateTime now)
    {
        var recycleRoot = Path.Combine(RuntimeMode.DemoArchiveRoot, "@Recycle");
        var recycledFolder = Path.Combine(recycleRoot, "Property & Facilities", "Lease Portfolio");
        Directory.CreateDirectory(recycledFolder);
        WritePdf(Path.Combine(recycledFolder, "Superseded Lease Draft.pdf"), "Superseded Lease Draft", "A fictional deleted-document sample for the QSurfer demo.");
        File.SetLastWriteTime(Path.Combine(recycledFolder, "Superseded Lease Draft.pdf"), now.AddDays(-2));

        var snapshotRoot = Path.Combine(RuntimeMode.DemoArchiveRoot, "@Recently-Snapshot", "2026-09-01", "Active Matters", "Juniper Labs");
        Directory.CreateDirectory(snapshotRoot);
        WritePdf(Path.Combine(snapshotRoot, "Matter Status Update 10.pdf"), "Matter Status Update", "Fictional snapshot sample.");
    }

    private static void WriteDemoFile(string path, string extension, string title, string folder)
    {
        if (File.Exists(path))
        {
            return;
        }

        if (extension.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            WritePdf(path, title, $"Fictional sample document for {folder}.");
            return;
        }

        File.WriteAllText(path, $"{title}\n\nFictional QSurfer demo document.\nFolder: {folder}\n", Encoding.UTF8);
    }

    private static void WritePdf(string path, string title, string subtitle)
    {
        if (File.Exists(path))
        {
            return;
        }

        var content = $"BT /F1 24 Tf 72 720 Td ({EscapePdf(title)}) Tj 0 -42 Td /F1 13 Tf ({EscapePdf(subtitle)}) Tj 0 -28 Td (This file contains synthetic data for a product demonstration.) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
    }

    private static string EscapePdf(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private sealed record DemoRecord(string DisplayPath, string ActualPath, string FileName, string Extension, string SearchText, DateTime Modified, long Size);
}
