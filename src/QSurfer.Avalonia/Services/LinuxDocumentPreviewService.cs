using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Docnet.Core;
using Docnet.Core.Models;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

// Linux has no equivalent to Windows preview-handler registration. Keep document
// previews desktop-neutral: PDFium renders PDFs and LibreOffice produces PDFs for
// office formats using a private QSurfer profile.
public static class LinuxDocumentPreviewService
{
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "dot", "dotx", "odt", "rtf",
        "xls", "xlsx", "xlsm", "xlt", "xltx", "ods", "csv",
        "ppt", "pptx", "pptm", "pot", "potx", "odp",
    };

    private static readonly SemaphoreSlim OfficeConversionGate = new(1, 1);
    private static readonly string CacheDirectory = UserDataPaths.Subdirectory("document-preview-cache");
    private static readonly string ProfileDirectory = UserDataPaths.Subdirectory("libreoffice-preview-profile");

    public static bool Supports(string extension)
    {
        extension = extension.Trim().TrimStart('.');
        return extension.Equals("pdf", StringComparison.OrdinalIgnoreCase) || OfficeExtensions.Contains(extension);
    }

    public static async Task<Bitmap?> TryRenderAsync(string sourcePath, string extension, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !Supports(extension))
        {
            return null;
        }

        try
        {
            var pdfPath = extension.Trim().TrimStart('.').Equals("pdf", StringComparison.OrdinalIgnoreCase)
                ? sourcePath
                : await ConvertOfficeDocumentAsync(sourcePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                return null;
            }

            return await Task.Run(() => RenderFirstPage(pdfPath), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("preview", $"Linux document preview failed path=\"{sourcePath}\" reason=\"{ex.Message}\"");
            return null;
        }
    }

    private static async Task<string?> ConvertOfficeDocumentAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
        {
            return null;
        }

        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ProfileDirectory);
        var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
        var outputDirectory = Path.Combine(CacheDirectory, cacheKey);
        var expectedPdf = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(source.Name) + ".pdf");
        if (File.Exists(expectedPdf))
        {
            File.SetLastAccessTimeUtc(expectedPdf, DateTime.UtcNow);
            return expectedPdf;
        }

        await OfficeConversionGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(expectedPdf))
            {
                return expectedPdf;
            }

            Directory.CreateDirectory(outputDirectory);
            var started = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "soffice",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--nologo");
            process.StartInfo.ArgumentList.Add("--nodefault");
            process.StartInfo.ArgumentList.Add("--norestore");
            process.StartInfo.ArgumentList.Add("-env:UserInstallation=" + new Uri(ProfileDirectory + Path.DirectorySeparatorChar).AbsoluteUri);
            process.StartInfo.ArgumentList.Add("--convert-to");
            process.StartInfo.ArgumentList.Add("pdf");
            process.StartInfo.ArgumentList.Add("--outdir");
            process.StartInfo.ArgumentList.Add(outputDirectory);
            process.StartInfo.ArgumentList.Add(source.FullName);

            if (!process.Start())
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0 || !File.Exists(expectedPdf))
            {
                AppLogger.Warn("preview", $"LibreOffice conversion failed path=\"{sourcePath}\" code={process.ExitCode} output=\"{output.Trim()}\" error=\"{error.Trim()}\"");
                return null;
            }

            AppLogger.Info("preview", $"LibreOffice converted path=\"{sourcePath}\" elapsed={started.ElapsedMilliseconds}ms");
            return expectedPdf;
        }
        finally
        {
            OfficeConversionGate.Release();
        }
    }

    private static Bitmap RenderFirstPage(string pdfPath)
    {
        using var document = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1000, 1400));
        if (document.GetPageCount() == 0)
        {
            throw new InvalidDataException("The PDF has no pages.");
        }

        using var page = document.GetPageReader(0);
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var pixels = page.GetImage();
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
        }
        return bitmap;
    }
}
