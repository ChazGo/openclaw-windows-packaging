using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using OpenClaw.Launcher.Gateway;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher;

internal static class Program
{
    public static async Task<int> Main(string[] args) =>
        await RunAsync(args, HostStartup.CreateProduction()).ConfigureAwait(false);

    // The whole startup path lives here rather than in Main so that tests can
    // drive it with fixture-owned diagnostics and writers. Main is only the
    // production adapter that supplies the real collaborators.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "This is the process last-chance handler. Every narrower catch in " +
            "this assembly uses an exception filter; this one deliberately does " +
            "not, because narrowing it would replace the diagnostic log entry, " +
            "the user-facing error message, and the deterministic exit code 1 " +
            "with an unhandled-exception crash.")]
    internal static async Task<int> RunAsync(string[] args, HostStartup startup)
    {
        string commandName = startup.Entrypoint == HostEntrypoint.Control
            ? HostEntrypointResolver.ControlCommandName
            : HostEntrypointResolver.AgentCommandName;
        HostDiagnosticLog? diagnostics = null;
        bool diagnosticWarningWritten = false;
        bool consoleWarningWritten = false;

        void WriteConsoleError(string message)
        {
            try
            {
                startup.Error.WriteLine(message);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                if (!consoleWarningWritten)
                {
                    consoleWarningWritten = true;
                    WriteDiagnostic(
                        $"Console error output failed: {exception.GetType().Name}.");
                }
            }
        }

        try
        {
            diagnostics = startup.CreateDiagnostics();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            diagnosticWarningWritten = true;
            WriteConsoleError(
                $"{commandName}: Unable to create diagnostics: {exception.Message}");
        }

        void WriteDiagnostic(string message)
        {
            if (diagnostics is null)
            {
                return;
            }

            try
            {
                diagnostics.Write(message);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ObjectDisposedException)
            {
                if (!diagnosticWarningWritten)
                {
                    diagnosticWarningWritten = true;
                    WriteConsoleError(
                        $"{commandName}: Unable to write diagnostics: {exception.Message}");
                }
            }
        }

        static string GetDiagnosticFailure(Exception exception) =>
            exception switch
            {
                InvalidDataException or
                TimeoutException or
                PlatformNotSupportedException or
                FileNotFoundException =>
                    $"{exception.GetType().Name}: {exception.Message}",
                _ => exception.GetType().Name
            };

        try
        {
            WriteDiagnostic($"Host started through the {commandName} entrypoint.");
            HostOptions options = HostOptions.Parse(args, startup.BaseDirectory);
            return startup.Entrypoint == HostEntrypoint.Control
                ? await RunControlAsync(
                    options,
                    args,
                    WriteDiagnostic,
                    startup.Output,
                    startup.Error,
                    startup.ResolveNode,
                    startup.InstallationLifecycle,
                    readEnvironmentVariable: startup.ReadEnvironmentVariable)
                    .ConfigureAwait(false)
                : await RunAgentAsync(
                    options,
                    WriteDiagnostic,
                    startup.ResolveNode ?? (_ => Task.FromResult(
                        (startup.InstallNodeRuntime ?? NodeRuntimeInstaller.EnsureInstalled)(
                            GetPackagedNodeArchivePath(options),
                            WriteDiagnostic))),
                    startup.LaunchOpenClaw ?? GatewayLauncher.RunAsync,
                    startup.InstallationLifecycle is null
                        ? null
                        : startup.InstallationLifecycle.CreateRuntime,
                    readEnvironmentVariable: startup.ReadEnvironmentVariable)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteDiagnostic($"Unhandled failure: {GetDiagnosticFailure(exception)}");
            WriteConsoleError($"{commandName}: {exception.Message}");
            if (diagnostics is not null)
            {
                WriteConsoleError(
                    $"{commandName}: See diagnostics: {diagnostics.Path}");
            }
            return 1;
        }
        finally
        {
            WriteDiagnostic("Host exiting.");
            diagnostics?.Dispose();
        }
    }

    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null) =>
        await RunAgentAsync(
            options,
            log,
            resolveNode ?? (_ => Task.FromResult(NodeRuntimeInstaller.EnsureInstalled(
                GetPackagedNodeArchivePath(options),
                log))),
            GatewayLauncher.RunAsync).ConfigureAwait(false);

    // launchOpenClaw is a test seam: tests substitute a fake in place of
    // GatewayLauncher.RunAsync so they can assert launch behavior without
    // starting a real Node child process or Windows job object.
    internal static async Task<int> RunAgentAsync(
        HostOptions options,
        Action<string> log,
        Func<CancellationToken, Task<NodeRuntime>> resolveNode,
        LaunchOpenClawAsync launchOpenClaw,
        Func<Action<string>, Session.SessionRuntime>? createSessionRuntime = null,
        Func<CancellationToken, Task<Mxc.MxcReadinessReport>>? probeReadiness = null,
        Func<string?>? getPackageFamilyName = null,
        Func<string, string?>? readEnvironmentVariable = null,
        Func<string?, GatewayIsolationSelection>? resolveGatewayIsolation = null,
        Func<bool>? isInteractive = null)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Using the OpenClaw application directly from the package.");

        Func<string, string?> environment =
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        string? packageFamilyName =
            (getPackageFamilyName ?? (() => HostPaths.Create().PackageFamilyName))();
        GatewayIsolationSelection isolation = (resolveGatewayIsolation ??
            (familyName =>
            {
                HostPaths paths = HostPaths.Create();
                return GatewayIsolationPolicy.Resolve(
                    new GatewayIsolationStateStore(paths.GatewayIsolationStatePath),
                    familyName is not null,
                    GatewayIsolationPolicy.GetCurrentUserSid(),
                    environment);
            }))(packageFamilyName);
        Session.SessionRoutingDecision routing;
        if (isolation.Mode == GatewayIsolationMode.Disabled)
        {
            routing = new Session.SessionRoutingDecision(
                Session.SessionRouting.Direct,
                isolation.Reason);
        }
        else
        {
            Mxc.MxcReadinessReport readiness = await (probeReadiness ??
                Mxc.MxcReadiness.ProbeAsync)(CancellationToken.None).ConfigureAwait(false);
            routing = Session.SessionRoutingPolicy.Decide(
                isolation,
                packageFamilyName,
                readiness);
        }

        log(routing.Reason);
        if (routing.Routing == Session.SessionRouting.Session)
        {
            Session.SessionRuntime runtime = (createSessionRuntime ??
                Session.SessionRuntime.Create)(log);
            Session.SessionRecord record =
                await runtime.StartForExecutionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            string agentNodePath = runtime.RequireAgentNodePath(
                GetPackagedNodeArchivePath(options));
            return await runtime.Executor.ExecuteAsync(
                record,
                new Session.SessionExecutionRequest(
                    runtime.RequireStagedHelper(record),
                    agentNodePath,
                    applicationDirectory,
                    options.OpenClawArguments,
                    Environment.CurrentDirectory)
                {
                    AdditionalEnvironment = OpenClawRuntimeEnvironment.Build(
                        (isInteractive ?? (() => WindowsHostConsole.Instance.IsInteractive))(),
                        environment,
                        isolation.Mode)
                },
                CancellationToken.None).ConfigureAwait(false);
        }

        NodeRuntime nodeRuntime = await resolveNode(CancellationToken.None)
            .ConfigureAwait(false);
        log(
            $"Using Node.js {nodeRuntime.Version} from " +
            $"{nodeRuntime.ExecutablePath}.");
        return await launchOpenClaw(
            nodeRuntime.ExecutablePath,
            applicationDirectory,
            options.OpenClawArguments,
            CancellationToken.None,
            log).ConfigureAwait(false);
    }

    // output and error are required parameters (not Console defaults) so tests
    // can capture clawctl output without mutating global console state,
    // which would be unsafe across parallel test runs.
    internal static async Task<int> RunControlAsync(
        HostOptions options,
        IReadOnlyList<string> args,
        Action<string> log,
        TextWriter output,
        TextWriter error,
        Func<CancellationToken, Task<NodeRuntime>>? resolveNode = null,
        Session.IInstallationLifecycle? installationLifecycle = null,
        Func<string, string?>? readEnvironmentVariable = null,
        Func<SetupOptions, GatewayIsolationSetupPlan>? resolveGatewayIsolationSetup = null,
        Action<GatewayIsolationMode>? persistGatewayIsolation = null,
        Func<GatewayIsolationSelection>? resolveGatewayIsolation = null,
        Func<GatewayCommandTarget>? createIsolatedTarget = null,
        Func<GatewayCommandTarget>? createNativeTarget = null,
        HostPaths? hostPaths = null,
        Func<SetupOptions, Action?, CancellationToken, Task<int>>? runNativeSetup = null)
    {
        _ = resolveNode;
        Session.IInstallationLifecycle lifecycle =
            installationLifecycle ?? Session.InstallationLifecycle.Production;
        HostPaths paths = hostPaths ?? HostPaths.Create();
        Func<string, string?> environment =
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        GatewayIsolationSelection ResolveIsolation() =>
            resolveGatewayIsolation is not null
                ? resolveGatewayIsolation()
                : (installationLifecycle is not null || resolveNode is not null) &&
                    paths.PackageFamilyName is null
                    ? new GatewayIsolationSelection(
                        GatewayIsolationMode.Enabled,
                        "Gateway isolation is enabled for the injected installation lifecycle.")
                    : GatewayIsolationPolicy.Resolve(
                        new GatewayIsolationStateStore(paths.GatewayIsolationStatePath),
                        paths.PackageFamilyName is not null,
                        GatewayIsolationPolicy.GetCurrentUserSid(),
                        environment);
        Session.SessionRuntime? sessionRuntime = null;
        Session.SessionRuntime GetSessionRuntime() =>
            sessionRuntime ??= lifecycle.CreateRuntime(log);

        GatewayCommandTarget CreateIsolatedTarget()
        {
            if (createIsolatedTarget is not null)
            {
                return createIsolatedTarget();
            }

            return new GatewayCommandTarget
            {
                Setup = (setupOptions, cancellationToken) => RunSetupAsync(
                    setupOptions,
                    options,
                    GetSessionRuntime,
                    lifecycle,
                    paths,
                    log,
                    output,
                    readEnvironmentVariable ?? Environment.GetEnvironmentVariable,
                    resolveGatewayIsolationSetup,
                    persistGatewayIsolation,
                    runNativeSetup,
                    cancellationToken),
                Status = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Session.SessionStatus status = await runtime
                        .Coordinator.ProbeRecordedStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(DescribeSessionStatus(status))
                        .ConfigureAwait(false);
                    Gateway.GatewayRuntime gatewayRuntime = Gateway.GatewayRuntime.Create(
                        options,
                        paths,
                        runtime,
                        log);
                    Gateway.GatewayStatusReport gateway = await gatewayRuntime.Controller
                        .GetStatusAsync(runtime.HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Gateway.GatewayControlOutput.WriteStatusAsync(
                        output,
                        gateway,
                        gatewayRuntime.Paths,
                        cancellationToken).ConfigureAwait(false);

                    return status.Availability is Session.SessionAvailability.Stale or
                        Session.SessionAvailability.BackendUnavailable or
                        Session.SessionAvailability.BackendError or
                        Session.SessionAvailability.Unusable ||
                        gateway.State is not Gateway.GatewayState.Running and
                            not Gateway.GatewayState.NotStarted
                        ? 1
                        : 0;
                },
                CollectLogs = async (requestedPath, cancellationToken) =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.DiagnosticsBundleResult result =
                        await Gateway.GatewayRuntime.Create(
                            options,
                            paths,
                            runtime,
                            log)
                        .CollectLogsAsync(
                            requestedPath,
                            includeSession: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await Gateway.NativeInstallationRuntime.WriteDiagnosticsResultAsync(
                        result,
                        output).ConfigureAwait(false);
                    return 0;
                },
                Teardown = async (force, cancellationToken) =>
                {
                    if (!force)
                    {
                        await error.WriteLineAsync(
                            "Teardown removes the isolated session and its data. Re-run with --force to continue.")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Session.TeardownResult result = await lifecycle.TeardownAsync(
                        options,
                        runtime,
                        log,
                        lockAlreadyHeld: false,
                        force: true,
                        cancellationToken)
                        .ConfigureAwait(false);
                    await output.WriteLineAsync(result.Message).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.Detail))
                    {
                        await output.WriteLineAsync(result.Detail).ConfigureAwait(false);
                    }

                    return result.Succeeded ? 0 : 1;
                },
                PowerShell = cancellationToken => RunPowerShellAsync(
                    options,
                    GetSessionRuntime(),
                    output,
                    cancellationToken),
                Gateway = new Gateway.LazyGatewayLifecycle(() =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    return new Gateway.SessionGatewayLifecycle(
                        Gateway.GatewayRuntime.Create(
                            options,
                            paths,
                            runtime,
                            log).Controller,
                        runtime.HelperPath);
                }),
                GatewayStatus = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.GatewayRuntime gatewayRuntime = Gateway.GatewayRuntime.Create(
                        options,
                        paths,
                        runtime,
                        log);
                    Gateway.GatewayStatusReport result = await gatewayRuntime.Controller
                        .GetStatusAsync(runtime.HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Gateway.GatewayControlOutput.WriteStatusAsync(
                        output,
                        result,
                        gatewayRuntime.Paths,
                        cancellationToken).ConfigureAwait(false);
                    return result.State is Gateway.GatewayState.Running or
                        Gateway.GatewayState.NotStarted
                        ? 0
                        : 1;
                },
                GatewayStop = async cancellationToken =>
                {
                    Session.SessionRuntime runtime = GetSessionRuntime();
                    Gateway.GatewayStopResult result = await Gateway.GatewayRuntime
                        .Create(options, paths, runtime, log)
                        .Controller
                        .StopAsync(runtime.HelperPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Gateway.GatewayControlOutput.WriteStopAsync(output, result)
                        .ConfigureAwait(false);
                    return result.Succeeded ? 0 : 1;
                }
            };
        }

        GatewayCommandTarget CreateNativeTarget()
        {
            if (createNativeTarget is not null)
            {
                return createNativeTarget();
            }

            Session.ISessionLock lifecycleLock = GetSessionRuntime().LifecycleLock;
            NodeRuntime? preparedRuntime = null;
            Gateway.NativeGatewayController gateway =
                Gateway.GatewayRuntime.CreateNativeLifecycle(
                    options,
                    paths,
                    lifecycleLock,
                    () => preparedRuntime ??
                        NodeRuntimeResolver.Resolve(GetPackagedNodeArchivePath(options)),
                    log);
            var native = new Gateway.NativeInstallationRuntime(
                options,
                paths,
                gateway,
                lifecycleLock,
                Gateway.GatewayRuntime.CreateRecoveryManager(paths, log),
                Gateway.GatewayRuntime.CreateDiagnostics(paths),
                lifecycle,
                runtime => preparedRuntime = runtime,
                log);
            return new GatewayCommandTarget
            {
                Setup = (setupOptions, cancellationToken) =>
                    native.SetupAsync(
                        setupOptions,
                        output,
                        lockAlreadyHeld: false,
                        completeIsolationSelection: null,
                        cancellationToken),
                Status = cancellationToken =>
                    native.GetStatusAsync(output, cancellationToken),
                CollectLogs = (requestedPath, cancellationToken) =>
                    native.CollectLogsAsync(requestedPath, output, cancellationToken),
                Teardown = async (force, cancellationToken) =>
                {
                    if (!force)
                    {
                        await error.WriteLineAsync(
                            "Teardown removes signed-in-user gateway state and runtime data. Re-run with --force to continue.")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    return await native.TeardownAsync(
                        output,
                        lockAlreadyHeld: false,
                        cancellationToken).ConfigureAwait(false);
                },
                PowerShell = _ => throw new Session.SessionException(
                    "Gateway isolation is disabled. Open ordinary PowerShell to run commands as the signed-in user."),
                Gateway = native.Gateway
            };
        }

        var router = new Gateway.ModeAwareCommandRouter(
            ResolveIsolation,
            (setupOptions, cancellationToken) => RunSetupAsync(
                setupOptions,
                options,
                GetSessionRuntime,
                lifecycle,
                paths,
                log,
                output,
                environment,
                resolveGatewayIsolationSetup,
                persistGatewayIsolation,
                runNativeSetup,
                cancellationToken),
            CreateIsolatedTarget,
            CreateNativeTarget,
            output);
        RootCommand command = ClawCtlCommandLine.Create(router.CreateHandlers());

        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,

            // Operational failures stay the host's responsibility. The default
            // handler would print its own message and return its own exit code,
            // losing the diagnostic log entry and the log path Main reports.
            EnableDefaultExceptionHandler = false,

            // Node lifetime is owned by the job object in GatewayLauncher. The
            // library's termination timeout would add a second, conflicting
            // forced-exit policy and process-wide signal handlers.
            ProcessTerminationTimeout = null
        };

        return await command
            .Parse(args, ClawCtlCommandLine.CreateParserConfiguration())
            .InvokeAsync(configuration)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunSetupAsync(
        SetupOptions setupOptions,
        HostOptions options,
        Func<Session.SessionRuntime> getSessionRuntime,
        Session.IInstallationLifecycle lifecycle,
        HostPaths paths,
        Action<string> log,
        TextWriter output,
        Func<string, string?> readEnvironmentVariable,
        Func<SetupOptions, GatewayIsolationSetupPlan>? resolveGatewayIsolationSetup,
        Action<GatewayIsolationMode>? persistGatewayIsolation,
        Func<SetupOptions, Action?, CancellationToken, Task<int>>? runNativeSetup,
        CancellationToken cancellationToken)
    {
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        log("Confirmed the packaged OpenClaw application is present.");
        ClawCtlConsole.WriteReadinessSummary(output, applicationDirectory);

        Session.SessionRoutingDecision routing = await lifecycle.CheckSessionSupportAsync(
            cancellationToken).ConfigureAwait(false);
        if (routing.Routing != Session.SessionRouting.Session)
        {
            await output.WriteLineAsync(
                $"OpenClaw setup requires isolated-session support. {routing.Reason}")
                .ConfigureAwait(false);
            return 1;
        }

        Session.SessionMode environmentMode =
            Session.SessionRoutingPolicy.ReadMode(readEnvironmentVariable);
        if (setupOptions.Fresh &&
            (setupOptions.NoIsolation || environmentMode == Session.SessionMode.Disabled))
        {
            await output.WriteLineAsync(
                "OpenClaw setup --fresh requires isolated-session provisioning. " +
                "Remove --fresh or enable isolation before retrying.")
                .ConfigureAwait(false);
            return 1;
        }
        string ownerSid = GatewayIsolationPolicy.GetCurrentUserSid();
        GatewayIsolationStateStore? isolationStore = paths.PackageFamilyName is null
            ? null
            : new GatewayIsolationStateStore(paths.GatewayIsolationStatePath);
        GatewayIsolationSetupPlan isolationPlan;
        try
        {
            isolationPlan =
                (resolveGatewayIsolationSetup ?? (requested =>
                    GatewayIsolationPolicy.ResolveSetup(
                        isolationStore,
                        paths.PackageFamilyName is not null,
                        ownerSid,
                        requested.NoIsolation,
                        readEnvironmentVariable)))(setupOptions);
        }
        catch (Exception exception) when (
            exception is GatewayIsolationException or Session.SessionException)
        {
            log($"Gateway-isolation setup intent could not be resolved: {exception.Message}");
            await output.WriteLineAsync(
                $"OpenClaw setup could not continue: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        Action<GatewayIsolationMode> persistIsolation =
            persistGatewayIsolation ?? (mode =>
                isolationStore?.Write(new GatewayIsolationRecord
                {
                    Mode = mode,
                    OwnerSid = ownerSid,
                }));
        log(isolationPlan.Reason);

        try
        {
            Session.SessionRuntime runtime = getSessionRuntime();
            if (isolationPlan.Mode == GatewayIsolationMode.Enabled && setupOptions.Fresh)
            {
                // Resolve every required package input before removing state.
                _ = lifecycle.ValidatePackageRuntime(options, runtime);
            }

            using Session.ISessionLockHandle handle =
                lifecycle.AcquireLifecycleLock(runtime);
            Action? completeIsolationSelection = isolationPlan.PersistAfterSuccess
                ? () => persistIsolation(isolationPlan.Mode)
                : null;
            if (isolationPlan.Mode == GatewayIsolationMode.Disabled)
            {
                if (runNativeSetup is not null)
                {
                    return await runNativeSetup(
                        setupOptions,
                        completeIsolationSelection,
                        cancellationToken).ConfigureAwait(false);
                }

                NodeRuntime? preparedRuntime = null;
                Gateway.NativeGatewayController gateway =
                    Gateway.GatewayRuntime.CreateNativeLifecycle(
                        options,
                        paths,
                        runtime.LifecycleLock,
                        () => preparedRuntime
                            ?? throw new Session.SessionException(
                                "The signed-in-user Node.js runtime has not been prepared."),
                        log);
                var native = new Gateway.NativeInstallationRuntime(
                    options,
                    paths,
                    gateway,
                    runtime.LifecycleLock,
                    Gateway.GatewayRuntime.CreateRecoveryManager(paths, log),
                    Gateway.GatewayRuntime.CreateDiagnostics(paths),
                    lifecycle,
                    value => preparedRuntime = value,
                    log);
                return await native.SetupAsync(
                    setupOptions,
                    output,
                    lockAlreadyHeld: true,
                    completeIsolationSelection,
                    cancellationToken).ConfigureAwait(false);
            }

            if (setupOptions.Fresh)
            {
                string reportPath = WriteFreshDiagnosticReport(runtime, log);
                await output.WriteLineAsync($"Pre-reset diagnostic report: {reportPath}")
                    .ConfigureAwait(false);
                Session.TeardownResult teardownResult;
                try
                {
                    teardownResult = await lifecycle.TeardownAsync(
                        options,
                        runtime,
                        log,
                        lockAlreadyHeld: true,
                        force: setupOptions.Force,
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    setupOptions.Force &&
                    exception is Session.SessionException or Mxc.MxcException)
                {
                    teardownResult = new Session.TeardownResult(
                        false,
                        "Teardown failed before external cleanup could be confirmed.",
                        exception.Message);
                }
                if (!teardownResult.Succeeded)
                {
                    if (!setupOptions.Force)
                    {
                        await output.WriteLineAsync(
                            $"Warning: Fresh setup stopped because teardown is incomplete: {teardownResult.Message}")
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(teardownResult.Detail))
                        {
                            await output.WriteLineAsync(teardownResult.Detail).ConfigureAwait(false);
                        }

                        return 1;
                    }

                    await output.WriteLineAsync(
                        $"WARNING: Forced fresh setup will continue without confirming external cleanup: {teardownResult.Message}")
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(teardownResult.Detail))
                    {
                        await output.WriteLineAsync(teardownResult.Detail).ConfigureAwait(false);
                    }
                }

                Session.IInstallationStateCleaner cleaner = lifecycle.CreateStateCleaner(runtime);
                cleaner.Clear();
                log("Fresh setup cleared package-owned local state.");
                if (!teardownResult.Succeeded)
                {
                    const string residualWarning =
                        "WARNING: Forced fresh setup did not prove a pristine machine because owned external cleanup remains unresolved. " +
                        "Review the pre-reset report for residual sandbox or gateway identifiers. " +
                        "A later setup reset cannot remove resources whose ownership record was cleared; " +
                        "remove them through the backend's administrative cleanup path before treating this machine as pristine.";
                    log(residualWarning);
                    await output.WriteLineAsync(residualWarning).ConfigureAwait(false);
                }

                return await RunSetupCoreAsync(
                    runtime,
                    options,
                    lifecycle,
                    output,
                    log,
                    lockAlreadyHeld: true,
                    cancellationToken,
                    completeIsolationSelection)
                    .ConfigureAwait(false);
            }

            return await RunSetupCoreAsync(
                runtime,
                options,
                lifecycle,
                output,
                log,
                lockAlreadyHeld: true,
                cancellationToken,
                completeIsolationSelection)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is Session.SessionException or GatewayIsolationException)
        {
            log($"OpenClaw setup is unavailable: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (IOException exception)
        {
            log($"Fresh setup local cleanup failed: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            log($"Fresh setup local cleanup was denied: {exception.Message}");
            await output.WriteLineAsync($"OpenClaw setup could not complete: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException)
        {
            string retryCommand = setupOptions.Fresh
                ? "clawctl setup --fresh"
                : "clawctl setup";
            log($"Setup was cancelled before it completed. Retry `{retryCommand}`.");
            await output.WriteLineAsync(
                $"OpenClaw setup was cancelled; setup may be incomplete. Rerun `{retryCommand}` to retry.")
                .ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> RunSetupCoreAsync(
        Session.SessionRuntime runtime,
        HostOptions options,
        Session.IInstallationLifecycle lifecycle,
        TextWriter output,
        Action<string> log,
        bool lockAlreadyHeld,
        CancellationToken cancellationToken,
        Action? completeIsolationSelection = null)
    {
        using Session.ISessionLockHandle? handle = lockAlreadyHeld
            ? null
            : runtime.AcquireLifecycleLock();
        runtime.SetupState.Write(new Session.SetupRecord
        {
            ApplicationId = runtime.ApplicationId,
            Phase = Session.SetupPhase.Preparing
        });
        Session.SessionStartResult session = await runtime.Coordinator
            .EnsureStartedWithResultAsync(cancellationToken).ConfigureAwait(false);
        string? supersededSandboxId = session.SupersededRecord?.SandboxId ??
            session.Record.SupersededSandboxId;
        if (supersededSandboxId is not null &&
            runtime.GatewayState.ClearForSupersededSession(supersededSandboxId))
        {
            log("Removed the gateway record for the superseded session.");
        }

        Session.SessionRecord record = session.Record;
        string helperPath = runtime.StageHelper(record);
        SessionRuntimeInstallResult agentRuntime = await runtime.Executor.InstallRuntimeAsync(
            record, helperPath, GetPackagedNodeArchivePath(options), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Node.js {agentRuntime.Version} is ready in the isolated agent session.")
            .ConfigureAwait(false);
        Gateway.GatewayPersistenceInstallResult recovery = await lifecycle
            .InstallRecoveryAsync(log, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(recovery.Message).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(recovery.Detail))
        {
            await output.WriteLineAsync(recovery.Detail).ConfigureAwait(false);
        }

        if (recovery.State != Gateway.GatewayPersistenceState.Ready)
        {
            return 1;
        }

        runtime.CompleteSetup(record, agentRuntime, startupEnabled: true);
        completeIsolationSelection?.Invoke();
        await output.WriteLineAsync("OpenClaw isolated session is ready.").ConfigureAwait(false);
        return 0;
    }

    private static string WriteFreshDiagnosticReport(
        Session.SessionRuntime runtime,
        Action<string> log)
    {
        Session.SessionStatus session = runtime.Coordinator.GetRecordedStatus();
        Gateway.GatewayStateResult gateway = runtime.GatewayState.Read();
        string directory = Path.Combine(Path.GetTempPath(), "OpenClawGatewayMSIX", "fresh-reset");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"pre-reset-{Guid.NewGuid():N}.log");
        List<string> lines =
        [
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"applicationId={runtime.ApplicationId}",
            "report=pre-reset diagnostic metadata; credentials and local file contents are excluded"
        ];
        if (session.Record is not null)
        {
            lines.Add($"sessionSandboxId={session.Record.SandboxId}");
            lines.Add($"sessionAgentUserName={session.Record.AgentUserName ?? string.Empty}");
            lines.Add($"sessionAgentUserSid={session.Record.AgentUserSid ?? string.Empty}");
        }
        else
        {
            lines.Add($"sessionRecordFault={session.Fault?.ToString() ?? "none"}");
        }

        if (gateway.Record is not null)
        {
            lines.Add($"gatewaySandboxId={gateway.Record.SandboxId}");
            lines.Add($"gatewayProcessId={gateway.Record.ProcessId}");
            lines.Add($"gatewayLaunchPending={gateway.Record.LaunchPending}");
        }
        else
        {
            lines.Add($"gatewayRecordFault={gateway.Fault?.ToString() ?? "none"}");
        }

        File.WriteAllLines(path, lines);
        log($"Captured redacted pre-reset diagnostic report at {path}.");
        return path;
    }

    private static async Task<int> RunPowerShellAsync(
        HostOptions options,
        Session.SessionRuntime runtime,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        Session.SessionRecord record = runtime.RequireSetup();
        record = await runtime.Coordinator.StartRecordedAsync(cancellationToken)
            .ConfigureAwait(false);
        string helperPath = runtime.RequireStagedHelper(record);
        string applicationDirectory = GetPackagedApplicationDirectory(options);
        string agentNodePath = runtime.RequireAgentNodePath(
            GetPackagedNodeArchivePath(options));
        string nodeDirectory = Path.GetDirectoryName(agentNodePath)
            ?? throw new Session.SessionException(
                "The agent's Node.js runtime has no parent directory.");
        SessionToolInstallResult installedTools = await runtime.Executor.InstallToolsAsync(
            record,
            helperPath,
            cancellationToken).ConfigureAwait(false);
        Session.AgentTools tools = new(
            Path.GetDirectoryName(installedTools.ShimPath)
                ?? throw new Session.SessionException(
                    "The installed agent command shim has no parent directory."),
            installedTools.ShimPath!);
        Session.AgentShell shell = Session.AgentShellResolver.Resolve(File.Exists);

        await output.WriteLineAsync(
            $"Opening {shell.DisplayName} as {record.AgentUserName ?? "the agent account"} " +
            "in the isolated session.").ConfigureAwait(false);
        await output.WriteLineAsync(
            "`openclaw` and `node` are on PATH; `clawctl` is not, because it " +
            "manages this session from outside it.").ConfigureAwait(false);
        await output.WriteLineAsync("Exit the shell to return.").ConfigureAwait(false);

        return await runtime.Executor.ExecuteCommandAsync(
            record,
            new Session.SessionCommandRequest(
                helperPath,
                shell.ExecutablePath,
                Session.AgentShellResolver.BuildArguments(
                    record.WorkspacePath!,
                    record.AgentUserName ?? "agent",
                    tools.DirectoryPath,
                    nodeDirectory),
                record.WorkspacePath!)
            {
                AdditionalEnvironment = Session.SessionExecutor.MergeEnvironment(
                    OpenClawRuntimeEnvironment.Build(
                        WindowsHostConsole.Instance.IsInteractive,
                        Environment.GetEnvironmentVariable),
                    Session.AgentToolShim.BuildEnvironment(agentNodePath, applicationDirectory))
            },
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

    private static string DescribeSessionStatus(Session.SessionStatus status) =>
        status.Availability switch
        {
            Session.SessionAvailability.None =>
                "No isolated session is recorded. Run `clawctl setup` first.",
            Session.SessionAvailability.Running =>
                $"Isolated session is running: {status.Record!.SandboxId}.",
            Session.SessionAvailability.Stale =>
                $"Recorded isolated session is stale: {status.Record!.SandboxId}. {status.Detail}",
            Session.SessionAvailability.BackendUnavailable =>
                $"MXC backend is unavailable for recorded session {status.Record!.SandboxId}: {status.Detail}",
            Session.SessionAvailability.BackendError =>
                $"MXC could not verify recorded session {status.Record!.SandboxId}: {status.Detail}",
            Session.SessionAvailability.Unusable =>
                $"The isolated-session record is unusable: {status.Detail}",
            _ => $"Isolated session is recorded: {status.Record!.SandboxId}."
        };

    internal delegate Task<int> LaunchOpenClawAsync(
        string nodePath,
        string applicationDirectory,
        IReadOnlyList<string> openClawArguments,
        CancellationToken cancellationToken,
        Action<string>? log);

    private static string GetPackagedApplicationDirectory(HostOptions options)
    {
        // Re-check File.Exists here (HostOptions.Parse already checked it)
        // so both a never-resolved and a since-removed application directory
        // fail through the same FileNotFoundException message.
        string? applicationDirectory = options.PackagedApplicationDirectory;
        string entryPoint = Path.Combine(
            applicationDirectory ?? Path.Combine(AppContext.BaseDirectory, "app"),
            "openclaw.mjs");
        if (applicationDirectory is null || !File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The packaged OpenClaw entry point was not found.",
                entryPoint);
        }

        return applicationDirectory;
    }

    private static string GetPackagedNodeArchivePath(HostOptions options)
    {
        string? archivePath = options.PackagedNodeArchivePath;
        string expectedPath = archivePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "runtime");
        if (archivePath is null || !File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The packaged Node.js runtime archive was not found.",
                expectedPath);
        }

        return archivePath;
    }
}
