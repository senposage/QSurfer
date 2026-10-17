using System.Net.Http.Headers;
using System.Text.Json;

namespace QSurfer.Avalonia.Services;

public static class ReleaseUpdateService
{
    public const string ReleasesPageUrl = "https://github.com/senposage/QSurfer/releases";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/senposage/QSurfer/releases/latest";

    public static async Task<string?> GetNewerReleaseVersionAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QSurfer", currentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseApiUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (!release.RootElement.TryGetProperty("tag_name", out var tagName))
        {
            return null;
        }

        var versionText = tagName.GetString()?.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latestVersion) || latestVersion <= currentVersion)
        {
            return null;
        }

        return latestVersion.ToString(3);
    }
}
