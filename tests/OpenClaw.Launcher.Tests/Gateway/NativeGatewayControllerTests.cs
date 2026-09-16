using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using System.Diagnostics;
using System.Security.Principal;

namespace OpenClaw.Launcher.Tests.Gateway;

internal sealed class FakeNativeGatewayProcess : INativeGatewayProcess
{
    public NativeGatewayLaunchOutcome LaunchOutcome { get; set; } =
        new(4321, NativeGatewayControllerTests.ProcessTime);

    public NativeGatewayProcessSnapshot Inspection { get; set; } = new();

    public NativeGatewayProcessSnapshot StopResult { get; set; } = new();

    public int LaunchCount { get; private set; }

    public int InspectCount { get; private set; }

    public int StopCount { get; private set; }

    public NativeGatewayLaunchRequest? LastRequest { get; private set; }

    public Task<NativeGatewayLaunchOutcome> LaunchAsync(
        NativeGatewayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        LaunchCount++;
        LastRequest = request;
        return Task.FromResult(LaunchOutcome);
    }

    public Task<NativeGatewayProcessSnapshot> InspectAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        InspectCount++;
        return Task.FromResult(Inspection);
    }

    public Task<NativeGatewayProcessSnapshot> StopAsync(
        NativeGatewayRecord record,
        CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.FromResult(StopResult);
    }
}

internal sealed class FakeNativeGatewayHealthProbe : INativeGatewayHealthProbe
{
    public NativeGatewayHealthResult Result { get; set; } = new(Healthy: true);

    public List<int> Ports { get; } = [];

    public Task<NativeGatewayHealthResult> ProbeAsync(
        int port,
        CancellationToken cancellationToken)
    {
        Ports.Add(port);
        return Task.FromResult(Result);
    }
}

internal sealed class FailingPublicationStore(string path)
    : NativeGatewayStateStore(path)
{
    private int _writes;

    public override void Write(NativeGatewayRecord record)
    {
        _writes++;
        if (_writes == 2)
        {
            throw new IOException("publication failed");
        }

        base.Write(record);
    }
}

public sealed class NativeGatewayControllerTests : IDisposable
{
    internal static readonly DateTimeOffset ProcessTime =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private const string OwnerSid = "S-1-5-21-1000";
    private const int WindowsSessionId = 7;
    private const string CurrentGeneration = "OpenClaw_2.0.0.0_x64";
    private const string OldGeneration = "OpenClaw_1.0.0.0_x64";
    private readonly string _root = TestDirectory.Create();
    private readonly FakeNativeGatewayProcess _process = new();
    private readonly FakeNativeGatewayHealthProbe _health = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string StatePath => Path.Combine(_root, "native-gateway.json");

    private string NodePath => Path.Combine(_root, "NodeJS", "node.exe");

    private string EntryPointPath =>
        Path.Combine(_root, "app", "openclaw.mjs");

    private string LogPath => Path.Combine(_root, "Logs", "gateway.log");

    private NativeGatewayStateStore Store => new(StatePath);

    private NativeGatewayLaunchRequest Request(int? port = 18789) =>
        new(
            NodePath,
            EntryPointPath,
            CurrentGeneration,
            OwnerSid,
            WindowsSessionId,
            port,
            LogPath);

    private NativeGatewayRecord Record(
        string generation = CurrentGeneration,
        bool launchPending = false)
    {
        return new()
        {
            LaunchPending = launchPending,
            ProcessId = launchPending ? 0 : 4321,
            ProcessCreationTimeUtc = launchPending ? default : ProcessTime,
            OwnerSid = OwnerSid,
            WindowsSessionId = WindowsSessionId,
            NodePath = NodePath,
            EntryPointPath = EntryPointPath,
            PackageGeneration = generation,
            ConfiguredPort = 18789,
            ObservedPorts = launchPending ? null : [18789],
            LogPath = LogPath,
            IntentCreatedUtc = ProcessTime.AddSeconds(-1),
            StartedUtc = launchPending ? default : ProcessTime
        };
    }

    private NativeGatewayProcessSnapshot HealthySnapshot(
        string generation = CurrentGeneration)
    {
        return
        new()
        {
            ProcessFound = true,
            ProcessCreationTimeUtc = ProcessTime,
            OwnerSid = OwnerSid,
            WindowsSessionId = WindowsSessionId,
            ExecutablePath = NodePath,
            PackageGeneration = generation,
            CommandLineArguments = [NodePath, EntryPointPath, "gateway", "run"],
            ListeningPorts = [18789]
        };
    }

    private NativeGatewayController CreateController(
        NativeGatewayStateStore? store = null) =>
        new(
            store ?? Store,
            _process,
            _health,
            () => Request(),
            () => CurrentGeneration,
            new AlwaysFreeLock(),
            _ => { },
            new FixedTimeProvider(ProcessTime));

    [Fact]
    public async Task HappyStartStatusAndStopPreserveVerifiedOwnership()
    {
        _process.Inspection = HealthySnapshot();

        GatewayLifecycleStartResult started = await CreateController()
            .StartAsync(CancellationToken.None);
        NativeGatewayRecord evidence = Store.Read().Record!;
        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        GatewayStopResult stopped = await CreateController()
            .StopAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Running, started.State);
        Assert.Equal(GatewayState.Running, status.State);
        Assert.True(stopped.Stopped);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _process.StopCount);
        Assert.False(evidence.LaunchPending);
        Assert.Equal(4321, evidence.ProcessId);
        Assert.Equal(ProcessTime, evidence.ProcessCreationTimeUtc);
        Assert.Equal(OwnerSid, evidence.OwnerSid);
        Assert.Equal(WindowsSessionId, evidence.WindowsSessionId);
        Assert.Equal(NodePath, evidence.NodePath);
        Assert.Equal(EntryPointPath, evidence.EntryPointPath);
        Assert.Equal(CurrentGeneration, evidence.PackageGeneration);
        Assert.Equal([18789], evidence.ObservedPorts);
        Assert.Equal(LogPath, evidence.LogPath);
        Assert.Equal(
            GatewayIsolationPolicy.EnvironmentValue(GatewayIsolationMode.Disabled),
            OpenClawRuntimeEnvironment.Build(GatewayIsolationMode.Disabled)[
                OpenClawRuntimeEnvironment.GatewayIsolationVariable]);
        Assert.Equal(NativeGatewayStateFault.Missing, Store.Read().Fault);
    }

    [Fact]
    public async Task StartIsIdempotentForHealthyOwnedProcess()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot();

        GatewayLifecycleStartResult result = await CreateController()
            .StartAsync(CancellationToken.None);

        Assert.True(result.AlreadyRunning);
        Assert.Equal(GatewayState.Running, result.State);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task ReusedProcessIdentifierIsStoppedNotOwned()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot() with
        {
            ProcessCreationTimeUtc = ProcessTime.AddMinutes(1)
        };

        GatewayLifecycleStatus result = await CreateController()
            .GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Stopped, result.State);
        Assert.Contains("reused", result.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("session")]
    [InlineData("executable")]
    [InlineData("entryPoint")]
    public async Task IdentityMismatchIsAmbiguousAndNeverStops(string mismatch)
    {
        Store.Write(Record());
        NativeGatewayProcessSnapshot snapshot = HealthySnapshot();
        _process.Inspection = mismatch switch
        {
            "sid" => snapshot with { OwnerSid = "S-1-5-21-9999" },
            "session" => snapshot with { WindowsSessionId = 99 },
            "executable" => snapshot with
            {
                ExecutablePath = Path.Combine(_root, "other", "node.exe")
            },
            "entryPoint" => snapshot with
            {
                CommandLineArguments =
                    [NodePath, @"C:\other\openclaw.mjs", "gateway", "run"]
            },
            _ => throw new InvalidOperationException()
        };

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        GatewayStopResult stopped = await CreateController()
            .StopAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Unknown, status.State);
        Assert.False(stopped.Succeeded);
        Assert.Equal(0, _process.StopCount);
        Assert.NotNull(Store.Read().Record);
    }

    [Fact]
    public async Task OldGenerationIsStaleAndCannotStartOverLiveProcess()
    {
        Store.Write(Record(OldGeneration));
        _process.Inspection = HealthySnapshot(OldGeneration);

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => CreateController().StartAsync(CancellationToken.None));

        Assert.Equal(GatewayState.Stale, status.State);
        Assert.Contains("older package generation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task OldGenerationCanRestartOnlyAfterOldProcessIsAbsent()
    {
        Store.Write(Record(OldGeneration));
        _process.Inspection = new NativeGatewayProcessSnapshot();

        await CreateController().StartAsync(CancellationToken.None);

        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(CurrentGeneration, Store.Read().Record!.PackageGeneration);
    }

    [Fact]
    public async Task AbsentOldGenerationIsReportedAsStale()
    {
        Store.Write(Record(OldGeneration));
        _process.Inspection = new NativeGatewayProcessSnapshot();

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Stale, status.State);
    }

    [Fact]
    public async Task LiveOwnedProcessWithoutHealthyEndpointIsUnhealthy()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot();
        _health.Result = new NativeGatewayHealthResult(
            Healthy: false,
            "connection refused");

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Unhealthy, status.State);
        Assert.Contains("connection refused", status.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingLaunchBlocksInspectionStopAndDuplicateLaunch()
    {
        Store.Write(Record(launchPending: true));

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        GatewayStopResult stopped = await CreateController()
            .StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SessionException>(
            () => CreateController().StartAsync(CancellationToken.None));

        Assert.Equal(GatewayState.Unknown, status.State);
        Assert.False(stopped.Succeeded);
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.InspectCount);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":99}""")]
    [InlineData("""{"schemaVersion":1}""")]
    public async Task UnusableRecordsAreUnknownAndBlockLaunch(string content)
    {
        File.WriteAllText(StatePath, content);

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SessionException>(
            () => CreateController().StartAsync(CancellationToken.None));

        Assert.Equal(GatewayState.Unknown, status.State);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task FailedEvidencePublicationRetainsPendingIntent()
    {
        _process.Inspection = HealthySnapshot();
        var store = new FailingPublicationStore(StatePath);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => CreateController(store).StartAsync(CancellationToken.None));

        Assert.Contains("publication failed", exception.Message, StringComparison.Ordinal);
        NativeGatewayRecord pending = Store.Read().Record!;
        Assert.True(pending.LaunchPending);
        Assert.Equal(1, _process.LaunchCount);
    }

    [Fact]
    public async Task FailedLaunchRetainsPendingIntent()
    {
        var process = new ThrowingNativeGatewayProcess();
        NativeGatewayController controller = new(
            Store,
            process,
            _health,
            () => Request(),
            () => CurrentGeneration,
            new AlwaysFreeLock(),
            _ => { });

        await Assert.ThrowsAsync<IOException>(
            () => controller.StartAsync(CancellationToken.None));

        Assert.True(Store.Read().Record!.LaunchPending);
    }

    [Fact]
    public async Task AmbiguousInspectionBlocksDuplicateLaunch()
    {
        Store.Write(Record());
        _process.Inspection = new NativeGatewayProcessSnapshot
        {
            ProcessFound = true,
            Error = "access denied"
        };

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SessionException>(
            () => CreateController().StartAsync(CancellationToken.None));

        Assert.Equal(GatewayState.Unknown, status.State);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task MismatchedPackageGenerationEvidenceBlocksOwnership()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot(OldGeneration);

        GatewayLifecycleStatus status = await CreateController()
            .GetStatusAsync(CancellationToken.None);
        GatewayStopResult stopped = await CreateController()
            .StopAsync(CancellationToken.None);

        Assert.Equal(GatewayState.Unknown, status.State);
        Assert.False(stopped.Succeeded);
        Assert.Equal(0, _process.StopCount);
    }

    internal sealed class ThrowingNativeGatewayProcess : INativeGatewayProcess
    {
        public Task<NativeGatewayLaunchOutcome> LaunchAsync(
            NativeGatewayLaunchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<NativeGatewayLaunchOutcome>(
                new IOException("launch failed"));

        public Task<NativeGatewayProcessSnapshot> InspectAsync(
            int processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NativeGatewayProcessSnapshot());

        public Task<NativeGatewayProcessSnapshot> StopAsync(
            NativeGatewayRecord record,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NativeGatewayProcessSnapshot());
    }

    [Fact]
    public async Task FailedStopRetainsEvidence()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot();
        _process.StopResult = HealthySnapshot() with { Error = "termination denied" };

        GatewayStopResult result = await CreateController()
            .StopAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(Store.Read().Record);
    }

    [Fact]
    public async Task ReadOnlyStatusDoesNotRewriteEvidence()
    {
        Store.Write(Record());
        _process.Inspection = HealthySnapshot();
        string before = File.ReadAllText(StatePath);

        await CreateController().GetStatusAsync(CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Fact]
    public void NativeLaunchUsesNoShellAndDisabledIsolationEnvironment()
    {
        NativeGatewayLaunchRequest request = Request();
        Directory.CreateDirectory(Path.GetDirectoryName(NodePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPointPath)!);
        File.WriteAllText(NodePath, "node");
        File.WriteAllText(EntryPointPath, "entry");

        System.Diagnostics.ProcessStartInfo startInfo =
            WindowsNativeGatewayProcess.CreateStartInfo(request);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(NodePath, startInfo.FileName);
        Assert.Equal(
            [EntryPointPath, "gateway", "run", "--port", "18789"],
            startInfo.ArgumentList);
        Assert.Equal(
            "disabled",
            startInfo.Environment[OpenClawRuntimeEnvironment.GatewayIsolationVariable]);
    }

    [Fact]
    public async Task WindowsInspectionReadsCurrentProcessOwnership()
    {
        using Process current = Process.GetCurrentProcess();

        NativeGatewayProcessSnapshot snapshot =
            await new WindowsNativeGatewayProcess().InspectAsync(
                current.Id,
                CancellationToken.None);

        Assert.True(snapshot.ProcessFound);
        Assert.Null(snapshot.Error);
        Assert.NotNull(snapshot.ProcessCreationTimeUtc);
        Assert.Equal(current.SessionId, snapshot.WindowsSessionId);
        Assert.Equal(
            WindowsIdentity.GetCurrent().User!.Value,
            snapshot.OwnerSid);
        Assert.Equal(
            Path.GetFullPath(current.MainModule!.FileName),
            Path.GetFullPath(snapshot.ExecutablePath!),
            ignoreCase: true);
        Assert.NotEmpty(snapshot.CommandLineArguments!);
    }
}
