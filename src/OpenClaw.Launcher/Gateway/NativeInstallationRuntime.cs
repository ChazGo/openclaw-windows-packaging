using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Owns package-managed state used when gateway isolation is disabled.</summary>
internal sealed class NativeInstallationRuntime
{
    private readonly HostOptions _options;
    private readonly NativeGatewayController _gateway;
    private readonly ISessionLock _lifecycleLock;
    private readonly IGatewayPersistence _recovery;
    private readonly IDiagnosticsCollector _diagnostics;
    private readonly IInstallationLifecycle _installationLifecycle;
    private readonly Action<NodeRuntime> _runtimePrepared;
    private readonly Action<string> _deleteRuntimeDirectory;
    private readonly Action<string> _log;

    public NativeInstallationRuntime(
        HostOptions options,
        HostPaths paths,
        NativeGatewayController gateway,
        ISessionLock lifecycleLock,
        IGatewayPersistence recovery,
        IDiagnosticsCollector diagnostics,
        IInstallationLifecycle installationLifecycle,
        Action<NodeRuntime> runtimePrepared,
        Action<string> log,
        Action<string>? deleteRuntimeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(lifecycleLock);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(installationLifecycle);
        ArgumentNullException.ThrowIfNull(runtimePrepared);
        ArgumentNullException.ThrowIfNull(log);

        _options = options;
        _gateway = gateway;
        _lifecycleLock = lifecycleLock;
        _recovery = recovery;
        _diagnostics = diagnostics;
        _installationLifecycle = installationLifecycle;
        _runtimePrepared = runtimePrepared;
        _deleteRuntimeDirectory = deleteRuntimeDirectory ?? DeleteRuntimeDirectory;
        _log = log;
    }

    public NativeGatewayController Gateway => _gateway;

    public async Task<int> SetupAsync(
        SetupOptions setupOptions,
        TextWriter output,
        bool lockAlreadyHeld,
        Action? completeIsolationSelection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        string applicationDirectory = RequireApplicationDirectory();
        _ = RequireNodeArchive();
        using ISessionLockHandle? handle = lockAlreadyHeld ? null : AcquireLock();

        if (setupOptions.Fresh)
        {
            TeardownResult reset = await TeardownUnderLockAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!reset.Succeeded)
            {
                await output.WriteLineAsync(reset.Message).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(reset.Detail))
                {
                    await output.WriteLineAsync(reset.Detail).ConfigureAwait(false);
                }
                return 1;
            }
        }

        NodeRuntime runtime = _installationLifecycle.PrepareHostRuntime(_options, _log);
        _runtimePrepared(runtime);
        GatewayPersistenceInstallResult recovery = await _recovery
            .InstallAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Node.js {runtime.Version} is ready for signed-in-user execution.")
            .ConfigureAwait(false);
        await output.WriteLineAsync(recovery.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(recovery.Detail))
        {
            await output.WriteLineAsync(recovery.Detail).ConfigureAwait(false);
        }

        if (recovery.State != GatewayPersistenceState.Ready)
        {
            return 1;
        }

        completeIsolationSelection?.Invoke();
        await output.WriteLineAsync(
            $"OpenClaw is ready to run directly from {applicationDirectory}.")
            .ConfigureAwait(false);
        return 0;
    }

    public async Task<int> GetStatusAsync(
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        bool healthy = true;
        try
        {
            NodeRuntime runtime = NodeRuntimeResolver.Resolve(RequireNodeArchive());
            await output.WriteLineAsync(
                $"Signed-in-user Node.js {runtime.Version} is ready.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidDataException or
            InvalidOperationException)
        {
            healthy = false;
            await output.WriteLineAsync(
                $"Signed-in-user runtime is not ready: {exception.Message}")
                .ConfigureAwait(false);
        }

        GatewayLifecycleStatus gateway = await _gateway
            .GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(gateway.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(gateway.Detail))
        {
            await output.WriteLineAsync(gateway.Detail).ConfigureAwait(false);
        }
        healthy &= gateway.State is GatewayState.Running or GatewayState.NotStarted;

        GatewayPersistenceStatus recovery = await _recovery
            .GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(recovery.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(recovery.Detail))
        {
            await output.WriteLineAsync(recovery.Detail).ConfigureAwait(false);
        }
        healthy &= recovery.State is GatewayPersistenceState.Ready or
            GatewayPersistenceState.NotInstalled;
        return healthy ? 0 : 1;
    }

    public async Task<int> CollectLogsAsync(
        string? requestedPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        DiagnosticsBundleResult result = await _diagnostics
            .CollectLogsAsync(
                requestedPath,
                includeSession: false,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteDiagnosticsResultAsync(result, output).ConfigureAwait(false);
        return 0;
    }

    public async Task<int> TeardownAsync(
        TextWriter output,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken)
    {
        using ISessionLockHandle? handle = lockAlreadyHeld ? null : AcquireLock();
        TeardownResult result = await TeardownUnderLockAsync(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(result.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            await output.WriteLineAsync(result.Detail).ConfigureAwait(false);
        }
        return result.Succeeded ? 0 : 1;
    }

    private async Task<TeardownResult> TeardownUnderLockAsync(
        CancellationToken cancellationToken)
    {
        GatewayPersistenceRemovalResult recovery = await _recovery
            .UninstallAsync(cancellationToken).ConfigureAwait(false);
        if (!recovery.Succeeded)
        {
            return new TeardownResult(false, recovery.Message, recovery.Detail);
        }

        GatewayStopResult gateway = await _gateway
            .StopUnderLockAsync(cancellationToken).ConfigureAwait(false);
        if (!gateway.Succeeded)
        {
            return new TeardownResult(false, gateway.Message, gateway.Detail);
        }

        try
        {
            string installDirectory =
                NodeRuntimeInstaller.GetInstallDirectory(RequireNodeArchive());
            _deleteRuntimeDirectory(
                Directory.GetParent(installDirectory)?.FullName
                ?? throw new InvalidOperationException(
                    "The signed-in-user runtime directory has no parent."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            return new TeardownResult(
                false,
                "The signed-in-user runtime could not be removed.",
                exception.Message);
        }

        return new TeardownResult(
            true,
            "Signed-in-user gateway recovery and runtime state were removed.");
    }

    private string RequireApplicationDirectory()
    {
        string directory = _options.PackagedApplicationDirectory
            ?? throw new FileNotFoundException(
                "The packaged OpenClaw application directory was not found.");
        string entryPoint = Path.Combine(directory, "openclaw.mjs");
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                entryPoint);
        }
        return directory;
    }

    private string RequireNodeArchive() =>
        _options.PackagedNodeArchivePath
        ?? throw new FileNotFoundException(
            "The packaged Node.js runtime archive was not found.");

    private ISessionLockHandle AcquireLock() =>
        _lifecycleLock.TryAcquire(SessionCoordinator.DefaultLockTimeout)
        ?? throw new SessionBusyException(SessionCoordinator.DefaultLockTimeout);

    private static void DeleteRuntimeDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    internal static async Task WriteDiagnosticsResultAsync(
        DiagnosticsBundleResult result,
        TextWriter output)
    {
        if (result.BundlePath is not null)
        {
            await output.WriteLineAsync(
                $"Diagnostics bundle created: {result.BundlePath}").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(
                "No diagnostic files were available to bundle.").ConfigureAwait(false);
        }

        foreach (string warning in result.Notes)
        {
            await output.WriteLineAsync($"Warning: {warning}").ConfigureAwait(false);
        }
    }
}
