namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Target-neutral gateway lifecycle used by routing layers.
/// </summary>
internal interface IGatewayLifecycle
{
    Task<GatewayLifecycleStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<GatewayLifecycleStartResult> StartAsync(CancellationToken cancellationToken);

    Task<GatewayStopResult> StopAsync(CancellationToken cancellationToken);
}

internal sealed record GatewayLifecycleStatus(
    GatewayState State,
    string Message,
    string? Detail = null);

internal sealed record GatewayLifecycleStartResult(
    GatewayState State,
    bool AlreadyRunning,
    string Message);

/// <summary>Adapts the existing isolated controller without changing its guest contract.</summary>
internal sealed class SessionGatewayLifecycle(
    GatewayController controller,
    string helperPath) : IGatewayLifecycle
{
    public async Task<GatewayLifecycleStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        GatewayStatusReport result = await controller
            .GetStatusAsync(helperPath, cancellationToken)
            .ConfigureAwait(false);
        return new GatewayLifecycleStatus(result.State, result.Message, result.Detail);
    }

    public async Task<GatewayLifecycleStartResult> StartAsync(
        CancellationToken cancellationToken)
    {
        GatewayStartResult result = await controller
            .StartAsync(helperPath, cancellationToken)
            .ConfigureAwait(false);
        return new GatewayLifecycleStartResult(
            result.State,
            result.AlreadyRunning,
            result.Message);
    }

    public Task<GatewayStopResult> StopAsync(CancellationToken cancellationToken) =>
        controller.StopAsync(helperPath, cancellationToken);
}

internal sealed class LazyGatewayLifecycle(
    Func<IGatewayLifecycle> create) : IGatewayLifecycle
{
    private IGatewayLifecycle? _value;

    private IGatewayLifecycle Value => _value ??= create();

    public Task<GatewayLifecycleStatus> GetStatusAsync(
        CancellationToken cancellationToken) =>
        Value.GetStatusAsync(cancellationToken);

    public Task<GatewayLifecycleStartResult> StartAsync(
        CancellationToken cancellationToken) =>
        Value.StartAsync(cancellationToken);

    public Task<GatewayStopResult> StopAsync(
        CancellationToken cancellationToken) =>
        Value.StopAsync(cancellationToken);
}
