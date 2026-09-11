using System.Diagnostics;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher;

internal sealed record NodeRuntime(
    string ExecutablePath,
    Version Version,
    Architecture Architecture);

internal static partial class NodeRuntimeResolver
{
    public static NodeRuntime Resolve(string archivePath) =>
        ResolvePath(
            Path.Combine(NodeRuntimeInstaller.GetInstallDirectory(archivePath), "node.exe"),
            NodeRuntimeInstaller.GetArchiveVersion(
                archivePath,
                RuntimeInformation.ProcessArchitecture));

    public static NodeRuntime ResolvePath(
        string executablePath,
        Version expectedVersion)
    {
        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                CreateFailureMessage(
                    "The bundled Node.js runtime has not been extracted."));
        }

        return Resolve(
            executablePath,
            expectedVersion,
            ReadVersion,
            ReadArchitecture,
            RuntimeInformation.ProcessArchitecture);
    }

    internal static NodeRuntime Resolve(
        string executablePath,
        Version expectedVersion,
        Func<string, string> readVersion,
        Func<string, Architecture> readArchitecture,
        Architecture requiredArchitecture)
    {
        try
        {
            Version version = ParseVersion(readVersion(executablePath));
            if (version != expectedVersion)
            {
                throw new InvalidDataException(
                    $"version {version} does not match bundled version {expectedVersion}");
            }

            Architecture architecture = readArchitecture(executablePath);
            if (architecture != requiredArchitecture)
            {
                throw new InvalidDataException(
                    $"architecture {architecture} does not match {requiredArchitecture}");
            }

            return new NodeRuntime(executablePath, version, architecture);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            BadImageFormatException or
            InvalidDataException or
            InvalidOperationException or
            Win32Exception)
        {
            throw new InvalidOperationException(
                CreateFailureMessage($"{executablePath}: {exception.Message}"),
                exception);
        }
    }

    internal static Version ParseVersion(string output)
    {
        Match match = NodeVersionRegex().Match(output.Trim());
        if (
            !match.Success ||
            !Version.TryParse(match.Groups["version"].Value, out Version? version))
        {
            throw new InvalidDataException(
                $"Node.js has an invalid product version: {output.Trim()}");
        }

        return version;
    }

    internal static string CreateFailureMessage(string detail) =>
        $"{detail}{Environment.NewLine}" +
        "Run `clawctl setup` to prepare the Node.js runtime bundled " +
        "with the installed OpenClaw package.";

    private static string ReadVersion(string executablePath)
    {
        // Executing a staged image can keep it locked after process exit,
        // preventing Windows from publishing the extracted directory.
        string? version = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException("Node.js has no product version.");
        }
        return version;
    }

    private static Architecture ReadArchitecture(string executablePath)
    {
        using FileStream stream = File.OpenRead(executablePath);
        using var peReader = new PEReader(stream);
        return peReader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => Architecture.X64,
            Machine.Arm64 => Architecture.Arm64,
            Machine.I386 => Architecture.X86,
            Machine.Arm => Architecture.Arm,
            Machine machine => throw new InvalidDataException(
                $"Node.js has unsupported executable architecture 0x{(ushort)machine:X4}")
        };
    }

    [GeneratedRegex(
        @"^v?(?<version>\d+\.\d+\.\d+)(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NodeVersionRegex();
}
