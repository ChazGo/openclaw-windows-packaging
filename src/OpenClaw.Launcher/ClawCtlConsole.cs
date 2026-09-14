namespace OpenClaw.Launcher;

internal static class ClawCtlConsole
{
    internal static void WriteNodeRuntimeSummary(
        TextWriter output,
        NodeRuntime runtime) =>
        output.WriteLine(
            $"Bundled Node.js {runtime.Version} is ready at {runtime.ExecutablePath}");

    public static void WriteReadinessSummary(
        TextWriter output,
        string applicationDirectory)
    {
        output.WriteLine();
        output.WriteLine("OpenClaw package is ready.");
        output.WriteLine($"Read-only application files: {applicationDirectory}");
    }
}
