using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionRoutingPolicyTests
{
    private const string PackageFamilyName = "OpenClaw.Gateway_abc123";

    private static MxcReadinessReport Ready(
        bool? backendAvailable = true,
        string? runtimeUnavailableReason = null,
        MxcHostSupport hostSupport = MxcHostSupport.Supported,
        string? probeFailureReason = null) =>
        new(
            runtimeUnavailableReason is null ? @"C:\Package\mxc\x64" : null,
            null,
            runtimeUnavailableReason,
            hostSupport,
            null,
            backendAvailable is null
                ? MxcSupportEvidence.HostBuild
                : MxcSupportEvidence.BackendProbe,
            backendAvailable is null
                ? null
                : new MxcBackendProbe(backendAvailable.Value, "base-container", []),
            probeFailureReason);

    private static Func<string, string?> Environment(string? value) =>
        name => name == SessionRoutingPolicy.ModeVariable ? value : null;

    private static GatewayIsolationSelection Enabled() =>
        new(GatewayIsolationMode.Enabled, "Gateway isolation is enabled.");

    private static GatewayIsolationSelection Disabled() =>
        new(GatewayIsolationMode.Disabled, "Gateway isolation is disabled.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetDevelopmentModeIsAutomatic(string? value)
    {
        Assert.Equal(
            SessionMode.Automatic,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("off")]
    [InlineData("no")]
    public void FalsyDevelopmentValuesDisableSessions(string value)
    {
        Assert.Equal(
            SessionMode.Disabled,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("On")]
    [InlineData("yes")]
    public void TruthyDevelopmentValuesRequireSessions(string value)
    {
        Assert.Equal(
            SessionMode.Required,
            SessionRoutingPolicy.ReadMode(Environment(value)));
    }

    [Fact]
    public void UnrecognizedDevelopmentValueIsRejectedRatherThanTreatedAsOff()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionRoutingPolicy.ReadMode(Environment("maybe")));

        Assert.Contains("maybe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledSelectionUsesTheSessionWhenAvailable()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            Enabled(),
            PackageFamilyName,
            Ready());

        Assert.Equal(SessionRouting.Session, decision.Routing);
        Assert.Contains("enabled", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BackendProbeOverridesAnUnsupportedBuildVerdict()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            Enabled(),
            PackageFamilyName,
            Ready(backendAvailable: true, hostSupport: MxcHostSupport.Unsupported));

        Assert.Equal(SessionRouting.Session, decision.Routing);
    }

    [Fact]
    public void DisabledSelectionRunsDirectlyWithoutBackendAvailability()
    {
        SessionRoutingDecision decision = SessionRoutingPolicy.Decide(
            Disabled(),
            PackageFamilyName,
            Ready(runtimeUnavailableReason: "absent"));

        Assert.Equal(SessionRouting.Direct, decision.Routing);
        Assert.Contains("disabled", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unpackaged")]
    [InlineData("no-runtime")]
    [InlineData("backend-unavailable")]
    [InlineData("unsupported-build")]
    [InlineData("unknown-support")]
    public void EnabledSelectionFailsClosedWhenIsolationCannotBeUsed(string scenario)
    {
        (string? packageFamilyName, MxcReadinessReport readiness) = scenario switch
        {
            "unpackaged" => (null, Ready()),
            "no-runtime" => (PackageFamilyName, Ready(runtimeUnavailableReason: "absent")),
            "backend-unavailable" => (PackageFamilyName, Ready(backendAvailable: false)),
            "unsupported-build" => (
                PackageFamilyName,
                Ready(backendAvailable: null, hostSupport: MxcHostSupport.Unsupported)),
            _ => (
                PackageFamilyName,
                Ready(
                    backendAvailable: null,
                    hostSupport: MxcHostSupport.Unknown,
                    probeFailureReason: "the executor crashed")),
        };

        SessionException exception = Assert.Throws<SessionException>(
            () => SessionRoutingPolicy.Decide(
                Enabled(),
                packageFamilyName,
                readiness));

        Assert.Contains("required", exception.Message, StringComparison.Ordinal);
    }
}
