namespace QSurfer.Core.Services;

public static class RuntimeMode
{
    public static bool IsDemo { get; private set; }

    // Keep the demonstration workspace outside a user profile so recording it cannot
    // reveal an operator name or any production folder structure.
    public static string DemoRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? Path.GetTempPath(), "QSurferDemo")
        : Path.Combine(Path.GetTempPath(), "qsurfer-demo");

    public static string DemoArchiveRoot => Path.Combine(DemoRoot, "Northstar Archive");

    public static void Configure(IEnumerable<string> arguments) =>
        IsDemo = arguments.Any(argument => argument.Equals("--demo", StringComparison.OrdinalIgnoreCase));

    public static void ResetDemoWorkspace()
    {
        if (!IsDemo)
        {
            return;
        }

        try
        {
            if (Directory.Exists(DemoRoot))
            {
                Directory.Delete(DemoRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A prior demo process can hold a preview file briefly. This launch
            // remains isolated even when its prior temporary folder is retained.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
