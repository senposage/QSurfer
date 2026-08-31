using System.Security.Principal;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class ResultRules
{
    private readonly AppConfig _config;
    private readonly HashSet<string> _identities;

    public ResultRules(AppConfig config)
    {
        _config = config;
        _identities = CurrentIdentities();
        AppLogger.Info("rules", $"visibility identities={string.Join(", ", _identities.OrderBy(identity => identity))} rules={config.VisibilityRules.Count}");
    }

    public bool IsHidden(SearchResult result)
    {
        return IsExcluded(result) || IsVisibilityHidden(result);
    }

    public bool IsExcluded(SearchResult result)
    {
        var fileName = result.FileName;
        var parent = ParentPath(result);
        var components = parent.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var candidates = PathCandidates(result);
        for (var i = 0; i < components.Length; i++)
        {
            var tail = string.Join('\\', components.Skip(i));
            if (!string.IsNullOrWhiteSpace(tail))
            {
                candidates.Add(tail);
                candidates.Add(tail.TrimEnd('\\') + "\\");
            }
        }

        foreach (var rule in _config.Exclude.FolderRules.Select(x => x.Pattern).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var normalized = Normalize(rule);
            if (HasWildcard(normalized))
            {
                if (candidates.Any(candidate => RuleMatchesPath(candidate, normalized)) ||
                    components.Any(component => WildcardMatch(component, normalized)))
                {
                    return true;
                }
            }
            else if (components.Any(component => component.Equals(normalized, StringComparison.OrdinalIgnoreCase)) ||
                     parent.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var rule in _config.Exclude.FileRules.Select(x => x.Pattern).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (HasWildcard(rule))
            {
                if (WildcardMatch(fileName, rule))
                {
                    return true;
                }
            }
            else if (fileName.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsVisibilityHidden(SearchResult result)
    {
        var rules = _config.VisibilityRules.Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern)).ToList();
        if (rules.Count == 0)
        {
            return false;
        }

        var matches = new List<(int specificity, string access)>();
        foreach (var rule in rules)
        {
            var pattern = Normalize(rule.Pattern);
            if (!PathCandidates(result).Any(candidate => RuleMatchesPath(candidate, pattern)))
            {
                continue;
            }
            var specificity = IdentitySpecificity(rule.Identity);
            if (specificity >= 0)
            {
                matches.Add((specificity, rule.Access));
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }
        var best = matches.Max(x => x.specificity);
        return !matches.Where(x => x.specificity == best).Any(x => x.access.Equals("allow", StringComparison.OrdinalIgnoreCase));
    }

    private int IdentitySpecificity(string identity)
    {
        var normalized = (identity ?? "*").Trim();
        if (normalized is "" or "*")
        {
            return 0;
        }
        return _identities.Contains(normalized) ? 2 : -1;
    }

    private static HashSet<string> CurrentIdentities()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        set.Add(user);
        set.Add($@"{domain}\{user}");
        set.Add(Environment.MachineName);
        if (!OperatingSystem.IsWindows())
        {
            return set;
        }

        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (!string.IsNullOrWhiteSpace(identity.Name))
            {
                set.Add(identity.Name);
            }
            if (identity.Groups != null)
            {
                foreach (var group in identity.Groups)
                {
                    try
                    {
                        if (group.Translate(typeof(NTAccount)) is NTAccount account && !string.IsNullOrWhiteSpace(account.Value))
                        {
                            set.Add(account.Value);
                        }
                    }
                    catch (IdentityNotMappedException)
                    {
                    }
                }
            }
        }
        catch
        {
        }
        return set;
    }

    private static string Normalize(string value) => (value ?? "").Replace('/', '\\').Trim();

    private static string ParentPath(SearchResult result)
    {
        var path = Normalize(result.Path);
        var fileName = result.FileName;
        if (!string.IsNullOrWhiteSpace(fileName) && path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^fileName.Length].TrimEnd('\\');
        }
        return path;
    }

    private static string FullPath(SearchResult result)
    {
        var path = Normalize(result.Path).TrimEnd('\\');
        var fileName = result.FileName;
        if (string.IsNullOrWhiteSpace(fileName) || path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return string.IsNullOrWhiteSpace(path) ? fileName : $"{path}\\{fileName}";
    }

    private static HashSet<string> PathCandidates(SearchResult result)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPathCandidate(candidates, ParentPath(result));
        AddPathCandidate(candidates, FullPath(result));
        return candidates;
    }

    private static void AddPathCandidate(HashSet<string> candidates, string path)
    {
        path = Normalize(path).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        candidates.Add(path);
        candidates.Add(path + "\\");
        if (path.StartsWith("Shared\\", StringComparison.OrdinalIgnoreCase))
        {
            var withoutShared = path[7..];
            candidates.Add(withoutShared);
            candidates.Add(withoutShared + "\\");
        }
    }

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

    private static bool RuleMatchesPath(string path, string rule)
    {
        path = Normalize(path).TrimEnd('\\');
        rule = Normalize(rule).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }
        if (HasWildcard(rule))
        {
            return WildcardMatch(path, rule) || WildcardMatch(path + "\\", rule);
        }
        return path.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(rule + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text ?? "", regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
