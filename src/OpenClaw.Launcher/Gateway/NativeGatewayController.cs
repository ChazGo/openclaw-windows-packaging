using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

internal sealed record NativeGatewayLaunchRequest(
    string NodePath,
    string EntryPointPath,
    string WorkingDirectory,
    string PackageGeneration,
    string OwnerSid,
    int WindowsSessionId,
    int? Port,
    string LogPath);

internal sealed record NativeGatewayLaunchOutcome(
    int ProcessId,
    DateTimeOffset ProcessCreationTimeUtc);

internal sealed record NativeGatewayProcessSnapshot
{
    public bool ProcessFound { get; init; }

    public DateTimeOffset? ProcessCreationTimeUtc { get; init; }

    public string? OwnerSid { get; init; }

    public int? WindowsSessionId { get; init; }

    public string? ExecutablePath { get; init; }

    public string? PackageGeneration { get; init; }

    public IReadOnlyList<string>? CommandLineArguments { get; init; }

    public IReadOnlyList<int>? ListeningPorts { get; init; }

    public string? Error { get; init; }
}

internal interface INativeGatewayProcess
{
    Task<NativeGatewayLaunchOutcome> LaunchAsync(
        NativeGatewayLaunchRequest request,
        CancellationToken cancellationToken);

    Task<NativeGatewayProcessSnapshot> InspectAsync(
        int processId,
        CancellationToken cancellationToken);

    Task<NativeGatewayProcessSnapshot> StopAsync(
        NativeGatewayRecord record,
        CancellationToken cancellationToken);
}

internal sealed record NativeGatewayHealthResult(bool Healthy, string? Error = null);

internal interface INativeGatewayHealthProbe
{
    Task<NativeGatewayHealthResult> ProbeAsync(
        int port,
        CancellationToken cancellationToken);
}

/// <summary>Owns a gateway running directly as the signed-in Windows user.</summary>
internal sealed class NativeGatewayController : IGatewayLifecycle
{
    private readonly NativeGatewayStateStore _store;
    private readonly INativeGatewayProcess _process;
    private readonly INativeGatewayHealthProbe _health;
    private readonly Func<NativeGatewayLaunchRequest> _requestFactory;
    private readonly Func<string> _currentGeneration;
    private readonly ISessionLock _lifecycleLock;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;

    public NativeGatewayController(
        NativeGatewayStateStore store,
        INativeGatewayProcess process,
        INativeGatewayHealthProbe health,
        Func<NativeGatewayLaunchRequest> requestFactory,
        Func<string> currentGeneration,
        ISessionLock lifecycleLock,
        Action<string> log,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(currentGeneration);
        ArgumentNullException.ThrowIfNull(lifecycleLock);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _process = process;
        _health = health;
        _requestFactory = requestFactory;
        _currentGeneration = currentGeneration;
        _lifecycleLock = lifecycleLock;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GatewayLifecycleStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        NativeGatewayStateResult state = _store.Read();
        if (state.Record is null)
        {
            return state.Fault == NativeGatewayStateFault.Missing
                ? new GatewayLifecycleStatus(
                    GatewayState.NotStarted,
                    "No signed-in-user gateway has been started.")
                : new GatewayLifecycleStatus(
                    GatewayState.Unknown,
                    "The signed-in-user gateway record could not be used.",
                    state.Detail);
        }

        if (state.Record.LaunchPending)
        {
            return new GatewayLifecycleStatus(
                GatewayState.Unknown,
                "A signed-in-user gateway launch is pending or was not confirmed.",
                "The launch intent was retained; do not start a replacement.",
                new NativeGatewayLifecycleSource(state.Record));
        }

        NativeGatewayInspection inspection = await InspectAsync(
            state.Record,
            cancellationToken).ConfigureAwait(false);
        return Describe(state.Record, inspection);
    }

    public async Task<GatewayLifecycleStartResult> StartAsync(
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();
        NativeGatewayStateResult state = _store.Read();
        if (state.Record is null && state.Fault != NativeGatewayStateFault.Missing)
        {
            throw new SessionException(
                $"The signed-in-user gateway record could not be used: {state.Detail}");
        }

        if (state.Record is { LaunchPending: true })
        {
            throw new SessionException(
                "A signed-in-user gateway launch is pending or was not confirmed. " +
                "The retained intent must be diagnosed before another launch.");
        }

        if (state.Record is { } existing)
        {
            NativeGatewayInspection inspection = await InspectAsync(
                existing,
                cancellationToken).ConfigureAwait(false);
            if (inspection.IsAmbiguous)
            {
                throw new SessionException(
                    "The signed-in-user gateway ownership could not be verified, " +
                    $"so another gateway was not started: {inspection.Detail}");
            }

            if (inspection.ProcessIdentityMatches)
            {
                if (inspection.GenerationMatches && inspection.Healthy)
                {
                    _log("The signed-in-user gateway is already running.");
                    return new GatewayLifecycleStartResult(
                        GatewayState.Running,
                        AlreadyRunning: true,
                        "The signed-in-user gateway is already running.");
                }

                throw new SessionException(
                    inspection.GenerationMatches
                        ? "The owned signed-in-user gateway is alive but unhealthy. " +
                          "Stop it before starting another."
                        : "A gateway from an older package generation is still running. " +
                          "Stop it before starting this generation.");
            }

            _log("The recorded signed-in-user gateway is no longer running.");
        }

        NativeGatewayLaunchRequest request = _requestFactory();
        DateTimeOffset intentTime = _clock.GetUtcNow();
        var pending = new NativeGatewayRecord
        {
            LaunchPending = true,
            OwnerSid = request.OwnerSid,
            WindowsSessionId = request.WindowsSessionId,
            NodePath = Path.GetFullPath(request.NodePath),
            EntryPointPath = Path.GetFullPath(request.EntryPointPath),
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            PackageGeneration = request.PackageGeneration,
            ConfiguredPort = request.Port,
            LogPath = Path.GetFullPath(request.LogPath),
            IntentCreatedUtc = intentTime
        };
        _store.Write(pending);

        NativeGatewayLaunchOutcome outcome = await _process
            .LaunchAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.ProcessId <= 0 || outcome.ProcessCreationTimeUtc == default)
        {
            throw new SessionException(
                "The signed-in-user gateway launch did not return complete process " +
                "identity, so the pending intent was retained.");
        }

        var confirmed = pending with
        {
            LaunchPending = false,
            ProcessId = outcome.ProcessId,
            ProcessCreationTimeUtc = outcome.ProcessCreationTimeUtc,
            StartedUtc = _clock.GetUtcNow()
        };

        NativeGatewayInspection observed = await InspectAsync(
            confirmed,
            cancellationToken).ConfigureAwait(false);
        confirmed = confirmed with { ObservedPorts = observed.Ports };

        // If publication fails, the earlier pending intent remains and blocks a
        // duplicate launch until an operator diagnoses the uncertain outcome.
        _store.Write(confirmed);
        GatewayState resultState = observed.IsAmbiguous ? GatewayState.Unknown
            : !observed.ProcessIdentityMatches ? GatewayState.Stopped
            : observed.Healthy ? GatewayState.Running
            : GatewayState.Starting;

        return new GatewayLifecycleStartResult(
            resultState,
            AlreadyRunning: false,
            resultState switch
            {
                GatewayState.Running => $"The signed-in-user gateway is running on " +
                    $"{DescribePorts(confirmed, observed.Ports)}.",
                GatewayState.Starting =>
                    "The signed-in-user gateway started but is not healthy yet.",
                GatewayState.Unknown =>
                    $"The signed-in-user gateway launch could not be verified: " +
                    $"{observed.Detail}",
                _ => "The signed-in-user gateway exited during startup."
            });
    }

    public async Task<GatewayStopResult> StopAsync(CancellationToken cancellationToken)
    {
        using ISessionLockHandle handle = AcquireLock();
        NativeGatewayStateResult state = _store.Read();
        if (state.Record is null)
        {
            return new GatewayStopResult(
                Stopped: false,
                "No signed-in-user gateway was recorded, so there was nothing to stop.",
                state.Fault == NativeGatewayStateFault.Missing ? null : state.Detail,
                state.Fault == NativeGatewayStateFault.Missing);
        }

        if (state.Record.LaunchPending)
        {
            return new GatewayStopResult(
                Stopped: false,
                "The pending launch could not be stopped safely; its intent was retained.",
                "No process identity was published.",
                Succeeded: false);
        }

        NativeGatewayInspection inspection = await InspectAsync(
            state.Record,
            cancellationToken).ConfigureAwait(false);
        if (inspection.IsAmbiguous)
        {
            return new GatewayStopResult(
                Stopped: false,
                "The signed-in-user gateway could not be stopped because ownership " +
                "could not be verified.",
                inspection.Detail,
                Succeeded: false);
        }

        if (!inspection.ProcessIdentityMatches)
        {
            _store.Clear();
            return new GatewayStopResult(
                Stopped: false,
                "The recorded signed-in-user gateway was no longer running, so its " +
                "record was removed.");
        }

        NativeGatewayProcessSnapshot stopped = await _process
            .StopAsync(state.Record, cancellationToken)
            .ConfigureAwait(false);
        if (stopped.Error is not null || stopped.ProcessFound)
        {
            return new GatewayStopResult(
                Stopped: false,
                "The signed-in-user gateway stop could not be verified; its record " +
                "was retained.",
                stopped.Error,
                Succeeded: false);
        }

        _store.Clear();
        return new GatewayStopResult(
            Stopped: true,
            "The signed-in-user gateway is stopped.");
    }

    private async Task<NativeGatewayInspection> InspectAsync(
        NativeGatewayRecord record,
        CancellationToken cancellationToken)
    {
        bool generationMatches;
        try
        {
            generationMatches = string.Equals(
                record.PackageGeneration,
                _currentGeneration(),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or SessionException)
        {
            return NativeGatewayInspection.Ambiguous(exception.Message);
        }

        NativeGatewayProcessSnapshot snapshot;
        try
        {
            snapshot = await _process
                .InspectAsync(record.ProcessId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            return NativeGatewayInspection.Ambiguous(exception.Message);
        }

        if (snapshot.Error is not null)
        {
            return NativeGatewayInspection.Ambiguous(snapshot.Error);
        }

        if (!snapshot.ProcessFound)
        {
            return NativeGatewayInspection.Absent(generationMatches);
        }

        if (snapshot.ProcessCreationTimeUtc != record.ProcessCreationTimeUtc)
        {
            return NativeGatewayInspection.Absent(
                generationMatches,
                "The recorded process identifier has been reused.");
        }

        string? mismatch = FindIdentityMismatch(record, snapshot);
        if (mismatch is not null)
        {
            return NativeGatewayInspection.Ambiguous(mismatch);
        }

        IReadOnlyList<int> ports = snapshot.ListeningPorts ?? [];
        int expectedPort = record.ConfiguredPort
            ?? (ports.Count > 0
                ? ports[0]
                : GatewayLaunchConfiguration.UpstreamDefaultPort);
        NativeGatewayHealthResult health = await _health
            .ProbeAsync(expectedPort, cancellationToken)
            .ConfigureAwait(false);
        bool listenerOwned = ports.Contains(expectedPort);
        return new NativeGatewayInspection(
            ProcessIdentityMatches: true,
            GenerationMatches: generationMatches,
            Healthy: listenerOwned && health.Healthy,
            Ports: ports,
            Detail: health.Error ??
                (!listenerOwned
                    ? $"The recorded process does not own loopback port {expectedPort}."
                    : null));
    }

    private static string? FindIdentityMismatch(
        NativeGatewayRecord record,
        NativeGatewayProcessSnapshot snapshot)
    {
        if (!string.Equals(snapshot.OwnerSid, record.OwnerSid, StringComparison.Ordinal))
        {
            return "The process owner SID does not match the recorded owner.";
        }
        if (snapshot.WindowsSessionId != record.WindowsSessionId)
        {
            return "The Windows session does not match the recorded session.";
        }
        if (!PathsEqual(snapshot.ExecutablePath, record.NodePath))
        {
            return "The process executable does not match the recorded Node.js runtime.";
        }
        if (!string.Equals(
                snapshot.PackageGeneration,
                record.PackageGeneration,
                StringComparison.Ordinal))
        {
            return "The process package generation does not match the recorded generation.";
        }
        if (snapshot.CommandLineArguments is null ||
            !snapshot.CommandLineArguments.Any(
                argument => PathsEqual(argument, record.EntryPointPath)))
        {
            return "The process command line does not contain the recorded packaged entry point.";
        }

        return null;
    }

    private static GatewayLifecycleStatus Describe(
        NativeGatewayRecord record,
        NativeGatewayInspection inspection)
    {
        if (inspection.IsAmbiguous)
        {
            return new GatewayLifecycleStatus(
                GatewayState.Unknown,
                "The signed-in-user gateway state could not be established.",
                inspection.Detail,
                new NativeGatewayLifecycleSource(record));
        }

        if (!inspection.ProcessIdentityMatches)
        {
            return new GatewayLifecycleStatus(
                inspection.GenerationMatches ? GatewayState.Stopped : GatewayState.Stale,
                inspection.GenerationMatches
                    ? "The signed-in-user gateway is not running."
                    : "The signed-in-user gateway record belongs to an older package generation.",
                inspection.Detail,
                new NativeGatewayLifecycleSource(record));
        }

        if (!inspection.GenerationMatches)
        {
            return new GatewayLifecycleStatus(
                GatewayState.Stale,
                "A signed-in-user gateway from an older package generation is still running.",
                "Stop the verified old process before restarting.",
                new NativeGatewayLifecycleSource(record));
        }

        if (inspection.Healthy)
        {
            return new GatewayLifecycleStatus(
                GatewayState.Running,
                $"The signed-in-user gateway is running on " +
                $"{DescribePorts(record, inspection.Ports)}.",
                Source: new NativeGatewayLifecycleSource(record));
        }

        return new GatewayLifecycleStatus(
            GatewayState.Unhealthy,
            "The signed-in-user gateway is running but is not healthy.",
            inspection.Detail,
            new NativeGatewayLifecycleSource(record));
    }

    private static bool PathsEqual(string? left, string right) =>
        left is not null &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string DescribePorts(
        NativeGatewayRecord record,
        IReadOnlyList<int> observedPorts)
    {
        if (observedPorts.Count > 0)
        {
            return observedPorts.Count == 1
                ? $"port {observedPorts[0]}"
                : $"ports {string.Join(", ", observedPorts)}";
        }

        return record.ConfiguredPort is int configured
            ? $"port {configured}"
            : $"port {GatewayLaunchConfiguration.UpstreamDefaultPort}";
    }

    private ISessionLockHandle AcquireLock() =>
        _lifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout)
            ?? throw new SessionBusyException(SessionCoordinator.DefaultLockTimeout);

    private sealed record NativeGatewayInspection(
        bool ProcessIdentityMatches,
        bool GenerationMatches,
        bool Healthy,
        IReadOnlyList<int> Ports,
        string? Detail,
        bool IsAmbiguous = false)
    {
        public static NativeGatewayInspection Absent(
            bool generationMatches,
            string? detail = null) =>
            new(false, generationMatches, false, [], detail);

        public static NativeGatewayInspection Ambiguous(string detail) =>
            new(false, false, false, [], detail, IsAmbiguous: true);
    }
}
