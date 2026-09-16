using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayIsolationTransitionManagerTests : IDisposable
{
    private const string OwnerSid = "S-1-5-21-1000";
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task StatusIsReadOnlyAndReportsSelectedTarget()
    {
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        using var output = new StringWriter();
        GatewayIsolationTransitionManager manager = CreateManager(
            store, isolated, native, output: output);

        int result = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(["readiness", "status"], isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
        Assert.Contains("Gateway isolation: enabled.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{not json""")]
    [InlineData("""{"schemaVersion":99,"mode":"Enabled","ownerSid":"S-1-5-21-1000","updatedUtc":"2026-09-15T22:00:00Z"}""")]
    [InlineData("""{"schemaVersion":1,"mode":"Enabled","ownerSid":"S-1-5-21-2000","updatedUtc":"2026-09-15T22:00:00Z"}""")]
    public async Task StatusFailsClosedForUnusableSelection(string json)
    {
        string path = StatePath;
        await File.WriteAllTextAsync(path, json);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            new GatewayIsolationStateStore(path),
            isolated,
            native).GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task MissingSelectionUsesInstalledDefaultWithoutPublishingState()
    {
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        GatewayIsolationStateStore store = new(StatePath);

        int result = await CreateManager(store, isolated, native)
            .GetStatusAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(["readiness", "status"], isolated.Calls);
        Assert.False(File.Exists(StatePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("maybe")]
    public async Task DisableRequiresClearAffirmativeInput(string response)
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        GatewayIsolationTransitionManager manager = CreateManager(
            store,
            isolated,
            native,
            input: new StringReader(response));

        int result = await manager.DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task RedirectedDisableCancelsWithoutReadingOrChangingState()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var input = new ThrowingTextReader();
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: input,
            canReadInput: false).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.False(input.ReadAttempted);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task ClosedInputCancelsWithoutChangingState()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var input = new StringReader("yes");
        input.Dispose();
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store, isolated, native, input: input).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task UnavailableInteractiveInputCancelsWithoutChangingState()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        var manager = new GatewayIsolationTransitionManager(
            store,
            OwnerSid,
            new AlwaysFreeLock(),
            isolated.Create(),
            native.Create(),
            new StringReader("yes"),
            TextWriter.Null,
            () => throw new InvalidOperationException("console unavailable"));

        int result = await manager.DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task DisableTransitionsOnlyAfterNativeGatewayIsHealthy()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("YES")).DisableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(["stop", "teardown"], isolated.Calls);
        Assert.Equal(["prepare", "start", "recovery"], native.Calls);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task DisableCommitsAfterNativeHealthBeforeIsolatedCleanup()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var events = new List<string>();
        var isolated = new RecordingTarget
        {
            OnCall = call =>
            {
                Assert.Equal(
                    call == "teardown"
                        ? GatewayIsolationMode.Disabled
                        : GatewayIsolationMode.Enabled,
                    store.Read(OwnerSid).Record!.Mode);
                events.Add($"isolated:{call}");
            }
        };
        var native = new RecordingTarget
        {
            OnCall = call =>
            {
                Assert.Equal(
                    GatewayIsolationMode.Enabled,
                    store.Read(OwnerSid).Record!.Mode);
                events.Add($"native:{call}");
            }
        };

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("yes")).DisableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(
            [
                "isolated:stop",
                "native:prepare",
                "native:start",
                "native:recovery",
                "isolated:teardown"
            ],
            events);
    }

    [Fact]
    public async Task DisableDoesNotCommitWhenNativeGatewayIsNotEstablished()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget
        {
            StartResult = new GatewayLifecycleStartResult(
                GatewayState.Starting, false, "still starting")
        };

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("y")).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
        Assert.Contains("teardown", native.Calls);
        Assert.Equal(["stop", "prepare", "start"], isolated.Calls);
    }

    [Fact]
    public async Task DisableRecoveryFailureRestoresIsolatedBeforeCleanup()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget { RecoveryResult = false };

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("yes")).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["stop", "prepare", "start"], isolated.Calls);
        Assert.Equal(["prepare", "start", "recovery", "teardown"], native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task DisableReportsIncompleteRollback()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget
        {
            PrepareResult = 1
        };
        var native = new RecordingTarget
        {
            PrepareResult = 1
        };
        using var output = new StringWriter();

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("y"),
            output: output).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Contains("ROLLBACK INCOMPLETE", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task DisablePublicationFailureRollsBackAndKeepsEnabled()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("yes"),
            writeSelection: _ => throw new IOException("publication failed"))
            .DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["stop", "prepare", "start"], isolated.Calls);
        Assert.Equal(
            ["prepare", "start", "recovery", "teardown"],
            native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task DisableSourceCleanupFailureKeepsCommittedDisabledSelection()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget
        {
            TeardownResult = new TeardownResult(
                false,
                "ownership changed",
                "cleanup refused")
        };
        using var output = new StringWriter();

        int result = await CreateManager(
            store,
            isolated,
            new RecordingTarget(),
            input: new StringReader("yes"),
            output: output).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
        Assert.Contains(
            "TRANSITION COMMITTED; SOURCE CLEANUP INCOMPLETE",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableStopsNativeThenCommitsAfterIsolatedPreparation()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(["stop", "teardown"], native.Calls);
        Assert.Equal(["teardown", "prepare", "start", "recovery"], isolated.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task EnableCommitsAfterIsolatedHealthBeforeNativeCleanup()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var events = new List<string>();
        var isolated = new RecordingTarget
        {
            OnCall = call =>
            {
                Assert.Equal(
                    GatewayIsolationMode.Disabled,
                    store.Read(OwnerSid).Record!.Mode);
                events.Add($"isolated:{call}");
            }
        };
        var native = new RecordingTarget
        {
            OnCall = call =>
            {
                Assert.Equal(
                    call == "teardown"
                        ? GatewayIsolationMode.Enabled
                        : GatewayIsolationMode.Disabled,
                    store.Read(OwnerSid).Record!.Mode);
                events.Add($"native:{call}");
            }
        };

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(
            [
                "native:stop",
                "isolated:teardown",
                "isolated:prepare",
                "isolated:start",
                "isolated:recovery",
                "native:teardown"
            ],
            events);
    }

    [Fact]
    public async Task EnableReturnsSetupRequiredAfterCommittingEstablishedIsolation()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget
        {
            StartResult = new GatewayLifecycleStartResult(
                GatewayState.Starting, false, "additional setup required")
        };

        int result = await CreateManager(store, isolated, new RecordingTarget())
            .EnableAsync(CancellationToken.None);

        Assert.Equal(GatewayIsolationTransitionManager.SetupRequiredExitCode, result);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task EnableDoesNotCommitAnUnhealthyIsolatedGateway()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget
        {
            StartResult = new GatewayLifecycleStartResult(
                GatewayState.Unhealthy,
                false,
                "not serving")
        };
        var native = new RecordingTarget();

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["teardown", "prepare", "start", "teardown"], isolated.Calls);
        Assert.Equal(["stop", "prepare", "start"], native.Calls);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task EnableRecoveryFailureRestoresNativeBeforeCleanup()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget { RecoveryResult = false };
        var native = new RecordingTarget();

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(
            ["teardown", "prepare", "start", "recovery", "teardown"],
            isolated.Calls);
        Assert.Equal(["stop", "prepare", "start"], native.Calls);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task EnableFailureBeforeCommitCleansPartialIsolationAndRestartsNative()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget { PrepareResult = 1 };
        var native = new RecordingTarget();

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["teardown", "prepare", "teardown"], isolated.Calls);
        Assert.Equal(["stop", "prepare", "start"], native.Calls);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task EnableSourceCleanupFailureKeepsCommittedEnabledSelection()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var native = new RecordingTarget
        {
            TeardownResult = new TeardownResult(
                false,
                "ownership changed",
                "cleanup refused")
        };
        using var output = new StringWriter();

        int result = await CreateManager(
            store,
            new RecordingTarget(),
            native,
            output: output).EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
        Assert.Contains(
            "TRANSITION COMMITTED; SOURCE CLEANUP INCOMPLETE",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedDestinationCleanupDoesNotRestartSourceGateway()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget
        {
            PrepareResult = 1,
            TeardownResults =
            [
                new TeardownResult(true, "stale state removed"),
                new TeardownResult(false, "inspection unavailable")
            ]
        };
        var native = new RecordingTarget();
        using var output = new StringWriter();

        int result = await CreateManager(
            store,
            isolated,
            native,
            output: output).EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["stop"], native.Calls);
        Assert.Equal(["teardown", "prepare", "teardown"], isolated.Calls);
        Assert.Contains("ROLLBACK INCOMPLETE", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task RepeatedSelectionRepairsOnlyTheSelectedIdentity(string value)
    {
        GatewayIsolationMode mode = Enum.Parse<GatewayIsolationMode>(value);
        GatewayIsolationStateStore store = CreateStore(mode);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();
        GatewayIsolationTransitionManager manager = CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("yes"));

        int result = mode == GatewayIsolationMode.Enabled
            ? await manager.EnableAsync(CancellationToken.None)
            : await manager.DisableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        RecordingTarget selected =
            mode == GatewayIsolationMode.Enabled ? isolated : native;
        RecordingTarget other =
            mode == GatewayIsolationMode.Enabled ? native : isolated;
        Assert.Equal(["prepare", "start", "recovery"], selected.Calls);
        Assert.Empty(other.Calls);
    }

    [Fact]
    public async Task SelectionIsRereadAfterLockAcquisition()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var lifecycleLock = new CallbackLock(() =>
            store.Write(new GatewayIsolationRecord
            {
                Mode = GatewayIsolationMode.Disabled,
                OwnerSid = OwnerSid
            }));
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            lifecycleLock,
            new StringReader("yes")).DisableAsync(CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Empty(isolated.Calls);
        Assert.Equal(["prepare", "start", "recovery"], native.Calls);
    }

    [Fact]
    public async Task LockContentionDoesNotReadOrMutateTargets()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            new NeverFreeLock(),
            new StringReader("yes")).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task AmbiguousNativeStopRefusesEnableWithoutProvisioning()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Disabled);
        var isolated = new RecordingTarget();
        var native = new RecordingTarget
        {
            StopResult = new GatewayStopResult(
                false,
                "ownership ambiguous",
                Succeeded: false)
        };

        int result = await CreateManager(store, isolated, native)
            .EnableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["stop"], native.Calls);
        Assert.Empty(isolated.Calls);
        Assert.Equal(GatewayIsolationMode.Disabled, store.Read(OwnerSid).Record!.Mode);
    }

    [Fact]
    public async Task UnavailableIsolatedInspectionRefusesDisableWithoutProvisioning()
    {
        GatewayIsolationStateStore store = CreateStore(GatewayIsolationMode.Enabled);
        var isolated = new RecordingTarget
        {
            StopResult = new GatewayStopResult(
                false,
                "inspection unavailable",
                Succeeded: false)
        };
        var native = new RecordingTarget();

        int result = await CreateManager(
            store,
            isolated,
            native,
            input: new StringReader("yes")).DisableAsync(CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(["stop"], isolated.Calls);
        Assert.Empty(native.Calls);
        Assert.Equal(GatewayIsolationMode.Enabled, store.Read(OwnerSid).Record!.Mode);
    }

    private string StatePath => Path.Combine(_root, "gateway-isolation.json");

    private GatewayIsolationStateStore CreateStore(GatewayIsolationMode mode)
    {
        var store = new GatewayIsolationStateStore(StatePath);
        store.Write(new GatewayIsolationRecord { Mode = mode, OwnerSid = OwnerSid });
        return store;
    }

    private static GatewayIsolationTransitionManager CreateManager(
        GatewayIsolationStateStore store,
        RecordingTarget isolated,
        RecordingTarget native,
        ISessionLock? lifecycleLock = null,
        TextReader? input = null,
        TextWriter? output = null,
        bool canReadInput = true,
        Action<GatewayIsolationMode>? writeSelection = null) =>
        new(
            store,
            OwnerSid,
            lifecycleLock ?? new AlwaysFreeLock(),
            isolated.Create(),
            native.Create(),
            input ?? new StringReader(string.Empty),
            output ?? TextWriter.Null,
            () => canReadInput,
            writeSelection);

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingTarget
    {
        public List<string> Calls { get; } = [];
        public int PrepareResult { get; init; }
        public GatewayLifecycleStartResult StartResult { get; init; } =
            new(GatewayState.Running, false, "running");
        public GatewayStopResult StopResult { get; init; } =
            new(true, "stopped");
        public TeardownResult TeardownResult { get; init; } =
            new(true, "removed");
        public IReadOnlyList<TeardownResult>? TeardownResults { get; init; }
        public bool RecoveryResult { get; init; } = true;
        public Action<string>? OnCall { get; init; }
        private int _teardownCallCount;

        public GatewayIsolationTransitionTarget Create() => new()
        {
            GetReadinessAsync = _ => RecordAsync("readiness", 0),
            PrepareUnderLockAsync = _ => RecordAsync("prepare", PrepareResult),
            WriteGatewayStatusAsync = (_, _) =>
            {
                Record("status");
                return Task.FromResult(new GatewayLifecycleStatus(
                    GatewayState.Running,
                    "running"));
            },
            StartGatewayUnderLockAsync = _ =>
            {
                Record("start");
                return Task.FromResult(StartResult);
            },
            StopGatewayUnderLockAsync = _ =>
            {
                Record("stop");
                return Task.FromResult(StopResult);
            },
            TeardownUnderLockAsync = _ =>
            {
                Record("teardown");
                TeardownResult result = TeardownResults is { Count: > 0 }
                    ? TeardownResults[Math.Min(
                        _teardownCallCount++,
                        TeardownResults.Count - 1)]
                    : TeardownResult;
                return Task.FromResult(result);
            },
            EnsureRecoveryUnderLockAsync = _ =>
            {
                Record("recovery");
                return Task.FromResult(RecoveryResult);
            }
        };

        private Task<int> RecordAsync(string name, int result)
        {
            Record(name);
            return Task.FromResult(result);
        }

        private void Record(string name)
        {
            Calls.Add(name);
            OnCall?.Invoke(name);
        }
    }

    private sealed class CallbackLock(Action onAcquire) : ISessionLock
    {
        public ISessionLockHandle? TryAcquire(TimeSpan timeout)
        {
            onAcquire();
            return new Handle();
        }

        private sealed class Handle : ISessionLockHandle
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class ThrowingTextReader : TextReader
    {
        public bool ReadAttempted { get; private set; }

        public override Task<string?> ReadLineAsync()
        {
            ReadAttempted = true;
            throw new InvalidOperationException("input unavailable");
        }
    }
}
