using System.Runtime.InteropServices;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class NativeInstallationRuntimeTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task SetupUsesHostRuntimeAndRecoveryUnderSharedLifecycleLock()
    {
        TestContext context = CreateContext();

        int exitCode = await context.Runtime.SetupAsync(
            new SetupOptions(Fresh: false, Force: false),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, context.Lock.AcquireCount);
        Assert.Equal(1, context.Recovery.InstallCount);
        Assert.Equal(1, context.EnsureNodeCount);
        Assert.Equal(0, context.Process.LaunchCount);
    }

    [Fact]
    public async Task StatusIsReadOnlyAndDoesNotInstallOrDeleteAnything()
    {
        TestContext context = CreateContext();
        using var output = new StringWriter();

        int exitCode = await context.Runtime.GetStatusAsync(
            output,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, context.Lock.AcquireCount);
        Assert.Equal(0, context.Recovery.InstallCount);
        Assert.Equal(0, context.Recovery.UninstallCount);
        Assert.Equal(0, context.EnsureNodeCount);
        Assert.Equal(0, context.DeleteRuntimeCount);
        Assert.Equal(0, context.Process.LaunchCount);
        Assert.Equal(0, context.Process.StopCount);
    }

    [Fact]
    public async Task DiagnosticsNeverRequestsSessionAttachment()
    {
        TestContext context = CreateContext();

        int exitCode = await context.Runtime.CollectLogsAsync(
            Path.Combine(_root, "diagnostics.zip"),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal([false], context.Diagnostics.IncludeSessionValues);
        Assert.Equal(0, context.Lock.AcquireCount);
    }

    [Fact]
    public async Task TeardownUsesSharedLockAndPreservesIsolationSelection()
    {
        TestContext context = CreateContext();
        Directory.CreateDirectory(Path.GetDirectoryName(
            context.Paths.GatewayIsolationStatePath)!);
        await File.WriteAllTextAsync(
            context.Paths.GatewayIsolationStatePath,
            "selection");

        int exitCode = await context.Runtime.TeardownAsync(
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, context.Lock.AcquireCount);
        Assert.Equal(1, context.Recovery.UninstallCount);
        Assert.Equal(1, context.DeleteRuntimeCount);
        Assert.True(File.Exists(context.Paths.GatewayIsolationStatePath));
        Assert.Equal(0, context.Process.LaunchCount);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private TestContext CreateContext()
    {
        string applicationDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "fixture");
        string archivePath = Path.Combine(
            _root,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "node-v24.20.0-win-arm64.zip"
                : "node-v24.20.0-win-x64.zip");
        File.WriteAllText(archivePath, "fixture");

        HostPaths paths = HostPaths.ForRoot(
            Path.Combine(_root, "state"),
            "OpenClaw.Gateway_test");
        var lifecycleLock = new RecordingLock();
        var process = new FakeNativeGatewayProcess();
        var gateway = new NativeGatewayController(
            new NativeGatewayStateStore(paths.NativeGatewayStatePath),
            process,
            new FakeNativeGatewayHealthProbe(),
            () => throw new InvalidOperationException("Start is not expected."),
            () => "OpenClaw_1.0.0.0_x64",
            lifecycleLock,
            _ => { });
        var recovery = new RecordingPersistence();
        var diagnostics = new RecordingDiagnostics();
        int ensureNodeCount = 0;
        int deleteRuntimeCount = 0;
        var runtime = new NativeInstallationRuntime(
            new HostOptions(applicationDirectory, archivePath, []),
            paths,
            gateway,
            lifecycleLock,
            recovery,
            diagnostics,
            _ => { },
            (path, _) =>
            {
                ensureNodeCount++;
                return new NodeRuntime(
                    Path.Combine(_root, "node.exe"),
                    new Version(24, 20, 0),
                    RuntimeInformation.ProcessArchitecture);
            },
            _ => deleteRuntimeCount++);

        return new TestContext(
            runtime,
            paths,
            lifecycleLock,
            process,
            recovery,
            diagnostics,
            () => ensureNodeCount,
            () => deleteRuntimeCount);
    }

    private sealed record TestContext(
        NativeInstallationRuntime Runtime,
        HostPaths Paths,
        RecordingLock Lock,
        FakeNativeGatewayProcess Process,
        RecordingPersistence Recovery,
        RecordingDiagnostics Diagnostics,
        Func<int> ReadEnsureNodeCount,
        Func<int> ReadDeleteRuntimeCount)
    {
        public int EnsureNodeCount => ReadEnsureNodeCount();

        public int DeleteRuntimeCount => ReadDeleteRuntimeCount();
    }

    private sealed class RecordingLock : ISessionLock
    {
        public int AcquireCount { get; private set; }

        public ISessionLockHandle? TryAcquire(TimeSpan timeout)
        {
            AcquireCount++;
            return new RecordingHandle();
        }

        private sealed class RecordingHandle : ISessionLockHandle
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingPersistence : IGatewayPersistence
    {
        public int InstallCount { get; private set; }

        public int UninstallCount { get; private set; }

        public Task<GatewayPersistenceStatus> GetStatusAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceStatus(
                GatewayPersistenceState.NotInstalled,
                GatewayPersistenceLane.None,
                "not installed"));

        public Task<GatewayPersistenceInstallResult> InstallAsync(
            CancellationToken cancellationToken)
        {
            InstallCount++;
            return Task.FromResult(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "installed",
                Changed: true));
        }

        public Task<GatewayPersistenceRemovalResult> UninstallAsync(
            CancellationToken cancellationToken)
        {
            UninstallCount++;
            return Task.FromResult(new GatewayPersistenceRemovalResult(
                Succeeded: true,
                Changed: true,
                "removed"));
        }
    }

    private sealed class RecordingDiagnostics : IDiagnosticsCollector
    {
        public List<bool> IncludeSessionValues { get; } = [];

        public Task<DiagnosticsBundleResult> CollectLogsAsync(
            string? requestedPath,
            bool includeSession,
            CancellationToken cancellationToken)
        {
            IncludeSessionValues.Add(includeSession);
            return Task.FromResult(new DiagnosticsBundleResult(
                requestedPath,
                SessionReached: false,
                []));
        }
    }
}
