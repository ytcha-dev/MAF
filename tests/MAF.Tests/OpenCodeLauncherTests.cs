namespace MAF.Tests;

public sealed class OpenCodeLauncherTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-launcher-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("opencode.cmd")]
    [InlineData("opencode.ps1")]
    [InlineData("opencode")]
    public void NpmWindowsLaunchersResolveToNativeChild(string name)
    {
        var native = Path.Combine(root, "node_modules", "opencode-ai", "bin", "opencode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "synthetic placeholder; never executed");
        var launcher = Path.Combine(root, name);
        File.WriteAllText(launcher, "node_modules/opencode-ai/bin/opencode.exe");
        Assert.Equal(native, OpenCodeLauncher.ResolveNative(launcher));
        Assert.Equal(native, OpenCodeLauncher.ResolveNative(native));
    }
    [Fact]
    public void MissingNativeOrUnknownWrapperFailsWithoutExecutingShell()
    {
        Directory.CreateDirectory(root);
        var launcher = Path.Combine(root, "opencode.cmd");
        File.WriteAllText(launcher, "unknown wrapper");
        Assert.Throws<ArgumentException>(() => OpenCodeLauncher.ResolveNative(launcher));
        Assert.Throws<FileNotFoundException>(() => OpenCodeLauncher.ResolveNative(Path.Combine(root, "missing.exe")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
