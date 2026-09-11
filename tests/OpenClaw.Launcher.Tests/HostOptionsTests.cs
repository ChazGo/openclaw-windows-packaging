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

    [Fact]
    public void ParseResolvesArchitectureSpecificPackagedNodeArchive()
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string archivePath = Path.Combine(
            runtimeDirectory,
            NodeRuntimeInstaller.GetArchiveFileName(
                System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture));
        File.WriteAllText(archivePath, "fixture");

        HostOptions options = HostOptions.Parse([], _testDirectory);

        Assert.Equal(archivePath, options.PackagedNodeArchivePath);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
