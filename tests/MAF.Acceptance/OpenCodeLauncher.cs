public static class OpenCodeLauncher
{
    // Npm's legal Windows .cmd/.ps1 launchers point to the packaged native executable.
    // Resolve that executable so the captured PID is the server itself, not a shell parent.
    public static string ResolveNative(string launcher)
    {
        var path = Path.GetFullPath(launcher);
        if (!File.Exists(path)) throw new FileNotFoundException("OpenCode launcher not found");
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
        if (Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".ps1" or "")
        {
            var native = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "opencode-ai", "bin", "opencode.exe");
            if (File.Exists(native) && File.ReadAllText(path).Contains("opencode-ai", StringComparison.Ordinal)) return native;
        }
        throw new ArgumentException("Unsupported launcher layout: pass the actual native OpenCode executable. No global PATH changes are needed.");
    }
}
