namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlConsoleTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void WriteHelpListsOnlyThePublicCommands()
    {
        using var output = new StringWriter();

        ClawCtlConsole.WriteHelp(output);

        string help = output.ToString();
        Assert.Contains("setup", help, StringComparison.Ordinal);
        Assert.Contains("readiness", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--version", help, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "prepare",
            help,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"{Environment.NewLine}  verify",
            help,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"{Environment.NewLine}  repair",
            help,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Extract Node.js", help, StringComparison.Ordinal);
        Assert.DoesNotContain("update-package", help, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway-service", help, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReadinessSummaryDescribesPackagedApplication()
    {
        using var output = new StringWriter();
        string applicationDirectory = Path.Combine(_testDirectory, "app");

        ClawCtlConsole.WriteReadinessSummary(
            output,
            applicationDirectory);

        string summary = output.ToString();
        Assert.Contains("package is ready", summary, StringComparison.Ordinal);
        Assert.Contains(applicationDirectory, summary, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
