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

    public Func<CancellationToken, Task<int>>? GatewayStatus { get; init; }

    public Func<CancellationToken, Task<int>>? GatewayStop { get; init; }
}

/// <summary>
/// Selects one command implementation from persisted gateway-isolation state.
/// </summary>
internal sealed class ModeAwareCommandRouter
{
    private readonly Lazy<GatewayIsolationSelection> _selection;
    private readonly Func<SetupOptions, CancellationToken, Task<int>> _setup;
    private readonly Func<GatewayCommandTarget> _createIsolated;
    private readonly Func<GatewayCommandTarget> _createNative;
    private GatewayCommandTarget? _target;
    private readonly TextWriter _output;

    public ModeAwareCommandRouter(
        Func<GatewayIsolationSelection> resolveSelection,
        Func<SetupOptions, CancellationToken, Task<int>> setup,
        Func<GatewayCommandTarget> createIsolated,
        Func<GatewayCommandTarget> createNative,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(resolveSelection);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(createIsolated);
        ArgumentNullException.ThrowIfNull(createNative);
        ArgumentNullException.ThrowIfNull(output);

        _selection = new Lazy<GatewayIsolationSelection>(resolveSelection);
        _setup = setup;
        _createIsolated = createIsolated;
        _createNative = createNative;
        _output = output;
    }

    public ClawCtlHandlers CreateHandlers() => new()
    {
        Setup = _setup,
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
        if (Target.GatewayStatus is not null)
        {
            await WriteModeAsync().ConfigureAwait(false);
            return await Target.GatewayStatus(cancellationToken).ConfigureAwait(false);
        }

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
        if (Target.GatewayStop is not null)
        {
            await WriteModeAsync().ConfigureAwait(false);
            return await Target.GatewayStop(cancellationToken).ConfigureAwait(false);
        }

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
            $"Gateway isolation: {GatewayIsolationPolicy.EnvironmentValue(Selection.Mode)}.");

    private GatewayIsolationSelection Selection => _selection.Value;

    private GatewayCommandTarget Target => _target ??= Selection.Mode switch
    {
        GatewayIsolationMode.Enabled => _createIsolated(),
        GatewayIsolationMode.Disabled => _createNative(),
        _ => throw new GatewayIsolationException(
            "Gateway-isolation mode must be Enabled or Disabled.")
    };
}
