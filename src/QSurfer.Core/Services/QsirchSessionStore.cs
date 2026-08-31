using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

internal sealed class QsirchSessionStore
{
    private static readonly TimeSpan MaxSessionAge = TimeSpan.FromDays(1);
    private readonly byte[] _entropy;
    private readonly string _path;

    public QsirchSessionStore(AppConfig config)
    {
        var identity = $"{(config.Ssl ? "https" : "http")}://{config.Host.Trim().ToUpperInvariant()}:{config.Port}/{config.User.Trim().ToUpperInvariant()}";
        var sessionId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        _entropy = Encoding.UTF8.GetBytes($"QSurfer.QsirchSession.v1|{identity}");
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QSurfer",
            "sessions",
            $"{sessionId}.bin");
    }

    public string? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(_path);
            var payload = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            var record = JsonSerializer.Deserialize<SessionRecord>(payload);
            if (record == null ||
                string.IsNullOrWhiteSpace(record.SessionId) ||
                DateTimeOffset.UtcNow - record.StoredAtUtc > MaxSessionAge)
            {
                Clear();
                return null;
            }

            return record.SessionId;
        }
        catch (Exception)
        {
            Clear();
            return null;
        }
    }

    public void Save(string sessionId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new SessionRecord(sessionId, DateTimeOffset.UtcNow));
            var encrypted = ProtectedData.Protect(payload, _entropy, DataProtectionScope.CurrentUser);
            var temporaryPath = _path + ".tmp";
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Session reuse is an optimization. Failed local persistence must never block search.
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed record SessionRecord(string SessionId, DateTimeOffset StoredAtUtc);
}
