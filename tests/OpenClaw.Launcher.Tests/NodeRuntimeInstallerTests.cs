using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Tests;

public sealed class NodeRuntimeInstallerTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Theory]
    [InlineData("24.16.0", Architecture.X64)]
    [InlineData("26.1.0", Architecture.X64)]
    [InlineData("26.1.0", Architecture.Arm64)]
    public void EnsureInstalledExtractsSelectedRuntimeOnce(
        string version,
        Architecture architecture)
    {
        string archivePath = CreateRuntimeArchive(version, architecture);
        string installDirectory = Path.Combine(_testDirectory, "installed");

        NodeRuntime first = EnsureInstalled(
            archivePath,
            installDirectory);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(first.ExecutablePath);
        NodeRuntime second = EnsureInstalled(
            archivePath,
            installDirectory);

        Assert.Equal(Path.Combine(installDirectory, "node.exe"), first.ExecutablePath);
        Assert.Equal(Version.Parse(version), first.Version);
        Assert.Equal(architecture, first.Architecture);
        Assert.Equal(first, second);
        Assert.Equal("fixture-node", File.ReadAllText(first.ExecutablePath));
        Assert.Equal(firstWriteTime, File.GetLastWriteTimeUtc(second.ExecutablePath));
        Assert.Equal(
            Version.Parse(version),
            NodeRuntimeInstaller.GetArchiveVersion(archivePath, architecture));
        Assert.EndsWith(
            Path.GetFileNameWithoutExtension(archivePath),
            NodeRuntimeInstaller.GetInstallDirectory(archivePath),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unexpected/node.exe")]
    [InlineData("{root}/../outside.txt")]
    [InlineData("{root}/nested/../../outside.txt")]
    public void EnsureInstalledRejectsUnsafeArchiveEntries(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        string archivePath = CreateRuntimeArchive("26.1.0", Architecture.X64);
        using (ZipArchive archive = ZipFile.Open(
            archivePath,
            ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName.Replace(
                "{root}",
                Path.GetFileNameWithoutExtension(archivePath),
                StringComparison.Ordinal));
            using StreamWriter writer = new(entry.Open());
            writer.Write("fixture-node");
        }

        string installDirectory = Path.Combine(_testDirectory, "unsafe-install");
        Assert.Throws<InvalidDataException>(() =>
            EnsureInstalled(archivePath, installDirectory));
        Assert.False(Directory.Exists(installDirectory));
        Assert.False(Directory.Exists($"{installDirectory}.extract"));
    }

    [Fact]
    public void EnsureInstalledReplacesIncompleteRuntimeDirectory()
    {
        string archivePath = CreateRuntimeArchive("26.1.0", Architecture.X64);
        string installDirectory = Path.Combine(_testDirectory, "incomplete");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(
            Path.Combine(installDirectory, "partial.txt"),
            "partial");

        NodeRuntime runtime = EnsureInstalled(archivePath, installDirectory);

        Assert.True(File.Exists(runtime.ExecutablePath));
        Assert.False(File.Exists(
            Path.Combine(installDirectory, "partial.txt")));
    }

    [Fact]
    public void EnsureInstalledRepairsInvalidExecutableAndThenReusesIt()
    {
        string archivePath = CreateRuntimeArchive("26.1.0", Architecture.X64);
        string installDirectory = Path.Combine(_testDirectory, "invalid");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "node.exe"), "invalid-node");
        var messages = new List<string>();

        NodeRuntime runtime = EnsureInstalled(archivePath, installDirectory, messages.Add);
        DateTime writeTime = File.GetLastWriteTimeUtc(runtime.ExecutablePath);
        NodeRuntime reused = EnsureInstalled(archivePath, installDirectory, messages.Add);

        Assert.Equal("fixture-node", File.ReadAllText(runtime.ExecutablePath));
        Assert.Equal(runtime, reused);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(reused.ExecutablePath));
        Assert.Contains("Reinstalling invalid bundled Node.js", Assert.Single(messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureInstalledDoesNotPublishInvalidRuntimeAndCanRetry()
    {
        string archivePath = CreateRuntimeArchive(
            "26.1.0",
            Architecture.X64,
            "invalid-node");
        string installDirectory = Path.Combine(_testDirectory, "retry");

        Assert.Throws<InvalidOperationException>(() =>
            EnsureInstalled(archivePath, installDirectory));
        Assert.False(Directory.Exists(installDirectory));
        Assert.False(Directory.Exists($"{installDirectory}.extract"));

        File.Delete(archivePath);
        CreateRuntimeArchive("26.1.0", Architecture.X64);
        NodeRuntime runtime = EnsureInstalled(archivePath, installDirectory);

        Assert.Equal("fixture-node", File.ReadAllText(runtime.ExecutablePath));
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void EnsureInstalledClearsInterruptedExtraction(Architecture architecture)
    {
        string archivePath = CreateRuntimeArchive("26.1.0", architecture);
        string installDirectory = Path.Combine(_testDirectory, "interrupted");
        string stagingDirectory = $"{installDirectory}.extract";
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "node.exe"), "partial-node");

        NodeRuntime runtime = EnsureInstalled(archivePath, installDirectory);

        Assert.Equal("fixture-node", File.ReadAllText(runtime.ExecutablePath));
        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task ConcurrentSetupPublishesOnlyOnce()
    {
        string archivePath = CreateRuntimeArchive("26.1.0", Architecture.X64);
        string installDirectory = Path.Combine(_testDirectory, "concurrent");
        int publications = 0;
        using var start = new Barrier(3);

        NodeRuntime Install()
        {
            start.SignalAndWait();
            return NodeRuntimeInstaller.EnsureInstalled(
                archivePath,
                installDirectory,
                path =>
                {
                    if (path == Path.Combine($"{installDirectory}.extract", "node.exe"))
                    {
                        Interlocked.Increment(ref publications);
                    }
                    return ResolveFixture(path, archivePath);
                },
                _ => { });
        }

        Task<NodeRuntime> first = Task.Run(Install);
        Task<NodeRuntime> second = Task.Run(Install);
        start.SignalAndWait();
        NodeRuntime[] runtimes = await Task.WhenAll(first, second);

        Assert.Equal(1, publications);
        Assert.Equal(runtimes[0], runtimes[1]);
        Assert.False(Directory.Exists($"{installDirectory}.extract"));
    }

    [Fact]
    public void InstallationLockIsGlobalAndScopedToItsDirectory()
    {
        string first = Path.Combine(_testDirectory, "first");
        string second = Path.Combine(_testDirectory, "second");
        string name = NodeRuntimeInstaller.GetInstallMutexName(first);

        Assert.StartsWith("Global\\", name, StringComparison.Ordinal);
        Assert.Equal(name, NodeRuntimeInstaller.GetInstallMutexName(first.ToUpperInvariant()));
        Assert.NotEqual(name, NodeRuntimeInstaller.GetInstallMutexName(second));
    }

    [Theory]
    [InlineData("node-v26-win-x64.zip")]
    [InlineData("node-v26.1.0-rc.1-win-x64.zip")]
    [InlineData("node-v26.1.0-win-arm64.zip")]
    public void ArchiveVersionRejectsMalformedOrWrongArchitectureNames(string name)
    {
        Assert.Throws<InvalidDataException>(() =>
            NodeRuntimeInstaller.GetArchiveVersion(name, Architecture.X64));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static NodeRuntime EnsureInstalled(
        string archivePath,
        string installDirectory,
        Action<string>? log = null) =>
        NodeRuntimeInstaller.EnsureInstalled(
            archivePath,
            installDirectory,
            path => ResolveFixture(path, archivePath),
            log ?? (_ => { }));

    private static NodeRuntime ResolveFixture(string path, string archivePath)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != "fixture-node")
        {
            throw new InvalidOperationException("Invalid fixture runtime.");
        }

        Architecture architecture = archivePath.EndsWith("-win-x64.zip", StringComparison.Ordinal)
            ? Architecture.X64
            : Architecture.Arm64;
        return new NodeRuntime(
            path,
            NodeRuntimeInstaller.GetArchiveVersion(archivePath, architecture),
            architecture);
    }

    private string CreateRuntimeArchive(
        string version,
        Architecture architecture,
        string nodeContent = "fixture-node")
    {
        string architectureName = architecture == Architecture.X64 ? "x64" : "arm64";
        string archivePath = Path.Combine(
            _testDirectory,
            $"node-v{version}-win-{architectureName}.zip");
        string rootName = Path.GetFileNameWithoutExtension(archivePath);
        using ZipArchive archive = ZipFile.Open(
            archivePath,
            ZipArchiveMode.Create);
        archive.CreateEntry($"{rootName}/node_modules/");
        ZipArchiveEntry nestedEntry = archive.CreateEntry(
            $"{rootName}/node_modules/package.json");
        using (StreamWriter nestedWriter = new(nestedEntry.Open()))
        {
            nestedWriter.Write("{}");
        }
        ZipArchiveEntry entry = archive.CreateEntry($"{rootName}/node.exe");
        using StreamWriter writer = new(entry.Open());
        writer.Write(nodeContent);
        return archivePath;
    }
}
