using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

internal sealed record GatewayIsolationCommandHandlers(
    Func<CancellationToken, Task<int>> Status,
    Func<CancellationToken, Task<int>> Enable,
    Func<CancellationToken, Task<int>> Disable);

internal sealed record GatewayIsolationTransitionTarget
{
    public required Func<CancellationToken, Task<int>> GetReadinessAsync { get; init; }
    public required Func<CancellationToken, Task<int>> PrepareUnderLockAsync { get; init; }
    public required Func<TextWriter, CancellationToken, Task<GatewayLifecycleStatus>>
        WriteGatewayStatusAsync
    { get; init; }
    public required Func<CancellationToken, Task<GatewayLifecycleStartResult>>
        StartGatewayUnderLockAsync
    { get; init; }
    public required Func<CancellationToken, Task<GatewayStopResult>>
        StopGatewayUnderLockAsync
    { get; init; }
    public required Func<CancellationToken, Task<TeardownResult>>
        TeardownUnderLockAsync
    { get; init; }
    public required Func<CancellationToken, Task<bool>> EnsureRecoveryUnderLockAsync
    { get; init; }
}

/// <summary>
/// Switches gateway identities while holding the installation lifecycle lock.
/// State is published only after the destination identity is established.
/// </summary>
internal sealed class GatewayIsolationTransitionManager
{
    public const int SetupRequiredExitCode = 2;

    private readonly GatewayIsolationStateStore _state;
    private readonly string _ownerSid;
    private readonly ISessionLock _lifecycleLock;
    private readonly GatewayIsolationTransitionTarget _isolated;
    private readonly GatewayIsolationTransitionTarget _native;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Func<bool> _canReadInteractiveInput;
    private readonly Action<GatewayIsolationMode> _writeSelection;

    public GatewayIsolationTransitionManager(
        GatewayIsolationStateStore state,
        string ownerSid,
        ISessionLock lifecycleLock,
        GatewayIsolationTransitionTarget isolated,
        GatewayIsolationTransitionTarget native,
        TextReader input,
        TextWriter output,
        Func<bool> canReadInteractiveInput,
        Action<GatewayIsolationMode>? writeSelection = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentNullException.ThrowIfNull(lifecycleLock);
        ArgumentNullException.ThrowIfNull(isolated);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(canReadInteractiveInput);

        _state = state;
        _ownerSid = ownerSid;
        _lifecycleLock = lifecycleLock;
        _isolated = isolated;
        _native = native;
        _input = input;
        _output = output;
        _canReadInteractiveInput = canReadInteractiveInput;
        _writeSelection = writeSelection ?? (mode =>
            _state.Write(new GatewayIsolationRecord
            {
                Mode = mode,
                OwnerSid = _ownerSid
            }));
    }

    public GatewayIsolationCommandHandlers CreateHandlers() => new(
        GetStatusAsync,
        EnableAsync,
        DisableAsync);

    internal async Task<int> GetStatusAsync(CancellationToken cancellationToken)
    {
        GatewayIsolationMode mode;
        try
        {
            mode = ReadMode();
        }
        catch (GatewayIsolationException exception)
        {
            await _output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        await WriteModeAsync(mode).ConfigureAwait(false);
        GatewayIsolationTransitionTarget target =
            mode == GatewayIsolationMode.Enabled ? _isolated : _native;
        int readiness = await target.GetReadinessAsync(cancellationToken)
            .ConfigureAwait(false);
        GatewayLifecycleStatus gateway = await target
            .WriteGatewayStatusAsync(_output, cancellationToken).ConfigureAwait(false);
        return readiness == 0 && IsAcceptableStatus(gateway.State) ? 0 : 1;
    }

    internal async Task<int> DisableAsync(CancellationToken cancellationToken)
    {
        using ISessionLockHandle? handle =
            _lifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout);
        if (handle is null)
        {
            await _output.WriteLineAsync(
                "Another OpenClaw process is changing installation state. Try again after it finishes.")
                .ConfigureAwait(false);
            return 1;
        }
        GatewayIsolationMode mode;
        try
        {
            mode = ReadMode();
        }
        catch (GatewayIsolationException exception)
        {
            await _output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (!await ConfirmDisableAsync(cancellationToken).ConfigureAwait(false))
        {
            await _output.WriteLineAsync(
                "Gateway-isolation disable was cancelled; no state or lifecycle resources were changed.")
                .ConfigureAwait(false);
            return 1;
        }

        if (mode == GatewayIsolationMode.Disabled)
        {
            return await RepairSelectedAsync(_native, GatewayIsolationMode.Disabled,
                cancellationToken).ConfigureAwait(false);
        }

        GatewayStopResult stopped;
        GatewayLifecycleStartResult started;
        try
        {
            stopped = await _isolated
                .StopGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            return await RollBackAsync(
                _isolated,
                $"The isolated gateway stop failed: {exception.Message}",
                RollbackToken(cancellationToken)).ConfigureAwait(false);
        }
        if (!stopped.Succeeded)
        {
            await WriteStopFailureAsync(stopped).ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (await _native.PrepareUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                return await RollBackDisableAsync(
                    "The signed-in-user identity could not be prepared.",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            started = await _native
                .StartGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (started.State != GatewayState.Running)
            {
                await _output.WriteLineAsync(started.Message).ConfigureAwait(false);
                return await RollBackDisableAsync(
                    "The signed-in-user gateway was not established.",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            if (!await _native.EnsureRecoveryUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return await RollBackDisableAsync(
                    "Gateway recovery could not be established for the signed-in-user identity.",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            WriteSelection(GatewayIsolationMode.Disabled);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            return await RollBackDisableAsync(
                $"The signed-in-user transition failed: {exception.Message}",
                RollbackToken(cancellationToken)).ConfigureAwait(false);
        }

        if (!await FinalizeSourceAsync(
                _isolated,
                "isolated",
                cancellationToken).ConfigureAwait(false))
        {
            await WriteModeAsync(GatewayIsolationMode.Disabled).ConfigureAwait(false);
            return 1;
        }

        await WriteModeAsync(GatewayIsolationMode.Disabled).ConfigureAwait(false);
        await _output.WriteLineAsync(started.Message).ConfigureAwait(false);
        return 0;
    }

    internal async Task<int> EnableAsync(CancellationToken cancellationToken)
    {
        using ISessionLockHandle? handle =
            _lifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout);
        if (handle is null)
        {
            await _output.WriteLineAsync(
                "Another OpenClaw process is changing installation state. Try again after it finishes.")
                .ConfigureAwait(false);
            return 1;
        }
        GatewayIsolationMode mode;
        GatewayLifecycleStartResult started;
        try
        {
            mode = ReadMode();
        }
        catch (GatewayIsolationException exception)
        {
            await _output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (mode == GatewayIsolationMode.Enabled)
        {
            return await RepairSelectedAsync(
                _isolated,
                GatewayIsolationMode.Enabled,
                cancellationToken).ConfigureAwait(false);
        }

        GatewayStopResult stopped;
        try
        {
            stopped = await _native
                .StopGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            return await RollBackAsync(
                _native,
                $"The signed-in-user gateway stop failed: {exception.Message}",
                RollbackToken(cancellationToken)).ConfigureAwait(false);
        }
        if (!stopped.Succeeded)
        {
            await WriteStopFailureAsync(stopped).ConfigureAwait(false);
            return 1;
        }

        try
        {
            TeardownResult staleIsolation = await _isolated
                .TeardownUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (!staleIsolation.Succeeded)
            {
                return await RollBackEnableAsync(
                    $"A fresh isolated identity could not be provisioned: {staleIsolation.Message}",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            if (await _isolated.PrepareUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                return await RollBackEnableAsync(
                    "The isolated identity could not be prepared.",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            started = await _isolated
                .StartGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (started.State is not GatewayState.Running and
                not GatewayState.Starting)
            {
                return await RollBackEnableAsync(
                    $"The isolated gateway was not established: {started.Message}",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            if (!await _isolated.EnsureRecoveryUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return await RollBackEnableAsync(
                    "Gateway recovery could not be established for the isolated identity.",
                    RollbackToken(cancellationToken)).ConfigureAwait(false);
            }

            WriteSelection(GatewayIsolationMode.Enabled);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            return await RollBackEnableAsync(
                $"The isolated transition failed: {exception.Message}",
                RollbackToken(cancellationToken)).ConfigureAwait(false);
        }

        if (!await FinalizeSourceAsync(
                _native,
                "signed-in-user",
                cancellationToken).ConfigureAwait(false))
        {
            await WriteModeAsync(GatewayIsolationMode.Enabled).ConfigureAwait(false);
            return 1;
        }

        await WriteModeAsync(GatewayIsolationMode.Enabled).ConfigureAwait(false);
        await _output.WriteLineAsync(started.Message).ConfigureAwait(false);
        return started.State == GatewayState.Running
            ? 0
            : SetupRequiredExitCode;
    }

    private async Task<int> RepairSelectedAsync(
        GatewayIsolationTransitionTarget target,
        GatewayIsolationMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await target.PrepareUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                return 1;
            }

            GatewayLifecycleStartResult started = await target
                .StartGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (!await target.EnsureRecoveryUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                await _output.WriteLineAsync(
                    "Gateway recovery could not be repaired for the selected identity.")
                    .ConfigureAwait(false);
                return 1;
            }

            await WriteModeAsync(mode).ConfigureAwait(false);
            await _output.WriteLineAsync(started.Message).ConfigureAwait(false);
            return started.State == GatewayState.Running
                ? 0
                : mode == GatewayIsolationMode.Enabled
                    ? SetupRequiredExitCode
                    : 1;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            await _output.WriteLineAsync(
                $"The selected identity could not be repaired: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RollBackDisableAsync(
        string failure,
        CancellationToken cancellationToken)
    {
        if (!await BestEffortTeardownAsync(_native, cancellationToken)
                .ConfigureAwait(false))
        {
            await _output.WriteLineAsync(failure).ConfigureAwait(false);
            await _output.WriteLineAsync(
                "ROLLBACK INCOMPLETE: the partial signed-in-user destination could not be safely removed, so the isolated gateway was not restarted.")
                .ConfigureAwait(false);
            return 1;
        }

        return await RollBackAsync(_isolated, failure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> RollBackEnableAsync(
        string failure,
        CancellationToken cancellationToken)
    {
        if (!await BestEffortTeardownAsync(_isolated, cancellationToken)
                .ConfigureAwait(false))
        {
            await _output.WriteLineAsync(failure).ConfigureAwait(false);
            await _output.WriteLineAsync(
                "ROLLBACK INCOMPLETE: the partial isolated destination could not be safely removed, so the signed-in-user gateway was not restarted.")
                .ConfigureAwait(false);
            return 1;
        }

        return await RollBackAsync(_native, failure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> RollBackAsync(
        GatewayIsolationTransitionTarget previous,
        string failure,
        CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync(failure).ConfigureAwait(false);
        try
        {
            if (await previous.PrepareUnderLockAsync(cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                throw new SessionException("The previous identity could not be prepared.");
            }

            GatewayLifecycleStartResult restarted = await previous
                .StartGatewayUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (restarted.State != GatewayState.Running)
            {
                throw new SessionException(restarted.Message);
            }

            await _output.WriteLineAsync(
                "The previous gateway identity was restored.").ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            await _output.WriteLineAsync(
                $"ROLLBACK INCOMPLETE: {exception.Message}").ConfigureAwait(false);
        }

        return 1;
    }

    private async Task<bool> BestEffortTeardownAsync(
        GatewayIsolationTransitionTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            TeardownResult cleanup = await target
                .TeardownUnderLockAsync(RollbackToken(cancellationToken))
                .ConfigureAwait(false);
            if (!cleanup.Succeeded)
            {
                await _output.WriteLineAsync(
                    $"ROLLBACK INCOMPLETE: partial destination cleanup failed: {cleanup.Message} {cleanup.Detail}")
                    .ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            await _output.WriteLineAsync(
                $"ROLLBACK INCOMPLETE: partial destination cleanup failed: {exception.Message}")
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> FinalizeSourceAsync(
        GatewayIsolationTransitionTarget source,
        string identity,
        CancellationToken cancellationToken)
    {
        try
        {
            TeardownResult cleanup = await source
                .TeardownUnderLockAsync(cancellationToken).ConfigureAwait(false);
            if (cleanup.Succeeded)
            {
                return true;
            }

            await _output.WriteLineAsync(
                $"TRANSITION COMMITTED; SOURCE CLEANUP INCOMPLETE: the {identity} identity could not be removed: {cleanup.Message} {cleanup.Detail}")
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            await _output.WriteLineAsync(
                $"TRANSITION COMMITTED; SOURCE CLEANUP INCOMPLETE: the {identity} identity could not be removed: {exception.Message}")
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> ConfirmDisableAsync(CancellationToken cancellationToken)
    {
        bool canRead;
        try
        {
            canRead = _canReadInteractiveInput();
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or
            InvalidOperationException)
        {
            return false;
        }

        if (!canRead)
        {
            return false;
        }

        await _output.WriteAsync(
            "Disable gateway isolation and remove the owned isolated session? [y/N] ")
            .ConfigureAwait(false);
        string? response;
        try
        {
            response = await _input.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or
            InvalidOperationException)
        {
            return false;
        }

        return string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private GatewayIsolationMode ReadMode()
    {
        GatewayIsolationStateResult result = _state.Read(_ownerSid);
        if (result.Record is { } record)
        {
            return record.Mode;
        }

        if (result.Fault == GatewayIsolationStateFault.Missing)
        {
            return GatewayIsolationMode.Enabled;
        }

        throw new GatewayIsolationException(
            $"The gateway-isolation state could not be used: {result.Detail}");
    }

    private void WriteSelection(GatewayIsolationMode mode) => _writeSelection(mode);

    private Task WriteModeAsync(GatewayIsolationMode mode) =>
        _output.WriteLineAsync(
            $"Gateway isolation: {GatewayIsolationPolicy.EnvironmentValue(mode)}.");

    private async Task WriteStopFailureAsync(GatewayStopResult stopped)
    {
        await _output.WriteLineAsync(stopped.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stopped.Detail))
        {
            await _output.WriteLineAsync(stopped.Detail).ConfigureAwait(false);
        }
    }

    private static bool IsAcceptableStatus(GatewayState state) =>
        state is GatewayState.Running or GatewayState.NotStarted;

    private static bool IsOperational(Exception exception) =>
        exception is SessionException or GatewayIsolationException or
            IOException or UnauthorizedAccessException or InvalidOperationException or
            OperationCanceledException;

    private static CancellationToken RollbackToken(CancellationToken token) =>
        token.IsCancellationRequested ? CancellationToken.None : token;
}
