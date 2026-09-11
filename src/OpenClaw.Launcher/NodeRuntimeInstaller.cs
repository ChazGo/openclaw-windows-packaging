using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OpenClaw.Launcher;

internal static class NodeRuntimeInstaller
{
    public const string Version = "24.16.0";

    public static string GetArchiveFileName(Architecture architecture) =>
        $"node-v{Version}-win-{GetArchitectureName(architecture)}.zip";

    public static string GetInstallDirectory(Architecture architecture) =>
        Path.Combine(
            HostDataPaths.GetProductLocalStateRoot(),
            "NodeJS",
            $"node-v{Version}-win-{GetArchitectureName(architecture)}");

    public static string GetExecutablePath(Architecture architecture) =>
        Path.Combine(GetInstallDirectory(architecture), "node.exe");

    public static async Task<NodeRuntime> EnsureInstalledAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        string executablePath = EnsureInstalled(
            archivePath,
            GetInstallDirectory(architecture),
            architecture);
        return await NodeRuntimeResolver.ResolvePathAsync(
            executablePath,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string EnsureInstalled(
        string archivePath,
        string installDirectory,
        Architecture architecture)
    {
        string executablePath = Path.Combine(installDirectory, "node.exe");
        if (File.Exists(executablePath))
        {
            return executablePath;
        }

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The packaged Node.js runtime archive was not found.",
                archivePath);
        }

        string mutexName =
            $"Local\\OpenClawGatewayMSIX.NodeRuntime.{GetArchitectureName(architecture)}";
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (File.Exists(executablePath))
            {
                return executablePath;
            }

            string? parentDirectory = Path.GetDirectoryName(installDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new InvalidOperationException(
                    "The Node.js installation path has no parent directory.");
            }

            Directory.CreateDirectory(parentDirectory);
            string stagingDirectory =
                $"{installDirectory}.{Guid.NewGuid():N}.extract";
            try
            {
                ExtractRuntime(
                    archivePath,
                    stagingDirectory,
                    architecture);
                if (!File.Exists(Path.Combine(stagingDirectory, "node.exe")))
                {
                    throw new InvalidDataException(
                        "The packaged Node.js archive does not contain node.exe.");
                }

                if (Directory.Exists(installDirectory))
                {
                    Directory.Delete(installDirectory, recursive: true);
                }
                Directory.Move(stagingDirectory, installDirectory);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }

            return executablePath;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void ExtractRuntime(
        string archivePath,
        string stagingDirectory,
        Architecture architecture)
    {
        string archiveRoot =
            $"node-v{Version}-win-{GetArchitectureName(architecture)}";
        string archivePrefix = archiveRoot + "/";
        string fullStagingDirectory =
            Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(stagingDirectory);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string archivePathName = entry.FullName.Replace('\\', '/');
            if (string.Equals(
                archivePathName,
                archiveRoot + "/",
                StringComparison.Ordinal))
            {
                continue;
            }

            if (!archivePathName.StartsWith(
                archivePrefix,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unexpected Node.js archive entry: {entry.FullName}");
            }

            bool isDirectory = string.IsNullOrEmpty(entry.Name);
            string relativePath = archivePathName[archivePrefix.Length..];
            if (isDirectory)
            {
                relativePath = relativePath.TrimEnd('/');
            }
            string[] segments = relativePath.Split('/');
            if (
                string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Contains(':', StringComparison.Ordinal) ||
                segments.Contains(string.Empty, StringComparer.Ordinal) ||
                segments.Contains(".", StringComparer.Ordinal) ||
                segments.Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsafe Node.js archive entry: {entry.FullName}");
            }

            string destinationPath = Path.GetFullPath(
                Path.Combine(stagingDirectory, relativePath));
            if (!destinationPath.StartsWith(
                fullStagingDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Unsafe Node.js archive entry: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidDataException(
                    $"Invalid Node.js archive entry: {entry.FullName}");
            }

            Directory.CreateDirectory(destinationDirectory);
            entry.ExtractToFile(destinationPath);
        }
    }

    private static string GetArchitectureName(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Node.js runtime packaging does not support {architecture}.")
        };
}
