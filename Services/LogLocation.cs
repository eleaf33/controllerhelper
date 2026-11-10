namespace ControllerHelper.Services;

internal static class LogLocation
{
    internal static string DirectoryPath => Resolve(AppContext.BaseDirectory);

    internal static string Resolve(string applicationDirectory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        string? parent = Path.GetDirectoryName(root);
        // Published payload lives in lib; the user starts the launcher one level up.
        if (parent is not null && Path.GetFileName(root).Equals("lib", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent, "ControllerHelper.exe")))
            root = parent;
        return Path.Combine(root, "logs");
    }
}
