using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Tests;

public sealed class NodeRuntimeResolverTests
{
    [Theory]
    [InlineData("24.16.0", Architecture.X64)]
    [InlineData("26.1.0", Architecture.X64)]
    [InlineData("26.1.0", Architecture.Arm64)]
    public void ResolveAcceptsSelectedRuntime(string version, Architecture architecture)
    {
        NodeRuntime runtime = Resolve(
            _ => $"v{version}",
            architecture,
            architecture,
            version);

        Assert.Equal("C:\\Node\\node.exe", runtime.ExecutablePath);
        Assert.Equal(Version.Parse(version), runtime.Version);
        Assert.Equal(architecture, runtime.Architecture);
    }

    [Fact]
    public void ResolveReportsSetupCommandWhenRuntimeIsMissing()
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => NodeRuntimeResolver.ResolvePath(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "node.exe"),
                new Version(26, 1, 0)));

        Assert.Contains("has not been extracted", exception.Message, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("24.14.0")]
    [InlineData("24.17.0")]
    public void ResolveRejectsRuntimeDifferentFromArchive(string version)
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => Resolve(
                _ => $"v{version}",
                Architecture.X64,
                Architecture.X64));

        Assert.Contains(
            $"version {version} does not match bundled version 24.16.0",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsMalformedVersion()
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => Resolve(
                _ => "not-node",
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("invalid product version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsPrereleaseVersion()
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => Resolve(
                _ => "v24.15.0-rc.1",
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("invalid product version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRejectsIncompatibleArchitecture()
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => Resolve(
                _ => "v24.16.0",
                Architecture.Arm64,
                Architecture.X64));

        Assert.Contains("architecture Arm64 does not match X64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveReportsVersionReadFailure()
    {
        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => Resolve(
                _ => throw new IOException("version read failed"),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("version read failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("clawctl setup", exception.Message, StringComparison.Ordinal);
    }

    private static NodeRuntime Resolve(
        Func<string, string> readVersion,
        Architecture candidateArchitecture,
        Architecture requiredArchitecture,
        string expectedVersion = "24.16.0") =>
        NodeRuntimeResolver.Resolve(
            "C:\\Node\\node.exe",
            Version.Parse(expectedVersion),
            readVersion,
            _ => candidateArchitecture,
            requiredArchitecture);
}
