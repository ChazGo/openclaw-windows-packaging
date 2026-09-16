namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// One complete implementation of the public management command surface.
/// </summary>
internal sealed record GatewayCommandTarget
{
    public required Func<SetupOptions, CancellationToken, Task<int>> Setup { get; init; }

    public required Func<CancellationToken, Task<int>> Status { get; init; }

    public required Func<string?, CancellationToken, Task<int>> CollectLogs { get; init; }

    public required Func<bool, CancellationToken, Task<int>> Teardown { get; init; }

    public required Func<CancellationToken, Task<int>> PowerShell { get; init; }

    public required IGatewayLifecycle Gateway { get; init; }
}

/// <summary>
/// Selects one command implementation from persisted gateway-isolation state.
/// </summary>
internal sealed class ModeAwareCommandRouter
{
    private readonly GatewayIsolationSelection _selection;
    private readonly Func<GatewayCommandTarget> _createTarget;
    private GatewayCommandTarget? _target;
    private readonly TextWriter _output;

    public ModeAwareCommandRouter(
        GatewayIsolationSelection selection,
        Func<GatewayCommandTarget> createIsolated,
        Func<GatewayCommandTarget> createNative,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(createIsolated);
        ArgumentNullException.ThrowIfNull(createNative);
        ArgumentNullException.ThrowIfNull(output);

        _selection = selection;
        _createTarget = selection.Mode switch
        {
            GatewayIsolationMode.Enabled => createIsolated,
            GatewayIsolationMode.Disabled => createNative,
            _ => throw new GatewayIsolationException(
                "Gateway-isolation mode must be Enabled or Disabled.")
        };
        _output = output;
    }

    public ClawCtlHandlers CreateHandlers() => new()
    {
        Setup = (options, token) => Target.Setup(options, token),
        Status = GetStatusAsync,
        CollectLogs = (path, token) => Target.CollectLogs(path, token),
        Teardown = (force, token) => Target.Teardown(force, token),
        PowerShell = token => Target.PowerShell(token),
        GatewayStart = StartGatewayAsync,
        GatewayStatus = GetGatewayStatusAsync,
        GatewayStop = StopGatewayAsync
    };

    private async Task<int> StartGatewayAsync(CancellationToken cancellationToken)
    {
        GatewayLifecycleStartResult result = await Target.Gateway
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await WriteModeAsync().ConfigureAwait(false);
        await _output.WriteLineAsync(result.Message).ConfigureAwait(false);
        return result.State == GatewayState.Running ? 0 : 1;
    }

    private async Task<int> GetStatusAsync(CancellationToken cancellationToken)
    {
        await WriteModeAsync().ConfigureAwait(false);
        return await Target.Status(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> GetGatewayStatusAsync(CancellationToken cancellationToken)
    {
        GatewayLifecycleStatus result = await Target.Gateway
            .GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await WriteModeAsync().ConfigureAwait(false);
        await _output.WriteLineAsync(result.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            await _output.WriteLineAsync(result.Detail).ConfigureAwait(false);
        }

        return result.State is GatewayState.Running or GatewayState.NotStarted
            ? 0
            : 1;
    }

    private async Task<int> StopGatewayAsync(CancellationToken cancellationToken)
    {
        GatewayStopResult result = await Target.Gateway
            .StopAsync(cancellationToken).ConfigureAwait(false);
        await WriteModeAsync().ConfigureAwait(false);
        await _output.WriteLineAsync(result.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            await _output.WriteLineAsync(result.Detail).ConfigureAwait(false);
        }

        return result.Succeeded ? 0 : 1;
    }

    private Task WriteModeAsync() =>
        _output.WriteLineAsync(
            $"Gateway isolation: {GatewayIsolationPolicy.EnvironmentValue(_selection.Mode)}.");

    private GatewayCommandTarget Target => _target ??= _createTarget();
}
