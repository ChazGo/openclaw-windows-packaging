using System.Security.Principal;
using System.Diagnostics;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Assembles gateway management from the running installation.</summary>
internal sealed partial class GatewayRuntime
{
    private readonly HostPaths _paths;
    private readonly SessionRuntime? _session;
    private readonly TimeProvider _clock;

    private GatewayRuntime(
        GatewayController controller,
        string helperPath,
        HostPaths paths,
        SessionRuntime? session,
        TimeProvider clock)
    {
        Controller = controller;
        HelperPath = helperPath;
        _paths = paths;
        _session = session;
        _clock = clock;
    }

    public GatewayController Controller { get; }

    public string HelperPath { get; }

    internal HostPaths Paths => _paths;

    private SessionRuntime Session =>
        _session ?? throw new InvalidOperationException(
            "This diagnostics runtime has no isolated-session client.");

    private static bool FileExists(string path) => File.Exists(path);

    public static GatewayPersistenceManager CreateRecoveryManager(Action<string> log)
        => CreateRecoveryManager(HostPaths.Create(), log);

    internal static GatewayPersistenceManager CreateRecoveryManager(
        HostPaths paths,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        string packageFamilyName = paths.PackageFamilyName
            ?? throw new SessionException(
                "OpenClaw is not running from its installed package, so it cannot configure gateway recovery.");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new SessionException(
                "The signed-in user's security identifier is unavailable, so gateway recovery cannot be configured.");

        return new GatewayPersistenceManager(
            new SchTasksGatewayScheduler(),
            new GatewayPersistenceOptions(
                userSid,
                packageFamilyName,
                paths.GatewayLauncherPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                paths.StateRoot,
                Path.Combine(AppContext.BaseDirectory, "openclaw.exe"),
                Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            log);
    }

    /// <summary>
    /// Builds the signed-in-user lifecycle without exposing it through a
    /// command handler. Layer 3 selects this target when isolation is disabled.
    /// </summary>
    internal static IGatewayLifecycle CreateNativeLifecycle(
        HostOptions options,
        NodeRuntime hostRuntime,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(hostRuntime);
        HostPaths paths = HostPaths.Create();
        string packageFamilyName = paths.PackageFamilyName
            ?? throw new SessionException(
                "OpenClaw is not running from its installed package, so it " +
                "cannot own a signed-in-user gateway.");
        return CreateNativeLifecycle(
            options,
            paths,
            new NamedSessionLock(
                PackageIdentity.ToApplicationId(packageFamilyName) + "_Installation"),
            () => hostRuntime,
            log);
    }

    internal static NativeGatewayController CreateNativeLifecycle(
        HostOptions options,
        HostPaths paths,
        ISessionLock lifecycleLock,
        Func<NodeRuntime> getHostRuntime,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lifecycleLock);
        ArgumentNullException.ThrowIfNull(getHostRuntime);
        ArgumentNullException.ThrowIfNull(log);

        string packageFamilyName = paths.PackageFamilyName
            ?? throw new SessionException(
                "OpenClaw is not running from its installed package, so it " +
                "cannot own a signed-in-user gateway.");
        string CurrentGeneration() =>
            PackageIdentity.TryGetPackageFullName()
            ?? throw new SessionException(
                "The installed package generation is unavailable.");

        NativeGatewayLaunchRequest CreateRequest()
        {
            NodeRuntime hostRuntime = getHostRuntime();
            string applicationDirectory = options.PackagedApplicationDirectory
                ?? throw new SessionException(
                    "The packaged OpenClaw application was not found.");
            string ownerSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new SessionException(
                    "The signed-in user's security identifier is unavailable.");
            GatewayLaunchConfiguration configuration =
                new GatewayConfigurationStore(paths.GatewayConfigurationPath)
                    .Resolve(paths.StateRoot, Environment.GetEnvironmentVariable);
            string workingDirectory = configuration.WorkingDirectory
                ?? throw new SessionException(
                    "The signed-in-user gateway working directory was not resolved.");

            return new NativeGatewayLaunchRequest(
                hostRuntime.ExecutablePath,
                Path.Combine(applicationDirectory, "openclaw.mjs"),
                workingDirectory,
                CurrentGeneration(),
                ownerSid,
                Process.GetCurrentProcess().SessionId,
                configuration.Port,
                Path.Combine(
                    paths.StateRoot,
                    "Logs",
                    $"native-gateway-{Guid.NewGuid():N}.log"));
        }

        return new NativeGatewayController(
            new NativeGatewayStateStore(paths.NativeGatewayStatePath),
            new WindowsNativeGatewayProcess(),
            new LoopbackGatewayHealthProbe(),
            CreateRequest,
            CurrentGeneration,
            lifecycleLock,
            log);
    }

    internal static GatewayRuntime CreateDiagnostics(
        HostPaths paths,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new GatewayRuntime(
            controller: null!,
            helperPath: string.Empty,
            paths,
            session: null,
            clock ?? TimeProvider.System);
    }

    public static GatewayRuntime Create(
        HostOptions options,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        HostPaths paths = HostPaths.Create();
        if (paths.PackageFamilyName is null)
        {
            throw new SessionException(
                "OpenClaw is not running from its installed package, so it has no identity to manage a gateway with.");
        }

        SessionRuntime session = SessionRuntime.Create(log);
        return Create(options, paths, session, log);
    }

    internal static GatewayRuntime Create(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        Action<string> log,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        return new GatewayRuntime(
            CreateController(options, paths, session, log),
            session.HelperPath,
            paths,
            session,
            clock ?? TimeProvider.System);
    }

    internal static TeardownOrchestrator CreateTeardownOrchestrator(
        HostOptions options,
        SessionRuntime session,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        HostPaths paths = HostPaths.Create();
        return CreateTeardownOrchestrator(
            options,
            paths,
            session,
            CreateRecoveryManager(paths, log),
            log);
    }

    internal static TeardownOrchestrator CreateTeardownOrchestrator(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        IGatewayPersistence recovery,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(log);

        return new TeardownOrchestrator(
            session.LifecycleLock,
            recovery,
            CreateController(options, paths, session, log),
            session.Coordinator,
            session.GatewayState,
            new GatewayConfigurationStore(paths.GatewayConfigurationPath),
            session.SetupState);
    }

    private static GatewayController CreateController(
        HostOptions options,
        HostPaths paths,
        SessionRuntime session,
        Action<string> log)
    {
        var configuration = new GatewayConfigurationStore(paths.GatewayConfigurationPath);
        Task<GatewayStartRequest> CreateRequestAsync(CancellationToken cancellationToken)
        {
            string applicationDirectory = options.PackagedApplicationDirectory
                ?? throw new SessionException(
                    "The packaged OpenClaw application was not found, so the gateway cannot be started.");
            SessionRecord sessionRecord = session.RequireSetup();
            GatewayLaunchConfiguration launch = ResolveLaunchConfiguration(
                configuration,
                sessionRecord,
                Environment.GetEnvironmentVariable);
            string archivePath = options.PackagedNodeArchivePath
                ?? throw new SessionException(
                    "The packaged Node.js runtime archive was not found.");
            Version packagedVersion = NodeRuntimeInstaller.GetArchiveVersion(
                archivePath,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            return Task.FromResult(new GatewayStartRequest(
                session.HelperPath,
                session.RequireAgentNodePath(packagedVersion),
                applicationDirectory,
                launch.Port)
            {
                WorkingDirectory = launch.WorkingDirectory
            });
        }

        return new GatewayController(
            session.Coordinator,
            new SessionGatewayClient(session.Backend, log),
            session.GatewayState,
            CreateRequestAsync,
            log,
            session.RequireSetup,
            session.LifecycleLock);
    }

    internal static GatewayLaunchConfiguration ResolveLaunchConfiguration(
        GatewayConfigurationStore configuration,
        SessionRecord sessionRecord,
        Func<string, string?> environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sessionRecord);
        ArgumentNullException.ThrowIfNull(environmentVariable);

        string workspacePath = sessionRecord.WorkspacePath
            ?? throw new SessionException(
                "The isolated session has no shared workspace for the gateway.");
        return configuration.Resolve(workspacePath, environmentVariable);
    }
}
