using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;

namespace QSurfer.Core.Services;

public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<LogEntry> Pending = new();
    private static int _draining;
    private const long MaxBytes = 2 * 1024 * 1024;
    private const long MaxSessionLogBytes = 256 * 1024;
    private const int LogLockAttempts = 8;

    public static string LogPath
    {
        get
        {
            return Path.Combine(ConfigStore.PortableRoot, "logs", "QSurfer.log");
        }
    }

    public static string SessionLogPath => Path.Combine(ConfigStore.PortableRoot, "logs", "QSurfer.sessions.log");

    public static string SearchLogPath => Path.Combine(ConfigStore.PortableRoot, "logs", "QSurfer.search.log");

    public static string ClientLogPath => Path.Combine(ConfigStore.PortableRoot, "logs", "QSurfer.client.log");

    public static IDisposable Measure(string area, string message)
    {
        Info(area, message + " started");
        return new LogTimer(area, message);
    }

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, Exception ex, string message)
    {
        Write("ERROR", area, $"{message}: {ex}".Replace(Environment.NewLine, " | "));
    }

    public static void Session(string message)
    {
        try
        {
            var sessionPath = SessionLogPath;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SESSION] {Environment.MachineName}\\{Environment.UserName} {message}{Environment.NewLine}";
            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath) ?? AppContext.BaseDirectory);
            lock (Gate)
            {
                using var logLock = AcquireFileLock(sessionPath);
                if (logLock == null)
                {
                    return;
                }
                RotateIfNeeded(sessionPath, MaxSessionLogBytes);
                File.AppendAllText(sessionPath, line);
            }
        }
        catch
        {
        }
    }

    private static void Write(string level, string area, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{area}] {Environment.MachineName}\\{Environment.UserName} {message}{Environment.NewLine}";
            Pending.Enqueue(new LogEntry(LogPathForArea(area), line));
            if (Interlocked.Exchange(ref _draining, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }
        catch
        {
        }
    }

    private static void DrainAsync()
    {
        try
        {
            var pendingByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            while (Pending.TryDequeue(out var entry))
            {
                if (!pendingByPath.TryGetValue(entry.Path, out var lines))
                {
                    lines = [];
                    pendingByPath.Add(entry.Path, lines);
                }
                lines.Add(entry.Line);
            }

            lock (Gate)
            {
                foreach (var (logPath, lines) in pendingByPath)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory);
                    using var logLock = AcquireFileLock(logPath);
                    if (logLock == null)
                    {
                        continue;
                    }
                    RotateIfNeeded(logPath, MaxBytes);
                    using var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                    foreach (var line in lines)
                    {
                        writer.Write(line);
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            if (!Pending.IsEmpty && Interlocked.Exchange(ref _draining, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }
    }

    private static void RotateIfNeeded(string logPath, long maxBytes)
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < maxBytes)
        {
            return;
        }

        var oldPath = logPath + ".old";
        if (File.Exists(oldPath))
        {
            File.Delete(oldPath);
        }
        File.Move(logPath, oldPath);
    }

    private static FileStream? AcquireFileLock(string logPath)
    {
        var lockPath = logPath + ".lock";
        for (var attempt = 0; attempt < LogLockAttempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < LogLockAttempts - 1)
            {
                Thread.Sleep(15);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    private static string LogPathForArea(string area) => area switch
    {
        "search" or "filter" or "paint" or "rules" or "index" => SearchLogPath,
        "qsirch" => ClientLogPath,
        _ => LogPath,
    };

    private sealed record LogEntry(string Path, string Line);

    private sealed class LogTimer(string area, string message) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public void Dispose()
        {
            _stopwatch.Stop();
            Info(area, $"{message} finished in {_stopwatch.ElapsedMilliseconds}ms");
        }
    }
}
