namespace MAF.Host;

internal static class DurableDatabasePath
{
    // Production data must be explicitly configured outside Temp and source repositories.
    // Test fixtures use the store directly; this does not relocate any existing tests.
    public static string Validate(string path, string projectRoot)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetFileName(path)))
            throw new ArgumentException("Execution database requires an absolute local file path.");
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Within(string parent) => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))
            + Path.DirectorySeparatorChar, comparison) || full.Equals(Path.GetFullPath(parent), comparison);
        if (Within(Path.GetTempPath()) || Within(projectRoot))
            throw new ArgumentException("Persistent execution database must be outside Temp and the target source repository.");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new ArgumentException("Execution database must use an available local drive.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)!); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Execution database ancestors must not be links.");
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))
                || Directory.Exists(Path.Combine(directory.FullName, ".svn")))
                throw new ArgumentException("Execution database must be outside source repositories.");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Execution database must not be a link.");
        if (Directory.Exists(full + ".locks") && (File.GetAttributes(full + ".locks") & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Execution ownership directory must not be a link.");
        return full;
    }
}
