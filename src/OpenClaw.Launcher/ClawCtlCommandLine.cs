using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace OpenClaw.Launcher;

internal sealed record ClawCtlHandlers
{
    public required Func<SetupOptions, CancellationToken, Task<int>> Setup { get; init; }
    public required Func<CancellationToken, Task<int>> Status { get; init; }
    public required Func<string?, CancellationToken, Task<int>> CollectLogs { get; init; }
    public required Func<bool, CancellationToken, Task<int>> Teardown { get; init; }
    public required Func<CancellationToken, Task<int>> PowerShell { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStart { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStatus { get; init; }
    public required Func<CancellationToken, Task<int>> GatewayStop { get; init; }
    public Func<CancellationToken, Task<int>> GatewayIsolationStatus { get; init; } =
        _ => Task.FromResult(1);
    public Func<CancellationToken, Task<int>> GatewayIsolationEnable { get; init; } =
        _ => Task.FromResult(1);
    public Func<CancellationToken, Task<int>> GatewayIsolationDisable { get; init; } =
        _ => Task.FromResult(1);
}

internal sealed record SetupOptions(bool Fresh, bool Force, bool NoIsolation = false);

// The clawctl command tree. Only the package-readiness surface belongs here:
// doctor, gateway, uninstall, and every other OpenClaw command is owned by the
// bundled CLI and reached through `openclaw`, which forwards its arguments
// without parsing them.
internal static class ClawCtlCommandLine
{
    public const string SetupCommandName = "setup";
    public const string StatusCommandName = "status";
    public const string CollectLogsCommandName = "collect-logs";

    // Response-file expansion is off. A leading `@` means nothing to clawctl,
    // so it is reported as an unrecognized argument instead of silently reading
    // a file from disk. `openclaw` already forwards such a token to the
    // OpenClaw CLI untouched, and leaving the two entrypoints consistent
    // matters more than the convenience. Completion stays enabled.
    public static ParserConfiguration CreateParserConfiguration() => new()
    {
        ResponseFileTokenReplacer = null
    };

    // Setup guidance is available without preparing the runtime.
    public static string RootDescription =>
        "Set up and manage the packaged OpenClaw application." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `clawctl setup` to prepare the selected gateway-isolation mode." +
        Environment.NewLine +
        Environment.NewLine +
        "Run `openclaw <arguments>` to invoke the OpenClaw CLI.";

    public static string SetupDescription =>
        "Extract or repair the bundled Node.js runtime in package LocalState " +
        "and prepare the selected gateway-isolation mode.";

    // runSetup stays a delegate so the command tree owns parsing and help while
    // Program keeps the readiness operation and its test seams.
    public static RootCommand Create(ClawCtlHandlers handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        Command setup = new(SetupCommandName, SetupDescription);
        Option<bool> noIsolation = new("--no-isolation")
        {
            Description = "Select direct signed-in-user setup when no mode is recorded, or repair an existing Disabled setup."
        };
        Option<bool> fresh = new("--fresh")
        {
            Description = "Reset resources owned by the selected setup mode before setting it up again."
        };
        setup.Options.Add(noIsolation);
        Option<bool> force = new("--force")
        {
            Description = "Continue with package-local cleanup when owned external cleanup cannot be confirmed. Requires --fresh."
        };
        setup.Options.Add(fresh);
        setup.Options.Add(force);
        setup.Validators.Add(result =>
        {
            if (result.GetValue(force) && !result.GetValue(fresh))
            {
                result.AddError("Option '--force' requires option '--fresh'.");
            }
        });
        setup.SetAction((parsed, cancellationToken) =>
            handlers.Setup(
                new SetupOptions(
                    parsed.GetValue(fresh),
                    parsed.GetValue(force),
                    parsed.GetValue(noIsolation)),
                cancellationToken));
        Command status = new(
            StatusCommandName,
            "Show the selected gateway-isolation mode and its recorded state without changing it.");
        status.SetAction((_, cancellationToken) => handlers.Status(cancellationToken));
        Option<string?> outputPath = new("--output")
        {
            Description = "Path for the diagnostics ZIP file."
        };
        Command collectLogs = new(
            CollectLogsCommandName,
            "Create a redacted diagnostics bundle for the selected mode.");
        collectLogs.Options.Add(outputPath);
        collectLogs.SetAction((parsed, cancellationToken) =>
            handlers.CollectLogs(parsed.GetValue(outputPath), cancellationToken));
        Option<bool> teardownForce = new("--force") { Description = "Confirm removal of resources owned by the selected mode." };
        Command teardown = new("teardown", "Remove resources owned by the selected mode.");
        teardown.Options.Add(teardownForce);
        teardown.SetAction((parsed, cancellationToken) =>
            handlers.Teardown(parsed.GetValue(teardownForce), cancellationToken));
        Command powerShell = new(
            "pwsh",
            "Open an interactive PowerShell session inside the isolated agent.");
        powerShell.SetAction((_, cancellationToken) => handlers.PowerShell(cancellationToken));
        Command gateway = new(
            "gateway-service",
            "Manage the background OpenClaw gateway for the selected mode.");
        Command gatewayStart = new("start", "Start the gateway if it is not running.");
        gatewayStart.SetAction((_, token) => handlers.GatewayStart(token));
        Command gatewayStatus = new("status", "Show the gateway state without changing it.");
        gatewayStatus.SetAction((_, token) => handlers.GatewayStatus(token));
        Command gatewayStop = new("stop", "Stop the gateway, keeping the session and its data.");
        gatewayStop.SetAction((_, token) => handlers.GatewayStop(token));
        gateway.Subcommands.Add(gatewayStart);
        gateway.Subcommands.Add(gatewayStatus);
        gateway.Subcommands.Add(gatewayStop);
        Command gatewayIsolation = new(
            "gateway-isolation",
            "Inspect or change whether OpenClaw runs in its isolated identity.");
        Command isolationStatus = new(
            "status",
            "Show the selected identity and its readiness without changing it.");
        isolationStatus.SetAction((_, token) =>
            handlers.GatewayIsolationStatus(token));
        Command isolationEnable = new(
            "enable",
            "Provision and select the isolated OpenClaw identity.");
        isolationEnable.SetAction((_, token) =>
            handlers.GatewayIsolationEnable(token));
        Command isolationDisable = new(
            "disable",
            "Confirm, then select the signed-in Windows user identity.");
        isolationDisable.SetAction((_, token) =>
            handlers.GatewayIsolationDisable(token));
        gatewayIsolation.Subcommands.Add(isolationStatus);
        gatewayIsolation.Subcommands.Add(isolationEnable);
        gatewayIsolation.Subcommands.Add(isolationDisable);

        RootCommand root = new(RootDescription)
        {
            setup,
            status,
            collectLogs,
            teardown,
            powerShell,
            gateway,
            gatewayIsolation
        };

        // Bare `clawctl` is a discovery request, not a usage error, so the root
        // prints help and succeeds instead of reporting a missing command.
        root.SetAction((parseResult, _) => Task.FromResult(WriteHelp(parseResult)));
        UseLauncherVersion(root);

        return root;
    }

    private static int WriteHelp(ParseResult parseResult)
    {
        HelpAction help = new();
        return help.Invoke(parseResult);
    }

    // The built-in version action reports the entry assembly, which is the test
    // or scenario host rather than the launcher. Report the launcher assembly so
    // the value identifies the shipped package binary in every host.
    private static void UseLauncherVersion(RootCommand root)
    {
        foreach (Option option in root.Options)
        {
            if (option is VersionOption versionOption)
            {
                versionOption.Action = new LauncherVersionAction();
            }
        }
    }

    private sealed class LauncherVersionAction : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            string version = typeof(LauncherVersionAction).Assembly
                .GetName()
                .Version?
                .ToString() ?? "unknown";
            parseResult.InvocationConfiguration.Output.WriteLine(version);
            return 0;
        }
    }
}
