using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

// Native preview handlers are more reliable against a local read-only copy than
// a mapped drive or UNC stream. Cache entries are keyed to the source file version.
public static class PreviewFileStager
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const long MaximumCacheBytes = 256L * 1024 * 1024;
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QSurfer", "preview-cache");

    public static async Task<string> StageIfRemoteAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var source = await Task.Run(() => Inspect(sourcePath), cancellationToken);
        if (source == null || !source.IsRemote || source.Length > MaximumFileBytes)
        {
            return sourcePath;
        }

        await CacheGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var extension = Path.GetExtension(sourcePath);
            var cachePath = Path.Combine(CacheDirectory, BuildCacheFileName(sourcePath, source.Length, source.LastWriteUtc, extension));
            if (File.Exists(cachePath))
            {
                File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
                return cachePath;
            }

            var stopwatch = Stopwatch.StartNew();
            var temporaryPath = cachePath + ".partial";
            try
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken);
                File.Move(temporaryPath, cachePath, true);
                File.SetAttributes(cachePath, FileAttributes.ReadOnly);
                AppLogger.Info("preview", $"staged remote preview bytes={source.Length} elapsed={stopwatch.ElapsedMilliseconds}ms path=\"{sourcePath}\"");
                _ = Task.Run(CleanCache);
                return cachePath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("preview", $"remote staging skipped path=\"{sourcePath}\" error=\"{ex.Message}\"");
            return sourcePath;
        }
        finally
        {
            CacheGate.Release();
        }
    }

    private static SourceFile? Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileInfo(path);
        return new SourceFile(file.Length, file.LastWriteTimeUtc, IsRemotePath(path));
    }

    private static bool IsRemotePath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCacheFileName(string path, long length, DateTime modifiedUtc, string extension)
    {
        var source = $"{path}|{length}|{modifiedUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hash + extension;
    }

    private static void CleanCache()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).EnumerateFiles()
                .Where(file => !file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastAccessTimeUtc)
                .ToList();
            long retainedBytes = 0;
            foreach (var file in files)
            {
                retainedBytes += file.Length;
                if (retainedBytes <= MaximumCacheBytes && file.LastAccessTimeUtc >= DateTime.UtcNow.AddDays(-3))
                {
                    continue;
                }

                file.IsReadOnly = false;
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private sealed record SourceFile(long Length, DateTime LastWriteUtc, bool IsRemote);
}
