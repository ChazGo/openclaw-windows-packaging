namespace OpenClaw.Launcher.Tests;

public sealed class HostOptionsTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void ParseForwardsAllArgumentsUnchanged()
    {
        HostOptions options = HostOptions.Parse(
        [
            "--host-payload", "payload.tar.gz",
            "--host-node", "test-node.exe",
            "--",
            "gateway", "run", "--port", "12345"
        ]);

        Assert.Equal(
            [
                "--host-payload", "payload.tar.gz",
                "--host-node", "test-node.exe",
                "--",
                "gateway", "run", "--port", "12345"
            ],
            options.OpenClawArguments);
    }

    [Fact]
    public void ParseReportsMissingPackagedApplication()
    {
        HostOptions options = HostOptions.Parse([], _testDirectory);

        Assert.Null(options.PackagedApplicationDirectory);
        Assert.Null(options.PackagedNodeArchivePath);
        Assert.Empty(options.OpenClawArguments);
    }

    [Fact]
    public void ParseResolvesPackagedApplicationWhenEntryPointExists()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");

        HostOptions options = HostOptions.Parse(
            ["gateway", "run"],
            _testDirectory);

        Assert.Equal(
            applicationDirectory,
            options.PackagedApplicationDirectory);
        Assert.Null(options.PackagedNodeArchivePath);
        Assert.Equal(["gateway", "run"], options.OpenClawArguments);
    }

    [Theory]
    [InlineData("24.16.0")]
    [InlineData("26.1.0")]
    public void ParseResolvesArchitectureSpecificPackagedNodeArchive(string version)
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string architecture = System.Runtime.InteropServices.RuntimeInformation
            .ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
                ? "x64"
                : "arm64";
        string archivePath = Path.Combine(
            runtimeDirectory,
            $"node-v{version}-win-{architecture}.zip");
        File.WriteAllText(archivePath, "fixture");

        HostOptions options = HostOptions.Parse([], _testDirectory);

        Assert.Equal(archivePath, options.PackagedNodeArchivePath);
    }

    [Fact]
    public void ArchiveDiscoveryRejectsAmbiguousVersions()
    {
        File.WriteAllText(Path.Combine(_testDirectory, "node-v24.16.0-win-x64.zip"), "fixture");
        File.WriteAllText(Path.Combine(_testDirectory, "node-v26.1.0-win-x64.zip"), "fixture");

        Assert.Throws<InvalidDataException>(() =>
            NodeRuntimeInstaller.FindArchivePath(
                _testDirectory,
                System.Runtime.InteropServices.Architecture.X64));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
