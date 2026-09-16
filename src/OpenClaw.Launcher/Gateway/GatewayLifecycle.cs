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

/// <summary>The exact target record from which a lifecycle status was derived.</summary>
internal abstract record GatewayLifecycleSource
{
    public abstract string? LogPath { get; }
}

internal sealed record SessionGatewayLifecycleSource(
    GatewayRecord Record) : GatewayLifecycleSource
{
    public override string? LogPath => Record.LogPath;
}

internal sealed record NativeGatewayLifecycleSource(
    NativeGatewayRecord Record) : GatewayLifecycleSource
{
    public override string LogPath => Record.LogPath;
}

internal sealed record GatewayLifecycleStatus(
    GatewayState State,
    string Message,
    string? Detail = null,
    GatewayLifecycleSource? Source = null);

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
        return new GatewayLifecycleStatus(
            result.State,
            result.Message,
            result.Detail,
            result.Record is null
                ? null
                : new SessionGatewayLifecycleSource(result.Record));
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
