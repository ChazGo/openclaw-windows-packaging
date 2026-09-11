using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Tests;

public sealed class NodeRuntimeInstallerTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void EnsureInstalledExtractsArchitectureRuntimeOnce()
    {
        Architecture architecture = Architecture.X64;
        string archivePath = CreateRuntimeArchive(architecture);
        string installDirectory = Path.Combine(_testDirectory, "installed");

        string firstPath = NodeRuntimeInstaller.EnsureInstalled(
            archivePath,
            installDirectory,
            architecture);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(firstPath);
        string secondPath = NodeRuntimeInstaller.EnsureInstalled(
            archivePath,
            installDirectory,
            architecture);

        Assert.Equal(Path.Combine(installDirectory, "node.exe"), firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.Equal("fixture-node", File.ReadAllText(firstPath));
        Assert.Equal(firstWriteTime, File.GetLastWriteTimeUtc(secondPath));
    }

    [Fact]
    public void EnsureInstalledRejectsEntriesOutsideExpectedRuntimeRoot()
    {
        string archivePath = Path.Combine(_testDirectory, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(
            archivePath,
            ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("unexpected/node.exe");
            using StreamWriter writer = new(entry.Open());
            writer.Write("fixture-node");
        }

        Assert.Throws<InvalidDataException>(() =>
            NodeRuntimeInstaller.EnsureInstalled(
                archivePath,
                Path.Combine(_testDirectory, "unsafe-install"),
                Architecture.X64));
    }

    [Fact]
    public void EnsureInstalledReplacesIncompleteRuntimeDirectory()
    {
        Architecture architecture = Architecture.X64;
        string archivePath = CreateRuntimeArchive(architecture);
        string installDirectory = Path.Combine(_testDirectory, "incomplete");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(
            Path.Combine(installDirectory, "partial.txt"),
            "partial");

        string executablePath = NodeRuntimeInstaller.EnsureInstalled(
            archivePath,
            installDirectory,
            architecture);

        Assert.True(File.Exists(executablePath));
        Assert.False(File.Exists(
            Path.Combine(installDirectory, "partial.txt")));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateRuntimeArchive(Architecture architecture)
    {
        string archivePath = Path.Combine(
            _testDirectory,
            NodeRuntimeInstaller.GetArchiveFileName(architecture));
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
        writer.Write("fixture-node");
        return archivePath;
    }
}
