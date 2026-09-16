using System.CommandLine;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class ModeAwareCommandRouterTests
{
    [Fact]
    public async Task SetupUsesItsIntentWithoutResolvingOrdinaryMode()
    {
        bool setupCalled = false;
        var router = new ModeAwareCommandRouter(
            () => throw new GatewayIsolationException(
                "Ordinary mode must not be resolved for setup."),
            (_, _) =>
            {
                setupCalled = true;
                return Task.FromResult(0);
            },
            () => throw new InvalidOperationException("Isolated target is not expected."),
            () => throw new InvalidOperationException("Native target is not expected."),
            TextWriter.Null);

        RootCommand command = ClawCtlCommandLine.Create(router.CreateHandlers());
        int exitCode = await command.Parse(
                ["setup", "--no-isolation"],
                ClawCtlCommandLine.CreateParserConfiguration())
            .InvokeAsync(new InvocationConfiguration
            {
                Output = TextWriter.Null,
                Error = TextWriter.Null,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = null
            }).ConfigureAwait(true);

        Assert.Equal(0, exitCode);
        Assert.True(setupCalled);
    }

    public static TheoryData<string[]> Commands => new()
    {
        { ["setup"] },
        { ["status"] },
        { ["collect-logs"] },
        { ["teardown", "--force"] },
        { ["pwsh"] },
        { ["gateway-service", "start"] },
        { ["gateway-service", "status"] },
        { ["gateway-service", "stop"] },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task EnabledRoutesEveryCommandOnlyToIsolatedTarget(string[] args)
    {
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        using var output = new StringWriter();

        int exitCode = await InvokeAsync(
            GatewayIsolationMode.Enabled,
            isolated,
            native,
            output,
            args);

        Assert.Equal(0, exitCode);
        Assert.Equal([CommandName(args)], isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(1, isolated.CreateCount);
        Assert.Equal(0, native.CreateCount);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task DisabledRoutesEveryCommandOnlyToNativeTarget(string[] args)
    {
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        using var output = new StringWriter();

        int exitCode = await InvokeAsync(
            GatewayIsolationMode.Disabled,
            isolated,
            native,
            output,
            args);

        Assert.Equal(0, exitCode);
        Assert.Empty(isolated.Calls);
        Assert.Equal([CommandName(args)], native.Calls);
        Assert.Equal(0, isolated.CreateCount);
        Assert.Equal(1, native.CreateCount);
    }

    [Theory]
    [InlineData("Enabled", "enabled")]
    [InlineData("Disabled", "disabled")]
    public async Task ReadOnlyStatusReportsSelectedModeWithoutCallingMutation(
        string modeValue,
        string expected)
    {
        GatewayIsolationMode mode = Enum.Parse<GatewayIsolationMode>(modeValue);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        using var output = new StringWriter();

        int exitCode = await InvokeAsync(
            mode,
            isolated,
            native,
            output,
            ["status"]);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            $"Gateway isolation: {expected}.",
            output.ToString(),
            StringComparison.Ordinal);
        RecordingTarget selected =
            mode == GatewayIsolationMode.Enabled ? isolated : native;
        Assert.Equal(["status"], selected.Calls);
    }

    [Fact]
    public async Task DisabledPowerShellFailureDirectsUserToOrdinaryPowerShell()
    {
        var isolated = new RecordingTarget();
        var native = new RecordingTarget
        {
            PowerShellFailure = new OpenClaw.Launcher.Session.SessionException(
                "Gateway isolation is disabled. Open ordinary PowerShell to run commands as the signed-in user.")
        };

        OpenClaw.Launcher.Session.SessionException exception =
            await Assert.ThrowsAsync<OpenClaw.Launcher.Session.SessionException>(() =>
                InvokeAsync(
                    GatewayIsolationMode.Disabled,
                    isolated,
                    native,
                    TextWriter.Null,
                    ["pwsh"])).ConfigureAwait(true);

        Assert.Contains("ordinary PowerShell", exception.Message, StringComparison.Ordinal);
        Assert.Empty(isolated.Calls);
        Assert.Equal(["pwsh"], native.Calls);
    }

    private static async Task<int> InvokeAsync(
        GatewayIsolationMode mode,
        RecordingTarget isolated,
        RecordingTarget native,
        TextWriter output,
        string[] args)
    {
        var router = new ModeAwareCommandRouter(
            () => new GatewayIsolationSelection(mode, "test selection"),
            (options, token) => (mode == GatewayIsolationMode.Enabled
                ? isolated.Create()
                : native.Create()).Setup(options, token),
            isolated.Create,
            native.Create,
            output);
        RootCommand command = ClawCtlCommandLine.Create(router.CreateHandlers());
        return await command.Parse(
                args,
                ClawCtlCommandLine.CreateParserConfiguration())
            .InvokeAsync(new InvocationConfiguration
            {
                Output = output,
                Error = TextWriter.Null,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = null
            }).ConfigureAwait(false);
    }

    private static string CommandName(string[] args) =>
        args[0] == "gateway-service"
            ? $"gateway-service {args[1]}"
            : args[0];

    private sealed class RecordingTarget
    {
        public List<string> Calls { get; } = [];

        public Exception? PowerShellFailure { get; init; }

        public int CreateCount { get; private set; }

        public GatewayCommandTarget Create()
        {
            CreateCount++;
            return new GatewayCommandTarget
            {
                Setup = (_, _) => RecordAsync("setup"),
                Status = _ => RecordAsync("status"),
                CollectLogs = (_, _) => RecordAsync("collect-logs"),
                Teardown = (_, _) => RecordAsync("teardown"),
                PowerShell = _ =>
                {
                    Calls.Add("pwsh");
                    return PowerShellFailure is null
                        ? Task.FromResult(0)
                        : Task.FromException<int>(PowerShellFailure);
                },
                Gateway = new RecordingGatewayLifecycle(Calls)
            };
        }

        private Task<int> RecordAsync(string command)
        {
            Calls.Add(command);
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingGatewayLifecycle(List<string> calls)
        : IGatewayLifecycle
    {
        public Task<GatewayLifecycleStatus> GetStatusAsync(
            CancellationToken cancellationToken)
        {
            calls.Add("gateway-service status");
            return Task.FromResult(new GatewayLifecycleStatus(
                GatewayState.NotStarted,
                "not started"));
        }

        public Task<GatewayLifecycleStartResult> StartAsync(
            CancellationToken cancellationToken)
        {
            calls.Add("gateway-service start");
            return Task.FromResult(new GatewayLifecycleStartResult(
                GatewayState.Running,
                AlreadyRunning: false,
                "started"));
        }

        public Task<GatewayStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            calls.Add("gateway-service stop");
            return Task.FromResult(new GatewayStopResult(
                Stopped: true,
                "stopped"));
        }
    }
}
