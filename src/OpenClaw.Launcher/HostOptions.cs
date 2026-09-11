namespace OpenClaw.Launcher;

internal sealed record HostOptions(
    string? PackagedApplicationDirectory,
    string? PackagedNodeArchivePath,
    IReadOnlyList<string> OpenClawArguments)
{
    public static HostOptions Parse(IReadOnlyList<string> arguments) =>
        Parse(arguments, AppContext.BaseDirectory);

    internal static HostOptions Parse(
        IReadOnlyList<string> arguments,
        string baseDirectory)
    {
        // openclaw.mjs presence is the sole readiness signal: MSIX enforces
        // package read-only integrity, so no separate hash/marker check is
        // needed here. Null (rather than throwing) defers the "not found"
        // failure to call sites, which can report it with more context.
        string packagedApplicationDirectory = Path.Combine(
            baseDirectory,
            "app");
        string? directApplicationDirectory = File.Exists(Path.Combine(
            packagedApplicationDirectory,
            "openclaw.mjs"))
                ? packagedApplicationDirectory
                : null;
        string packagedNodeArchivePath = Path.Combine(
            baseDirectory,
            "runtime",
            NodeRuntimeInstaller.GetArchiveFileName(
                System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture));

        return new HostOptions(
            directApplicationDirectory,
            File.Exists(packagedNodeArchivePath)
                ? packagedNodeArchivePath
                : null,
            [.. arguments]);
    }
}
