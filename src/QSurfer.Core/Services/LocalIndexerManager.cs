using System.Diagnostics;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>
/// Owns the optional, local QIndexer deployment.  It never alters the
/// connection settings for administrator-managed (remote/NAS) indexers.
/// </summary>
public sealed class LocalIndexerManager : IDisposable
{
    public const string ServiceName = "QSurferQIndexer";
    private const string LinuxServiceName = "qindexer.service";
    private static readonly TimeSpan StatusProbeTimeout = TimeSpan.FromSeconds(4);
    public const string Repository = "senposage/qindexer";
    private const string NssmRepository = "tuyndoan/nssm";
    private static string ExecutableName => OperatingSystem.IsWindows() ? "qindexer.exe" : "qindexer";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly bool _usePostgres;
    private readonly string? _progressPath;

    public LocalIndexerManager(HttpClient? httpClient = null, string? stateDirectory = null, bool usePostgres = false, string? progressPath = null)
    {
        _http = httpClient ?? CreateHttpClient();
        _ownsHttp = httpClient == null;
        StateDirectory = stateDirectory ?? ResolveStateDirectory();
        _usePostgres = usePostgres;
        _progressPath = progressPath;
    }

    public string StateDirectory { get; }
    /// <summary>
    /// The verified QIndexer release is kept as its own untouched payload.
    /// Configuration, credentials, logs, and index data stay at the state
    /// root so release updates cannot overwrite them.
    /// </summary>
    public string ReleaseDirectory => Path.Combine(StateDirectory, "release");
    private string ReleaseExecutablePath => Path.Combine(ReleaseDirectory, ExecutableName);
    private string PackagedExecutablePath => Path.Combine(StateDirectory, "bin", ExecutableName);
    /// <summary>The QIndexer-owned executable installed by its release package.</summary>
    public string ExecutablePath => File.Exists(PackagedExecutablePath) ? PackagedExecutablePath : ReleaseExecutablePath;
    private string ToolsDirectory => Path.Combine(StateDirectory, "tools");
    private string NssmPath => Path.Combine(ToolsDirectory, "nssm.exe");
    private string PackagedConfigPath => Path.Combine(StateDirectory, "configs", "qindexer.yaml");
    private string LegacyConfigPath => Path.Combine(StateDirectory, "config.yaml");
    /// <summary>The QIndexer-owned configuration. The legacy path is read only for upgrade compatibility.</summary>
    public string ConfigPath => File.Exists(PackagedConfigPath) || !File.Exists(LegacyConfigPath)
        ? PackagedConfigPath
        : LegacyConfigPath;
    public string DataDirectory => Path.Combine(StateDirectory, "data");
    public string LogsDirectory => Path.Combine(StateDirectory, "logs");
    public string SecretsDirectory => Path.Combine(StateDirectory, "secrets");
    private string SearchTokenPath => Path.Combine(SecretsDirectory, "search.token");
    private string AdminTokenPath => Path.Combine(SecretsDirectory, "admin.token");

    /// <summary>Returns the catalog mode from QIndexer's own configuration.</summary>
    public bool? TryGetConfiguredUsePostgres()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var config = File.ReadAllText(ConfigPath);
            var index = System.Text.RegularExpressions.Regex.Match(config, @"(?ms)^index:\s*$.*?(?=^\S|\z)");
            var driver = index.Success
                ? System.Text.RegularExpressions.Regex.Match(index.Value, @"(?m)^\s+driver:\s*[""']?(?<driver>[^""'\r\n#]+)")
                : System.Text.RegularExpressions.Match.Empty;
            if (driver.Success)
            {
                return string.Equals(driver.Groups["driver"].Value.Trim(), "postgres", StringComparison.OrdinalIgnoreCase);
            }

            // QIndexer 1.2.8 and older had no catalog setting and were SQLite-only.
            return string.Equals(ConfigPath, LegacyConfigPath, StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the local administration credential for an explicitly requested
    /// support handoff. Callers must never persist or log this value.
    /// </summary>
    public string? GetAdminTokenForSupport()
    {
        return TryReadAuthToken("management");
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The machine-owned state root used by the elevated service installer.</summary>
    public static string MachineStateRoot => MachineStateDirectory();

    /// <summary>
    /// Performs the privileged Linux registration and starts the service for
    /// the original desktop user through that user's systemd session.
    /// </summary>
    public async Task<string> PrepareLinuxServiceAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux service preparation is only available on Linux.");
        }
        if (!IsElevated())
        {
            throw new InvalidOperationException("Linux service preparation requires administrator authorization.");
        }

        CreateStateDirectories();
        var version = await EnsureLatestInstalledAsync(cancellationToken);
        if (!File.Exists(ReleaseExecutablePath))
        {
            throw new InvalidOperationException("The verified QIndexer package did not produce the QIndexer executable.");
        }

        if (!File.Exists(ConfigPath))
        {
            await CreateInitialConfigurationAsync(cancellationToken);
        }

        await EnsureLinuxServiceAsync(cancellationToken);
        await StartAsync(cancellationToken);
        return version;
    }

    public async Task<LocalIndexerSetupResult> EnableAsync(CancellationToken cancellationToken = default, bool forceWindowsService = false)
    {
        var installedVersion = await InstallAsync(cancellationToken, forceWindowsService);

        await StartAsync(cancellationToken, forceWindowsService);
        var health = await WaitForHealthyAsync(cancellationToken);
        if (health.State is not (LocalIndexerHealthState.Healthy or LocalIndexerHealthState.SetupRequired))
        {
            throw new InvalidOperationException(health.Message);
        }

        return new LocalIndexerSetupResult(installedVersion, StateDirectory, UsesWindowsService(), health);
    }

    /// <summary>
    /// Downloads and verifies QIndexer, creates its private configuration, and
    /// registers its operating-system service without starting it.
    /// </summary>
    public async Task<string> InstallAsync(CancellationToken cancellationToken = default, bool forceWindowsService = false)
    {
        EnsureSupported();
        if (forceWindowsService && OperatingSystem.IsWindows() && !StateDirectory.Equals(MachineStateDirectory(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Windows service setup requires the machine-owned QIndexer state directory.");
        }

        CreateStateDirectories();
        var installedVersion = await EnsureLatestInstalledAsync(cancellationToken);
        if (!File.Exists(ReleaseExecutablePath))
        {
            throw new InvalidOperationException("The verified QIndexer package did not produce the QIndexer executable.");
        }

        if (!File.Exists(ConfigPath))
        {
            await CreateInitialConfigurationAsync(cancellationToken);
        }

        if (OperatingSystem.IsLinux())
        {
            await EnsureLinuxServiceAsync(cancellationToken);
        }
        else
        {
            if (!forceWindowsService && !CanUseWindowsService())
            {
                throw new InvalidOperationException("Installing the local QIndexer service requires Windows elevation.");
            }
            await EnsureServiceAsync(cancellationToken);
        }

        return installedVersion;
    }

    public async Task<LocalIndexerSetupResult> UpgradeAsync(CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (!File.Exists(ConfigPath))
        {
            return await EnableAsync(cancellationToken);
        }

        CreateStateDirectories();
        var wasRunning = await IsRunningAsync(cancellationToken);
        if (wasRunning)
        {
            await StopAsync(cancellationToken);
        }

        var downloaded = await DownloadLatestExecutableAsync(cancellationToken);
        var previous = ReleaseDirectory + ".previous";
        var staging = ReleaseDirectory + ".next";
        try
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(previous);
            CopyDirectory(downloaded.PayloadDirectory, staging);
            if (Directory.Exists(ReleaseDirectory))
            {
                Directory.Move(ReleaseDirectory, previous);
            }
            Directory.Move(staging, ReleaseDirectory);

            // QIndexer owns its runtime, PostgreSQL provisioning, and YAML.
            // Re-run its packaged installer for modern deployments so it
            // updates the installed binary without QSurfer rewriting those
            // settings. A legacy SQLite deployment stays on SQLite; 1.2.8
            // predates the explicit driver setting, so stamp that choice
            // before starting a 1.2.9+ binary that defaults to PostgreSQL.
            var currentConfigIsPackaged = string.Equals(ConfigPath, PackagedConfigPath, StringComparison.OrdinalIgnoreCase);
            if (currentConfigIsPackaged)
            {
                await RunPackagedInstallerAsync(cancellationToken);
            }
            else if (_usePostgres)
            {
                // The user explicitly selected PostgreSQL. Keep the legacy
                // SQLite files intact, but let QIndexer's package provision a
                // new private PostgreSQL catalog and configuration.
                await RunPackagedInstallerAsync(cancellationToken);
                await SeedLocalRootsAsync(cancellationToken);
            }
            else
            {
                await PreserveLegacySqliteModeAsync(cancellationToken);
            }

            await StartAsync(cancellationToken);
            var health = await WaitForHealthyAsync(cancellationToken);
            if (health.State != LocalIndexerHealthState.Healthy)
            {
                throw new InvalidOperationException(health.Message);
            }

            // Keep the previous release only until the new release has
            // answered an authenticated health request.
            TryDeleteDirectory(previous);
            return new LocalIndexerSetupResult(downloaded.Version, StateDirectory, UsesWindowsService(), health);
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            TryDeleteDirectory(staging);
            if (Directory.Exists(previous))
            {
                TryDeleteDirectory(ReleaseDirectory);
                Directory.Move(previous, ReleaseDirectory);
                if (wasRunning)
                {
                    await StartAsync(CancellationToken.None);
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(downloaded.TemporaryDirectory);
        }
    }

    /// <summary>Stops indexing but deliberately retains every local artifact and the index.</summary>
    public async Task DisableAsync(CancellationToken cancellationToken = default) => await StopAsync(cancellationToken);

    /// <summary>
    /// Removes the managed executable/configuration. Index data is retained
    /// unless the caller explicitly asks to remove it.
    /// </summary>
    public async Task UninstallAsync(bool removeIndexData, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        await StopAsync(cancellationToken);
        if (OperatingSystem.IsLinux())
        {
            var unitPath = LinuxUserServiceUnitPath();
            if (File.Exists(unitPath))
            {
                await RunLinuxUserSystemctlAsync(["disable", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
                File.Delete(unitPath);
                await RunLinuxUserSystemctlAsync(["daemon-reload"], cancellationToken, allowAlreadyRunning: true);
            }
        }
        if (UsesWindowsService())
        {
            if (File.Exists(NssmPath))
            {
                await RunProcessAsync(NssmPath, ["remove", ServiceName, "confirm"], cancellationToken, allowAlreadyRunning: true);
            }
            else
            {
                await RunProcessAsync("sc.exe", ["delete", ServiceName], cancellationToken, allowAlreadyRunning: true);
            }
        }

        TryDeleteDirectory(ReleaseDirectory);
        TryDeleteDirectory(ReleaseDirectory + ".previous");
        TryDeleteDirectory(ReleaseDirectory + ".next");
        // Remove the legacy binary-only deployment when upgrading from an
        // earlier QSurfer installer. It never contained the release sidecars.
        TryDeleteFile(Path.Combine(StateDirectory, ExecutableName));
        TryDeleteFile(Path.Combine(StateDirectory, ExecutableName + ".previous"));
        TryDeleteFile(PackagedConfigPath);
        TryDeleteFile(LegacyConfigPath);
        TryDeleteDirectory(SecretsDirectory);
        TryDeleteDirectory(LogsDirectory);
        TryDeleteDirectory(Path.Combine(StateDirectory, "bin"));
        TryDeleteDirectory(Path.Combine(StateDirectory, "scripts"));
        if (removeIndexData)
        {
            TryDeleteDirectory(DataDirectory);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default, bool forceWindowsService = false)
    {
        EnsureSupported();
        if (!File.Exists(ExecutablePath) || !File.Exists(ConfigPath))
        {
            throw new InvalidOperationException("QIndexer is not installed yet.");
        }

        if (await IsRunningAsync(cancellationToken))
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            if (!File.Exists(LinuxUserServiceUnitPath()))
            {
                throw new InvalidOperationException("The Linux QIndexer service has not been registered yet.");
            }
            await RunLinuxUserSystemctlAsync(["daemon-reload"], cancellationToken);
            await RunLinuxUserSystemctlAsync(["enable", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
            await RunLinuxUserSystemctlAsync(["start", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
            return;
        }

        if (UsesWindowsService())
        {
            // An elevated install/start refreshes the private host payload and
            // migrates the original in-place wrapper if one remains.
            if (forceWindowsService || IsElevated())
            {
                await EnsureServiceAsync(cancellationToken);
            }
            await RunProcessAsync("sc.exe", ["start", ServiceName], cancellationToken, allowAlreadyRunning: true);
            return;
        }

        if (!forceWindowsService && !CanUseWindowsService())
        {
            throw new InvalidOperationException("Starting the local QIndexer service requires Windows elevation.");
        }

        await EnsureServiceAsync(cancellationToken);
        await RunProcessAsync("sc.exe", ["start", ServiceName], cancellationToken, allowAlreadyRunning: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || !File.Exists(ConfigPath))
        {
            return;
        }

        // NSSM owns the child process lifetime. Stopping the wrapper first
        // prevents an intentional QIndexer shutdown from being restarted.
        if (OperatingSystem.IsWindows() && await IsNssmServiceAsync(cancellationToken))
        {
            await RunProcessAsync(NssmPath, ["stop", ServiceName], cancellationToken, allowAlreadyRunning: true);
            return;
        }

        // The authenticated endpoint asks QIndexer to flush/checkpoint before
        // it exits. It is intentionally best effort when the process is dead
        // or still in first-run setup without a management token.
        try
        {
            await SendAdminAsync(HttpMethod.Post, "/admin/v1/service/stop", null, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The generated config may not contain a management token yet.
            // Fall through to the owning service manager so uninstall and
            // catalog-mode changes remain recoverable from first-run setup.
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        if (OperatingSystem.IsLinux() && await LinuxServiceIsRunningAsync(cancellationToken))
        {
            await RunLinuxUserSystemctlAsync(["stop", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
            return;
        }

        // The endpoint asks QIndexer to flush and stop. The service host also
        // stops itself when its child exits; sc.exe covers the brief race
        // where SCM has not processed that exit yet.
        if (UsesWindowsService() && await ServiceIsRunningAsync(cancellationToken))
        {
            try
            {
                await RunProcessAsync("sc.exe", ["stop", ServiceName], cancellationToken, allowAlreadyRunning: true);
            }
            catch (InvalidOperationException)
            {
                if (await ServiceIsRunningAsync(cancellationToken))
                {
                    throw;
                }

                // The service host observed its QIndexer child exit and SCM
                // completed the stop between our status check and sc.exe.
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await IsRunningAsync(cancellationToken))
            {
                return;
            }
            await Task.Delay(300, cancellationToken);
        }
        throw new TimeoutException("QIndexer did not stop within 30 seconds.");
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default) =>
        await SendAdminAsync(HttpMethod.Post, "/admin/v1/crawler/pause", "{\"seconds\":86400}", cancellationToken);

    public async Task ResumeAsync(CancellationToken cancellationToken = default) =>
        await SendAdminAsync(HttpMethod.Post, "/admin/v1/crawler/resume", null, cancellationToken);

    /// <summary>Clears only indexed rows after an explicit UI confirmation; source files are untouched.</summary>
    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        foreach (var root in status.Roots)
        {
            var body = JsonSerializer.Serialize(new { confirm_root_id = root.Id });
            await SendAdminAsync(HttpMethod.Post, $"/admin/v1/roots/{Uri.EscapeDataString(root.Id)}/clear-index", body, cancellationToken);
            await SendAdminAsync(HttpMethod.Post, $"/admin/v1/roots/{Uri.EscapeDataString(root.Id)}/crawl", null, cancellationToken);
        }
    }

    public void OpenAdvancedAdministration()
    {
        var ports = TryReadPorts();
        if (ports == null)
        {
            throw new InvalidOperationException("QIndexer has not been configured yet.");
        }
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{ports.Value.AdminPort}/") { UseShellExecute = true });
    }

    public SearchServiceConnection? TryGetSearchConnection()
    {
        var ports = TryReadPorts();
        var token = TryReadAuthToken("server");
        if (ports == null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new SearchServiceConnection { Enabled = true, Host = "127.0.0.1", Port = ports.Value.SearchPort, Ssl = false, SslVerify = true, Token = token };
    }

    public async Task<LocalIndexerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return LocalIndexerStatus.Unsupported();
        }
        if (!File.Exists(ConfigPath) || !File.Exists(ExecutablePath))
        {
            return LocalIndexerStatus.NotInstalled(StateDirectory);
        }

        // A missing or unhealthy user D-Bus session must not leave Settings
        // waiting indefinitely just to paint the optional QIndexer status.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(StatusProbeTimeout);
        var probeToken = probeTimeout.Token;
        var processRunning = await IsRunningAsync(probeToken);
        if (!processRunning)
        {
            return LocalIndexerStatus.ProcessUnavailable(StateDirectory, UsesWindowsService());
        }

        var ports = TryReadPorts();
        var searchToken = TryReadAuthToken("server");
        if (ports == null || string.IsNullOrWhiteSpace(searchToken))
        {
            return LocalIndexerStatus.SetupRequired(StateDirectory, UsesWindowsService());
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ports.Value.SearchPort}/v1/health");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", searchToken);
            using var response = await _http.SendAsync(request, probeToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return LocalIndexerStatus.AuthenticationFailed(StateDirectory, UsesWindowsService());
            }
            if (!response.IsSuccessStatusCode)
            {
                return LocalIndexerStatus.HealthUnavailable(StateDirectory, UsesWindowsService(), $"Health endpoint returned {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(probeToken));
            var root = document.RootElement;
            var index = root.TryGetProperty("index", out var indexElement) ? indexElement : default;
            var ready = index.ValueKind == JsonValueKind.Object && index.TryGetProperty("ready", out var readyValue) && readyValue.GetBoolean();
            var count = index.ValueKind == JsonValueKind.Object && index.TryGetProperty("document_count", out var countValue) && countValue.TryGetInt64(out var documentCount)
                ? documentCount
                : 0;
            var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() ?? "Unknown" : "Unknown";
            if (!ready)
            {
                return LocalIndexerStatus.IndexUnavailable(StateDirectory, UsesWindowsService(), version, count);
            }

            var details = await ReadAdminStatusAsync(ports.Value.AdminPort, probeToken);
            return LocalIndexerStatus.Healthy(StateDirectory, UsesWindowsService(), version, count, details.IndexSizeBytes, details.Roots, details.CrawlerState, details.LastError, details.LastCrawl);
        }
        catch (HttpRequestException)
        {
            return LocalIndexerStatus.HealthUnavailable(StateDirectory, UsesWindowsService(), "The loopback health endpoint is unavailable.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LocalIndexerStatus.HealthUnavailable(StateDirectory, UsesWindowsService(), "The loopback health endpoint timed out.");
        }
    }

    public async Task<LocalIndexerHealth> WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        LocalIndexerStatus? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await GetStatusAsync(cancellationToken);
            if (latest.Health.State is LocalIndexerHealthState.Healthy or LocalIndexerHealthState.SetupRequired)
            {
                return latest.Health;
            }
            await Task.Delay(500, cancellationToken);
        }
        return latest?.Health ?? new LocalIndexerHealth(LocalIndexerHealthState.HealthUnavailable, "QIndexer did not become healthy within 30 seconds.");
    }

    public static IReadOnlyList<LocalIndexerVolume> DiscoverFixedVolumes()
    {
        if (OperatingSystem.IsLinux())
        {
            var home = LinuxUserHome();
            var identity = "linux-home:" + home;
            var id = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
            return [new LocalIndexerVolume(id, "Home folder", home, identity)];
        }
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var volumes = new List<LocalIndexerVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var path = drive.RootDirectory.FullName;
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || !IsDriveLetterRoot(path))
                {
                    continue;
                }

                var identity = TryGetVolumeGuid(path) ?? TryGetVolumeSerial(path) ?? path.TrimEnd('\\').ToUpperInvariant();
                var id = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
                volumes.Add(new LocalIndexerVolume(id, $"Local disk {path[..2]}", path, identity));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return volumes.OrderBy(volume => volume.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static (int SearchPort, int AdminPort) FindLoopbackPorts()
    {
        var first = FindLoopbackPort();
        var second = FindLoopbackPort();
        while (second == first)
        {
            second = FindLoopbackPort();
        }
        return (first, second);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<string> EnsureLatestInstalledAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ExecutablePath))
        {
            var current = await GetStatusAsync(cancellationToken);
            return current.Version ?? "Installed";
        }

        var downloaded = await DownloadLatestExecutableAsync(cancellationToken);
        var staging = ReleaseDirectory + ".next";
        try
        {
            TryDeleteDirectory(staging);
            CopyDirectory(downloaded.PayloadDirectory, staging);
            TryDeleteDirectory(ReleaseDirectory);
            Directory.Move(staging, ReleaseDirectory);

            // This was the old binary-only location. The complete release is
            // now deployed under release/ to keep its sidecars together.
            TryDeleteFile(Path.Combine(StateDirectory, ExecutableName));
            TryDeleteFile(Path.Combine(StateDirectory, ExecutableName + ".previous"));
            return downloaded.Version;
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(downloaded.TemporaryDirectory);
        }
    }

    private async Task<DownloadedRelease> DownloadLatestExecutableAsync(CancellationToken cancellationToken)
    {
        using var release = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        release.EnsureSuccessStatusCode();
        using var releaseDocument = JsonDocument.Parse(await release.Content.ReadAsStringAsync(cancellationToken));
        var root = releaseDocument.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString()?.TrimStart('v') ?? "Unknown" : "Unknown";
        var assets = root.GetProperty("assets").EnumerateArray().ToList();
        var packageSuffix = OperatingSystem.IsWindows() ? "_windows_amd64.zip" : "_linux_amd64.tar.gz";
        var package = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase) == true);
        var sums = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.Contains("SHA256SUMS", StringComparison.OrdinalIgnoreCase) == true);
        if (package.ValueKind == JsonValueKind.Undefined || sums.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("The QIndexer release does not include the required platform package and SHA256SUMS.");
        }

        var packageName = package.GetProperty("name").GetString()!;
        var packageUrl = package.GetProperty("browser_download_url").GetString()!;
        var sumsUrl = sums.GetProperty("browser_download_url").GetString()!;
        var temp = Path.Combine(Path.GetTempPath(), "qsurfer-qindexer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, packageName);
        try
        {
            var checksums = await _http.GetStringAsync(sumsUrl, cancellationToken);
            var expected = ParseChecksum(checksums, packageName);
            await using (var source = await _http.GetStreamAsync(packageUrl, cancellationToken))
            await using (var destination = File.Create(zip))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
            await using (var stream = File.OpenRead(zip))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
                {
                    throw new CryptographicException("QIndexer package checksum verification failed.");
                }
            }

            var unpacked = Path.Combine(temp, "unpacked");
            Directory.CreateDirectory(unpacked);
            if (OperatingSystem.IsWindows())
            {
                using var archive = ZipFile.OpenRead(zip);
                archive.ExtractToDirectory(unpacked, overwriteFiles: true);
            }
            else
            {
                await using var gzip = new GZipStream(File.OpenRead(zip), CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, unpacked, overwriteFiles: true);
            }

            var executable = Directory.EnumerateFiles(unpacked, ExecutableName, SearchOption.AllDirectories)
                .SingleOrDefault();
            if (executable == null)
            {
                throw new InvalidOperationException($"The verified QIndexer package does not contain {ExecutableName}.");
            }

            // A release archive may have a top-level version directory (Linux)
            // or place its files at the archive root (Windows). In both cases,
            // deploy every file published beside the executable without adding,
            // renaming, or filtering any QIndexer release content.
            var packageRoot = Path.GetDirectoryName(executable)!;
            var payload = Path.Combine(temp, "payload");
            CopyDirectory(packageRoot, payload);
            return new DownloadedRelease(version, temp, payload);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }
    }

    private async Task EnsureNssmInstalledAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || File.Exists(NssmPath)) return;

        using var release = await _http.GetAsync($"https://api.github.com/repos/{NssmRepository}/releases/latest", cancellationToken);
        release.EnsureSuccessStatusCode();
        using var releaseDocument = JsonDocument.Parse(await release.Content.ReadAsStringAsync(cancellationToken));
        var assets = releaseDocument.RootElement.GetProperty("assets").EnumerateArray().ToList();
        var package = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        var sums = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString()?.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase) == true);
        if (package.ValueKind == JsonValueKind.Undefined || sums.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("The NSSM release does not include the required ZIP package and SHA-256 checksum.");
        }

        var packageName = package.GetProperty("name").GetString()!;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "qsurfer-nssm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var archivePath = Path.Combine(temporaryDirectory, packageName);
        try
        {
            var expected = ParseChecksum(await _http.GetStringAsync(sums.GetProperty("browser_download_url").GetString()!, cancellationToken), packageName);
            await using (var source = await _http.GetStreamAsync(package.GetProperty("browser_download_url").GetString()!, cancellationToken))
            await using (var destination = File.Create(archivePath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
            await using (var stream = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
                {
                    throw new CryptographicException("NSSM package checksum verification failed.");
                }
            }

            var unpacked = Path.Combine(temporaryDirectory, "unpacked");
            ZipFile.ExtractToDirectory(archivePath, unpacked, overwriteFiles: true);
            var nssm = Directory.EnumerateFiles(unpacked, "nssm.exe", SearchOption.AllDirectories)
                .SingleOrDefault(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "win64", StringComparison.OrdinalIgnoreCase));
            if (nssm == null)
            {
                throw new InvalidOperationException("The verified NSSM package does not contain the 64-bit NSSM executable.");
            }

            Directory.CreateDirectory(ToolsDirectory);
            File.Copy(nssm, NssmPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private async Task<LocalIndexerAdminDetails> ReadAdminStatusAsync(int adminPort, CancellationToken cancellationToken)
    {
        try
        {
            using var diagnostics = await SendAdminAsync(HttpMethod.Get, "/admin/v1/diagnostics", null, cancellationToken, adminPort);
            using var document = JsonDocument.Parse(await diagnostics.Content.ReadAsStringAsync(cancellationToken));
            var roots = new List<LocalIndexerRootStatus>();
            var crawlerState = "Idle";
            var lastError = "";
            DateTimeOffset? lastCrawl = null;

            // QIndexer 1.1 diagnostics separates configured roots from their
            // mutable catalog state. Keep the old top-level roots shape as a
            // compatibility fallback for an already-deployed older server.
            var rootArray = document.RootElement.TryGetProperty("config", out var config) &&
                            config.ValueKind == JsonValueKind.Object &&
                            config.TryGetProperty("roots", out var configuredRoots)
                ? configuredRoots
                : document.RootElement.TryGetProperty("roots", out var legacyRoots)
                    ? legacyRoots
                    : default;
            var rootStates = document.RootElement.TryGetProperty("root_states", out var states) &&
                             states.ValueKind == JsonValueKind.Object
                ? states
                : default;
            if (rootArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var root in rootArray.EnumerateArray())
                {
                    var id = ReadString(root, "root_id", "id");
                    var name = ReadString(root, "name");
                    var rootState = rootStates.ValueKind == JsonValueKind.Object &&
                                    !string.IsNullOrWhiteSpace(id) &&
                                    rootStates.TryGetProperty(id, out var stateValue)
                        ? stateValue
                        : root;
                    var state = ReadString(rootState, "last_status", "last_crawl_status", "status");
                    if (string.IsNullOrWhiteSpace(state)) state = ReadBoolean(root, "enabled") ? "Configured" : "Disabled";
                    var error = ReadString(rootState, "last_error");
                    var count = ReadLong(rootState, "document_count");
                    var crawl = ReadTimestamp(rootState, "last_successful_crawl_at");
                    roots.Add(new LocalIndexerRootStatus(id, name, state, count, crawl, error));
                    if (!string.IsNullOrWhiteSpace(error)) lastError = error;
                    if (crawl > lastCrawl) lastCrawl = crawl;
                }
            }
            if (document.RootElement.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
            {
                crawlerState = ReadString(metrics, "crawler_state", "state", "activity");
                if (string.IsNullOrWhiteSpace(crawlerState))
                {
                    crawlerState = ReadBoolean(metrics, "paused")
                        ? "Paused"
                        : ReadLong(metrics, "active_crawls") > 0
                            ? "Crawling"
                            : "Idle";
                }
            }
            var indexSizeBytes = await ReadIndexSizeBytesAsync(adminPort, cancellationToken);
            return new LocalIndexerAdminDetails(roots, string.IsNullOrWhiteSpace(crawlerState) ? "Idle" : crawlerState, lastError, lastCrawl, indexSizeBytes);
        }
        catch
        {
            // Search health remains authoritative. Admin details are additive
            // and must not hide a healthy local search endpoint.
            return new LocalIndexerAdminDetails([], "Unknown", "", null, null);
        }
    }

    private async Task<long?> ReadIndexSizeBytesAsync(int adminPort, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAdminAsync(HttpMethod.Get, "/admin/v1/metrics", null, cancellationToken, adminPort);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("index_size_bytes", out var size) && size.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch
        {
            // Metrics are informational. A healthy search endpoint should not
            // look unhealthy just because a version lacks this admin metric.
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken, int? knownAdminPort = null)
    {
        var ports = knownAdminPort.HasValue ? (SearchPort: 0, AdminPort: knownAdminPort.Value) : TryReadPorts()
            ?? throw new InvalidOperationException("QIndexer has not been configured yet.");
        var adminToken = TryReadAuthToken("management");
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            throw new InvalidOperationException("Finish local QIndexer setup in its administration page.");
        }

        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{ports.AdminPort}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException("QIndexer administration request failed.");
        }
        return response;
    }

    private (int SearchPort, int AdminPort)? TryReadPorts()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            return ParseLoopbackPorts(File.ReadAllText(ConfigPath));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static (int SearchPort, int AdminPort)? ParseLoopbackPorts(string config)
    {
        var search = ReadSectionPort(config, "server");
        var admin = ReadSectionPort(config, "management");
        return search.HasValue && admin.HasValue && search.Value != admin.Value
            ? (search.Value, admin.Value)
            : null;
    }

    private static int? ReadSectionPort(string config, string section)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            config,
            $@"(?ms)^{System.Text.RegularExpressions.Regex.Escape(section)}:\s*$.*?^\s+bind:\s*[\""']?127\.0\.0\.1:(?<port>\d+)[\""']?\s*$");
        return match.Success && int.TryParse(match.Groups["port"].Value, out var port) ? port : null;
    }

    private string? TryReadAuthToken(string section)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var config = File.ReadAllText(ConfigPath);
            var sectionMatch = System.Text.RegularExpressions.Regex.Match(
                config,
                $@"(?ms)^{System.Text.RegularExpressions.Regex.Escape(section)}:\s*$.*?(?=^\S|\z)");
            if (!sectionMatch.Success) return null;
            var token = System.Text.RegularExpressions.Regex.Match(
                sectionMatch.Value,
                "(?m)^\\s+token:\\s*[\"']?(?<token>[^\"'\\r\\n#]+)");
            var inlineToken = token.Success ? token.Groups["token"].Value.Trim() : null;
            if (!string.IsNullOrWhiteSpace(inlineToken)) return inlineToken;

            // SQLite is the deliberate portable mode. It keeps its generated
            // loopback credentials in token files beside the legacy config.
            var legacyToken = string.Equals(ConfigPath, LegacyConfigPath, StringComparison.OrdinalIgnoreCase)
                ? string.Equals(section, "management", StringComparison.OrdinalIgnoreCase) ? AdminTokenPath : SearchTokenPath
                : null;
            return legacyToken != null && File.Exists(legacyToken)
                ? File.ReadAllText(legacyToken).Trim()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            return await LinuxServiceIsRunningAsync(cancellationToken);
        }
        if (UsesWindowsService())
        {
            return await ServiceIsRunningAsync(cancellationToken);
        }
        foreach (var process in Process.GetProcessesByName("qindexer"))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(ExecutablePath), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private bool UsesWindowsService() => OperatingSystem.IsWindows() && ServiceExists();
    private bool CanUseWindowsService() => OperatingSystem.IsWindows() && IsElevated() && StateDirectory.Equals(MachineStateDirectory(), StringComparison.OrdinalIgnoreCase);

    private async Task EnsureLinuxServiceAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!IsElevated())
        {
            throw new InvalidOperationException("Installing or updating the Linux QIndexer service requires administrator authorization.");
        }

        var user = LinuxServiceUser();
        await RemoveLegacyLinuxSystemServiceAsync(cancellationToken);
        await RunProcessAsync("loginctl", ["enable-linger", user], cancellationToken);

        var unitPath = LinuxUserServiceUnitPath();
        var unitDirectory = Path.GetDirectoryName(unitPath)!;
        Directory.CreateDirectory(unitDirectory);
        var unit = $"""
            [Unit]
            Description=QIndexer Search Service

            [Service]
            Type=simple
            WorkingDirectory={StateDirectory}
            ExecStart={ExecutablePath} run --config {ConfigPath}
            Restart=on-failure
            RestartSec=5
            LimitNOFILE=1048576

            [Install]
            WantedBy=default.target
            """;
        await File.WriteAllTextAsync(unitPath, unit, new UTF8Encoding(false), cancellationToken);
        await RunProcessAsync("chown", ["-R", user, StateDirectory], cancellationToken);
        await RunProcessAsync("chown", ["-R", user, unitDirectory], cancellationToken);
    }

    private static async Task RemoveLegacyLinuxSystemServiceAsync(CancellationToken cancellationToken)
    {
        var legacyUnitPath = Path.Combine("/etc/systemd/system", LinuxServiceName);
        if (!File.Exists(legacyUnitPath)) return;

        await RunProcessAsync("systemctl", ["stop", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
        await RunProcessAsync("systemctl", ["disable", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
        File.Delete(legacyUnitPath);
        await RunProcessAsync("systemctl", ["daemon-reload"], cancellationToken);
    }

    private static async Task<bool> LinuxServiceIsRunningAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var result = await RunLinuxUserSystemctlAsync(["is-active", "--quiet", LinuxServiceName], cancellationToken, allowAlreadyRunning: true);
            return result.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunLinuxUserSystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowAlreadyRunning = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux user services are only available on Linux.");
        }

        if (!IsElevated())
        {
            return await RunProcessAsync("systemctl", ["--user", .. arguments], cancellationToken, allowAlreadyRunning);
        }

        var user = LinuxServiceUser();
        var uid = await LinuxServiceUserIdAsync(user, cancellationToken);
        var runtimeDirectory = $"/run/user/{uid}";
        return await RunProcessAsync(
            "runuser",
            ["-u", user, "--", "env", $"XDG_RUNTIME_DIR={runtimeDirectory}", $"DBUS_SESSION_BUS_ADDRESS=unix:path={runtimeDirectory}/bus", "systemctl", "--user", .. arguments],
            cancellationToken,
            allowAlreadyRunning);
    }

    private static async Task<string> LinuxServiceUserIdAsync(string user, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("id", ["-u", user], cancellationToken);
        var uid = result.StandardOutput.Trim();
        if (!uint.TryParse(uid, out _))
        {
            throw new InvalidOperationException("QSurfer could not determine the Linux user service identity.");
        }
        return uid;
    }

    private async Task EnsureServiceAsync(CancellationToken cancellationToken)
    {
        await EnsureNssmInstalledAsync(cancellationToken);
        if (ServiceExists())
        {
            var existing = await RunProcessAsync("sc.exe", ["qc", ServiceName], cancellationToken, allowAlreadyRunning: true);
            if (existing.ExitCode == 0 && existing.StandardOutput.Contains(NssmPath, StringComparison.OrdinalIgnoreCase))
            {
                // An update can move the managed process from the legacy
                // SQLite layout to QIndexer's package-managed PostgreSQL layout.
                // Keep the NSSM wrapper, but point it at the active payload/config.
                await RunProcessAsync(NssmPath, ["set", ServiceName, "Application", ExecutablePath], cancellationToken);
                await RunProcessAsync(NssmPath, ["set", ServiceName, "AppParameters", $"run --config {ConfigPath}"], cancellationToken);
                await RunProcessAsync(NssmPath, ["set", ServiceName, "AppDirectory", StateDirectory], cancellationToken);
                await RunProcessAsync("sc.exe", ["config", ServiceName, "start=", "auto"], cancellationToken);
                await RunProcessAsync(NssmPath, ["set", ServiceName, "AppExit", "Default", "Restart"], cancellationToken);
                return;
            }

            // The direct qindexer.exe registration cannot report service
            // startup to SCM. Remove it (or the retired QSurfer host) before
            // installing the NSSM-managed console server.
            await RunProcessAsync("sc.exe", ["delete", ServiceName], cancellationToken, allowAlreadyRunning: true);
            await StopManagedQIndexerProcessesAsync();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (ServiceExists() && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200, cancellationToken);
            }
            if (ServiceExists())
            {
                throw new InvalidOperationException("Windows has not released the previous QIndexer service yet.");
            }
        }

        await InstallNssmServiceAsync(cancellationToken);
        await RunProcessAsync(NssmPath, ["set", ServiceName, "AppDirectory", StateDirectory], cancellationToken);
        await RunProcessAsync(NssmPath, ["set", ServiceName, "Start", "SERVICE_AUTO_START"], cancellationToken);
        await RunProcessAsync(NssmPath, ["set", ServiceName, "AppExit", "Default", "Restart"], cancellationToken);
    }

    private async Task InstallNssmServiceAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                await RunProcessAsync(NssmPath, ["install", ServiceName, ExecutablePath, "run", "--config", ConfigPath], cancellationToken);
                return;
            }
            catch (InvalidOperationException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(300, cancellationToken);
            }
        }
    }

    private async Task StopManagedQIndexerProcessesAsync()
    {
        foreach (var process in Process.GetProcessesByName("qindexer"))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(ExecutablePath), StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch
            {
                // A process can exit or refuse module inspection mid-migration.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private async Task<bool> ServiceIsRunningAsync(CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("sc.exe", ["query", ServiceName], cancellationToken, allowAlreadyRunning: true);
        return result.ExitCode == 0 && result.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsNssmServiceAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(NssmPath) || !ServiceExists()) return false;
        var service = await RunProcessAsync("sc.exe", ["qc", ServiceName], cancellationToken, allowAlreadyRunning: true);
        return service.ExitCode == 0 && service.StandardOutput.Contains(NssmPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool ServiceExists()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, ArgumentList = { "query", ServiceName } });
            process?.WaitForExit(2_000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowAlreadyRunning = false)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process can exit between the state check and Kill.
            }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 && !(allowAlreadyRunning && (output + error).Contains("already", StringComparison.OrdinalIgnoreCase)))
        {
            var detail = (output + Environment.NewLine + error).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{executable} failed while managing QIndexer."
                : $"{executable} failed while managing QIndexer: {detail}");
        }
        return new ProcessResult(process.ExitCode, output);
    }

    private void CreateStateDirectories()
    {
        Directory.CreateDirectory(StateDirectory);
    }

    private async Task CreateInitialConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_usePostgres)
        {
            // The QIndexer package provisions PostgreSQL and its private DSN.
            // QSurfer never constructs, stores, or exports either value.
            await RunPackagedInstallerAsync(cancellationToken);
            await SeedLocalRootsAsync(cancellationToken);
            return;
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        EnsureToken(SearchTokenPath);
        EnsureToken(AdminTokenPath);
        var ports = FindLoopbackPorts();
        await File.WriteAllTextAsync(
            LegacyConfigPath,
            RenderSqliteConfig(ports.SearchPort, ports.AdminPort, DataDirectory, DiscoverFixedVolumes()),
            new UTF8Encoding(false),
            cancellationToken);
        RestrictToCurrentUser(LegacyConfigPath);
    }

    private async Task PreserveLegacySqliteModeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LegacyConfigPath))
        {
            return;
        }

        var config = await File.ReadAllTextAsync(LegacyConfigPath, cancellationToken);
        var indexHeader = System.Text.RegularExpressions.Regex.Match(config, @"(?m)^index:\s*\r?$");
        if (!indexHeader.Success)
        {
            throw new InvalidOperationException("The legacy QIndexer configuration has no index section.");
        }

        var nextSection = new System.Text.RegularExpressions.Regex(@"(?m)^\S[^\r\n]*:\s*$")
            .Match(config, indexHeader.Index + indexHeader.Length);
        var indexBodyEnd = nextSection.Success ? nextSection.Index : config.Length;
        var indexBody = config[indexHeader.Index..indexBodyEnd];
        if (System.Text.RegularExpressions.Regex.IsMatch(indexBody, @"(?m)^\s{2,}database:\s*$"))
        {
            return;
        }

        var lineBreak = config.IndexOf('\n', indexHeader.Index);
        var insertAt = lineBreak >= 0 ? lineBreak + 1 : config.Length;
        var lineEnding = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        config = config.Insert(insertAt, $"  database:{lineEnding}    driver: sqlite{lineEnding}");
        await File.WriteAllTextAsync(LegacyConfigPath, config, new UTF8Encoding(false), cancellationToken);
        RestrictToCurrentUser(LegacyConfigPath);
    }

    private async Task SeedLocalRootsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PackagedConfigPath))
        {
            throw new InvalidOperationException("The QIndexer package did not create its configuration.");
        }

        var config = await File.ReadAllTextAsync(PackagedConfigPath, cancellationToken);
        var roots = System.Text.RegularExpressions.Regex.Match(config, @"(?m)^roots:\s*$");
        if (!roots.Success)
        {
            throw new InvalidOperationException("The QIndexer package configuration has no roots section.");
        }

        // The QIndexer installer deliberately emits a bare roots section for
        // first run. Seed the signed-in Linux user's home only in that state;
        // never replace roots managed later through QIndexer administration.
        var existingRootSection = config[(roots.Index + roots.Length)..];
        if (!string.IsNullOrWhiteSpace(existingRootSection))
        {
            return;
        }

        // Roots are the final section of the shipped config. Preserve every
        // preceding QIndexer-owned setting, especially the provisioned DSN.
        var generated = config[..roots.Index] + "roots:" + Environment.NewLine + RenderRootEntries(DiscoverFixedVolumes());
        await File.WriteAllTextAsync(PackagedConfigPath, generated, new UTF8Encoding(false), cancellationToken);
    }

    private static string RenderSqliteConfig(int searchPort, int adminPort, string dataDirectory, IReadOnlyList<LocalIndexerVolume> volumes)
    {
        if (searchPort is < 1 or > 65535 || adminPort is < 1 or > 65535 || searchPort == adminPort)
        {
            throw new ArgumentOutOfRangeException(nameof(searchPort), "Two distinct loopback ports are required.");
        }

        return $"""
            server:
              bind: "127.0.0.1:{searchPort}"
              bind_addresses:
                - "127.0.0.1:{searchPort}"
              public_base_url: "http://127.0.0.1:{searchPort}"
              auth:
                mode: "static-token"
                token_file: "secrets/search.token"

            management:
              enabled: true
              bind: "127.0.0.1:{adminPort}"
              bind_addresses:
                - "127.0.0.1:{adminPort}"
              auth:
                mode: "admin-token"
                token_file: "secrets/admin.token"

            index:
              data_dir: "{Yaml(dataDirectory)}"
              database:
                driver: "sqlite"

            watcher:
              enabled: true

            roots:
            {RenderRootEntries(volumes)}
            """;
    }

    private static string RenderRootEntries(IReadOnlyList<LocalIndexerVolume> volumes)
    {
        var yaml = new StringBuilder();
        foreach (var volume in volumes)
        {
            yaml.AppendLine($"  - id: \"{Yaml(volume.Id)}\"");
            yaml.AppendLine($"    name: \"{Yaml(volume.Name)}\"");
            yaml.AppendLine($"    path: \"{Yaml(volume.Path)}\"");
            yaml.AppendLine("    enabled: true");
            yaml.AppendLine("    labels: [\"local\", \"fixed-volume\"]");
            yaml.AppendLine("    exclude_folder_patterns: [\"**/.git/**\", \"**/node_modules/**\", \"**/@Recently-Snapshot/**\", \"**/@Recycle/**\", \"**/#recycle/**\", \"**/$RECYCLE.BIN/**\", \"**/RECYCLER/**\"]");
        }
        return yaml.ToString();
    }

    private static void EnsureToken(string path)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(File.ReadAllText(path)))
        {
            File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'), new UTF8Encoding(false));
        }
        RestrictToCurrentUser(path);
    }

    private async Task RunPackagedInstallerAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var installer = Path.Combine(ReleaseDirectory, "scripts", "install-qindexer.ps1");
            if (!File.Exists(installer))
            {
                throw new InvalidOperationException("The QIndexer package does not include its Windows installer.");
            }
            var arguments = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installer,
                "-InstallDir", StateDirectory, "-BinaryPath", ReleaseExecutablePath,
            };
            if (!string.IsNullOrWhiteSpace(_progressPath))
            {
                arguments.Add("-ProgressPath");
                arguments.Add(_progressPath);
            }
            arguments.Add("-ServiceName");
            arguments.Add(ServiceName);
            await RunProcessAsync("powershell.exe", arguments, cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var installer = Path.Combine(ReleaseDirectory, "scripts", "install-qindexer-linux.sh");
            if (!File.Exists(installer))
            {
                throw new InvalidOperationException("The QIndexer package does not include its Linux installer.");
            }
            var arguments = new List<string> { installer, "--install-dir", StateDirectory, "--binary", ReleaseExecutablePath };
            if (!string.IsNullOrWhiteSpace(_progressPath))
            {
                arguments.Add("--progress-file");
                arguments.Add(_progressPath);
            }
            await RunProcessAsync("bash", arguments, cancellationToken);
            return;
        }

        throw new PlatformNotSupportedException("QIndexer package installation is not supported on this operating system.");
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("QSurfer could not protect a local QIndexer secret file.");
            }
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(sid)) return;
            var startInfo = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("/inheritance:r");
            startInfo.ArgumentList.Add("/grant:r");
            // icacls treats a numeric SID as an account name unless it has
            // this prefix; without it fresh elevated installs fail before
            // either token can be created.
            startInfo.ArgumentList.Add($"*{sid}:(F)");
            startInfo.ArgumentList.Add("/grant:r");
            startInfo.ArgumentList.Add("SYSTEM:(F)");
            using var process = Process.Start(startInfo);
            if (process == null || !process.WaitForExit(5_000) || process.ExitCode != 0)
            {
                throw new InvalidOperationException("QSurfer could not protect a local QIndexer secret file.");
            }
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("QSurfer could not protect a local QIndexer secret file.", exception);
        }
    }

    private static string ResolveStateDirectory()
    {
        if (OperatingSystem.IsWindows()) return MachineStateDirectory();
        if (OperatingSystem.IsLinux()) return Path.Combine(LinuxUserHome(), ".qsurfer", "QIndexer");
        return UserDataPaths.Subdirectory("QIndexer");
    }

    private static string LinuxServiceUser()
    {
        var elevatedUser = Environment.GetEnvironmentVariable("PKEXEC_UID");
        if (OperatingSystem.IsLinux() && uint.TryParse(elevatedUser, out var uid))
        {
            try
            {
                var result = RunProcessAsync("id", ["-nu", uid.ToString()], CancellationToken.None).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(result.StandardOutput)) return result.StandardOutput.Trim();
            }
            catch (InvalidOperationException)
            {
            }
        }
        return Environment.UserName;
    }

    private static string LinuxUserHome()
    {
        // Settings constructs this manager on the UI thread. A normal desktop
        // launch already has HOME; never synchronously ask NSS/getent here,
        // because directory-backed account lookups can stall the UI.
        if (!IsElevated())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home)) return home;

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile)) return userProfile;
            return Path.Combine("/home", Environment.UserName);
        }

        var user = LinuxServiceUser();
        try
        {
            var result = RunProcessAsync("getent", ["passwd", user], CancellationToken.None).GetAwaiter().GetResult();
            var fields = result.StandardOutput.Trim().Split(':');
            if (fields.Length >= 6 && !string.IsNullOrWhiteSpace(fields[5])) return fields[5];
        }
        catch (InvalidOperationException)
        {
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? "/home/" + user : profile;
    }

    private static string LinuxUserServiceUnitPath() =>
        Path.Combine(LinuxUserHome(), ".config", "systemd", "user", LinuxServiceName);

    private static string MachineStateDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(programData) ? UserDataPaths.Root : programData, "QSurfer", "QIndexer");
    }

    public static bool IsElevated()
    {
        if (OperatingSystem.IsLinux()) return geteuid() == 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QSurfer-LocalIndexerManager");
        return client;
    }

    private static string ParseChecksum(string content, string filename)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), filename, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLowerInvariant();
            }
        }
        throw new CryptographicException("SHA256SUMS does not contain the downloaded QIndexer package.");
    }

    private static string Yaml(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static int FindLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsDriveLetterRoot(string path) => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    private static string? TryGetVolumeGuid(string root)
    {
        var buffer = new StringBuilder(260);
        return GetVolumeNameForVolumeMountPoint(root, buffer, buffer.Capacity) ? buffer.ToString().TrimEnd('\\') : null;
    }

    private static string? TryGetVolumeSerial(string root)
    {
        return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0) ? $"serial-{serial:X8}" : null;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        }
        return "";
    }

    private static long ReadLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static bool ReadBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            }
        }
    }
    private static void EnsureSupported() { if (!IsSupported) throw new PlatformNotSupportedException("Local QIndexer management is supported on Windows and Linux only."); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("libc")]
    private static extern uint geteuid();

    private sealed record DownloadedRelease(string Version, string TemporaryDirectory, string PayloadDirectory);
    private sealed record ProcessResult(int ExitCode, string StandardOutput);
    private sealed record LocalIndexerAdminDetails(IReadOnlyList<LocalIndexerRootStatus> Roots, string CrawlerState, string LastError, DateTimeOffset? LastCrawl, long? IndexSizeBytes);
}
public sealed record LocalIndexerVolume(string Id, string Name, string Path, string StableIdentity);
public sealed record LocalIndexerRootStatus(string Id, string Name, string State, long DocumentCount, DateTimeOffset? LastCrawl, string Error);
public sealed record LocalIndexerHealth(LocalIndexerHealthState State, string Message);
public sealed record LocalIndexerSetupResult(string Version, string StateDirectory, bool UsesWindowsService, LocalIndexerHealth Health);

public enum LocalIndexerHealthState
{
    Unsupported,
    NotInstalled,
    ProcessUnavailable,
    HealthUnavailable,
    AuthenticationFailed,
    IndexUnavailable,
    SetupRequired,
    Healthy,
}
public sealed record LocalIndexerStatus(
    LocalIndexerHealth Health,
    string? Version,
    string StateDirectory,
    bool UsesWindowsService,
    long DocumentCount,
    long? IndexSizeBytes,
    IReadOnlyList<LocalIndexerRootStatus> Roots,
    string CrawlerState,
    string LastError,
    DateTimeOffset? LastCrawl)
{
    public static LocalIndexerStatus Unsupported() => new(new(LocalIndexerHealthState.Unsupported, "Local QIndexer management is available on Windows only."), null, "", false, 0, null, [], "", "", null);
    public static LocalIndexerStatus NotInstalled(string directory) => new(new(LocalIndexerHealthState.NotInstalled, "QIndexer is not installed."), null, directory, false, 0, null, [], "", "", null);
    public static LocalIndexerStatus ProcessUnavailable(string directory, bool service) => new(new(LocalIndexerHealthState.ProcessUnavailable, "QIndexer is installed but not running."), null, directory, service, 0, null, [], "", "", null);
    public static LocalIndexerStatus HealthUnavailable(string directory, bool service, string message) => new(new(LocalIndexerHealthState.HealthUnavailable, message), null, directory, service, 0, null, [], "", "", null);
    public static LocalIndexerStatus AuthenticationFailed(string directory, bool service) => new(new(LocalIndexerHealthState.AuthenticationFailed, "QIndexer rejected its managed loopback credential."), null, directory, service, 0, null, [], "", "", null);
    public static LocalIndexerStatus IndexUnavailable(string directory, bool service, string version, long documents) => new(new(LocalIndexerHealthState.IndexUnavailable, "QIndexer is running but its index is not ready."), version, directory, service, documents, null, [], "", "", null);
    public static LocalIndexerStatus SetupRequired(string directory, bool service) => new(new(LocalIndexerHealthState.SetupRequired, "QIndexer is running. Open advanced administration to set its first token."), null, directory, service, 0, null, [], "", "", null);
    public static LocalIndexerStatus Healthy(string directory, bool service, string version, long documents, long? indexSizeBytes, IReadOnlyList<LocalIndexerRootStatus> roots, string crawlerState, string error, DateTimeOffset? lastCrawl) => new(new(LocalIndexerHealthState.Healthy, "QIndexer is healthy."), version, directory, service, documents, indexSizeBytes, roots, crawlerState, error, lastCrawl);
}
