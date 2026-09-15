using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayIsolationStateStoreTests : IDisposable
{
    private const string OwnerSid = "S-1-5-21-1000";
    private static readonly DateTimeOffset UpdatedUtc =
        new(2026, 9, 15, 22, 0, 0, TimeSpan.Zero);
    private readonly string _root = TestDirectory.Create();

    private string Path_ => Path.Combine(_root, "gateway-isolation.json");

    private GatewayIsolationStateStore Store =>
        new(Path_, () => UpdatedUtc);

    [Fact]
    public void InstalledMissingStateRejectsDisablingEnvironmentOverride()
    {
        GatewayIsolationException exception = Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.Resolve(
                Store,
                isPackaged: true,
                OwnerSid,
                name => name == "OPENCLAW_SESSION" ? "0" : null));

        Assert.Contains("setup --no-isolation", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Disabled")]
    [InlineData("0", "Disabled")]
    [InlineData("1", "Enabled")]
    public void UnpackagedMissingStateUsesDevelopmentDefaultOrOverride(
        string? overrideValue,
        string expected)
    {
        GatewayIsolationSelection selection = GatewayIsolationPolicy.Resolve(
            Store,
            isPackaged: false,
            OwnerSid,
            name => name == "OPENCLAW_SESSION" ? overrideValue : null);

        Assert.Equal(
            Enum.Parse<GatewayIsolationMode>(expected),
            selection.Mode);
    }

    [Theory]
    [InlineData("Enabled", "0")]
    [InlineData("Disabled", "1")]
    public void PersistedStateRejectsConflictingEnvironmentOverride(
        string persistedValue,
        string overrideValue)
    {
        GatewayIsolationMode persisted =
            Enum.Parse<GatewayIsolationMode>(persistedValue);
        Store.Write(new GatewayIsolationRecord
        {
            Mode = persisted,
            OwnerSid = OwnerSid,
        });

        GatewayIsolationException exception = Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.Resolve(
                Store,
                isPackaged: true,
                OwnerSid,
                name => name == "OPENCLAW_SESSION" ? overrideValue : null));

        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Enabled", "1")]
    [InlineData("Disabled", "0")]
    public void PersistedStateAcceptsMatchingEnvironmentOverride(
        string persistedValue,
        string overrideValue)
    {
        GatewayIsolationMode persisted =
            Enum.Parse<GatewayIsolationMode>(persistedValue);
        Store.Write(new GatewayIsolationRecord
        {
            Mode = persisted,
            OwnerSid = OwnerSid,
        });

        GatewayIsolationSelection selection = GatewayIsolationPolicy.Resolve(
            Store,
            isPackaged: true,
            OwnerSid,
            name => name == "OPENCLAW_SESSION" ? overrideValue : null);

        Assert.Equal(persisted, selection.Mode);
    }

    [Theory]
    [InlineData(false, "Enabled")]
    [InlineData(true, "Disabled")]
    public void InstalledMissingStateSetupSelectsInitialModeAfterSuccess(
        bool noIsolation,
        string expected)
    {
        GatewayIsolationSetupPlan plan = GatewayIsolationPolicy.ResolveSetup(
            Store,
            isPackaged: true,
            OwnerSid,
            noIsolation,
            _ => null);

        Assert.Equal(Enum.Parse<GatewayIsolationMode>(expected), plan.Mode);
        Assert.True(plan.PersistAfterSuccess);
        Assert.Equal(
            noIsolation
                ? GatewayIsolationSetupIntentSource.NoIsolationOption
                : GatewayIsolationSetupIntentSource.DefaultSelection,
            plan.Source);
    }

    [Theory]
    [InlineData("Enabled", false, "Enabled")]
    [InlineData("Disabled", false, "Disabled")]
    [InlineData("Disabled", true, "Disabled")]
    public void InstalledSetupPreservesExistingSelection(
        string persistedValue,
        bool noIsolation,
        string expected)
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = Enum.Parse<GatewayIsolationMode>(persistedValue),
            OwnerSid = OwnerSid,
        });

        GatewayIsolationSetupPlan plan = GatewayIsolationPolicy.ResolveSetup(
            Store,
            isPackaged: true,
            OwnerSid,
            noIsolation,
            _ => null);

        Assert.Equal(Enum.Parse<GatewayIsolationMode>(expected), plan.Mode);
        Assert.False(plan.PersistAfterSuccess);
        Assert.Equal(
            noIsolation
                ? GatewayIsolationSetupIntentSource.NoIsolationOption
                : GatewayIsolationSetupIntentSource.PersistedSelection,
            plan.Source);
    }

    [Fact]
    public void InstalledDisabledSetupPreservesEnvironmentOverrideProvenance()
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Disabled,
            OwnerSid = OwnerSid,
        });

        GatewayIsolationSetupPlan plan = GatewayIsolationPolicy.ResolveSetup(
            Store,
            isPackaged: true,
            OwnerSid,
            noIsolation: false,
            name => name == "OPENCLAW_SESSION" ? "0" : null);

        Assert.Equal(GatewayIsolationMode.Disabled, plan.Mode);
        Assert.False(plan.PersistAfterSuccess);
        Assert.Equal(GatewayIsolationSetupIntentSource.EnvironmentOverride, plan.Source);
    }

    [Fact]
    public void InstalledEnabledSetupRejectsNoIsolationTransition()
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Enabled,
            OwnerSid = OwnerSid,
        });

        GatewayIsolationException exception = Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.ResolveSetup(
                Store,
                isPackaged: true,
                OwnerSid,
                noIsolation: true,
                _ => null));

        Assert.Contains("gateway-isolation disable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledMissingStateSetupRejectsDisablingEnvironmentOverride()
    {
        GatewayIsolationException exception = Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.ResolveSetup(
                Store,
                isPackaged: true,
                OwnerSid,
                noIsolation: false,
                name => name == "OPENCLAW_SESSION" ? "0" : null));

        Assert.Contains("setup --no-isolation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledNoIsolationSetupRejectsRequiredEnvironmentOverride()
    {
        GatewayIsolationException exception = Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.ResolveSetup(
                Store,
                isPackaged: true,
                OwnerSid,
                noIsolation: true,
                name => name == "OPENCLAW_SESSION" ? "1" : null));

        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordRoundTripsWithCurrentSchemaOwnerAndTimestamp()
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Disabled,
            OwnerSid = OwnerSid,
        });

        GatewayIsolationRecord record = Assert.IsType<GatewayIsolationRecord>(
            Store.Read(OwnerSid).Record);

        Assert.Equal(GatewayIsolationStateStore.CurrentSchemaVersion, record.SchemaVersion);
        Assert.Equal(GatewayIsolationMode.Disabled, record.Mode);
        Assert.Equal(OwnerSid, record.OwnerSid);
        Assert.Equal(UpdatedUtc, record.UpdatedUtc);
    }

    [Fact]
    public void ReplacingStateLeavesNoTemporaryFile()
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Enabled,
            OwnerSid = OwnerSid,
        });
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Disabled,
            OwnerSid = OwnerSid,
        });

        Assert.Equal(
            GatewayIsolationMode.Disabled,
            Store.Read(OwnerSid).Record!.Mode);
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void ForeignOwnerIsRejected()
    {
        Store.Write(new GatewayIsolationRecord
        {
            Mode = GatewayIsolationMode.Enabled,
            OwnerSid = "S-1-5-21-2000",
        });

        Assert.Equal(
            GatewayIsolationStateFault.ForeignOwner,
            Store.Read(OwnerSid).Fault);
        Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.Resolve(Store, true, OwnerSid, _ => null));
    }

    [Theory]
    [InlineData("""{not json""", "Unreadable")]
    [InlineData(
        """{"schemaVersion":99,"mode":"Enabled","ownerSid":"S-1-5-21-1000","updatedUtc":"2026-09-15T22:00:00Z"}""",
        "UnsupportedSchema")]
    [InlineData(
        """{"schemaVersion":1,"mode":"Enabled","ownerSid":"","updatedUtc":"2026-09-15T22:00:00Z"}""",
        "Invalid")]
    [InlineData(
        """{"schemaVersion":1,"ownerSid":"S-1-5-21-1000","updatedUtc":"2026-09-15T22:00:00Z"}""",
        "Invalid")]
    public void InvalidStateFailsClosed(
        string json,
        string expectedFault)
    {
        File.WriteAllText(Path_, json);

        Assert.Equal(
            Enum.Parse<GatewayIsolationStateFault>(expectedFault),
            Store.Read(OwnerSid).Fault);
        Assert.Throws<GatewayIsolationException>(() =>
            GatewayIsolationPolicy.Resolve(Store, true, OwnerSid, _ => null));
    }

    [Fact]
    public void UnknownModeCannotBeWritten()
    {
        Assert.Throws<GatewayIsolationException>(() =>
            Store.Write(new GatewayIsolationRecord
            {
                Mode = GatewayIsolationMode.Unknown,
                OwnerSid = OwnerSid,
            }));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
