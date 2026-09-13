using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Converters;

public sealed class FileIconBrushConverter : IValueConverter
{
    private static readonly IBrush Folder = Brush("#E0A83A");
    private static readonly IBrush Drive = Brush("#43A7DD");
    private static readonly IBrush Home = Brush("#47C5B1");
    private static readonly IBrush Recycle = Brush("#E76B6B");
    private static readonly IBrush Document = Brush("#4AA8F0");
    private static readonly IBrush Spreadsheet = Brush("#36B87C");
    private static readonly IBrush Presentation = Brush("#F08B4E");
    private static readonly IBrush Pdf = Brush("#E66565");
    private static readonly IBrush Image = Brush("#B57DE8");
    private static readonly IBrush Archive = Brush("#D6A443");
    private static readonly IBrush Media = Brush("#4BC1C8");
    private static readonly IBrush Generic = Brush("#7EAED4");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NavigationTreeNode { IsHome: true } => Home,
        NavigationTreeNode { IsRecoveryFolder: true } => Recycle,
        NavigationTreeNode { IsDrive: true } => Drive,
        NavigationTreeNode => Folder,
        BrowserItem { IsRecycleBinFolder: true } => Recycle,
        BrowserItem { IsDrive: true } => Drive,
        BrowserItem { IsFolder: true } => Folder,
        BrowserItem item => ForExtension(Path.GetExtension(item.Name)),
        FavoriteTreeNode { IsFolder: true } => Folder,
        FavoriteTreeNode { SavedSearch: not null } => Home,
        FavoriteTreeNode { Result: { } result } => ForResult(result),
        SearchResult result => ForResult(result),
        _ => Generic
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static IBrush ForResult(SearchResult result) => result.IsFolder ? Folder : ForExtension(result.Extension);

    private static IBrush ForExtension(string? extension) => (extension ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" => Pdf,
        "doc" or "docx" or "odt" or "rtf" => Document,
        "xls" or "xlsx" or "ods" or "csv" => Spreadsheet,
        "ppt" or "pptx" or "odp" => Presentation,
        "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tif" or "tiff" => Image,
        "zip" or "rar" or "7z" or "tar" or "gz" => Archive,
        "mp3" or "wav" or "flac" or "m4a" or "mp4" or "mkv" or "avi" or "mov" or "webm" => Media,
        _ => Generic
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
