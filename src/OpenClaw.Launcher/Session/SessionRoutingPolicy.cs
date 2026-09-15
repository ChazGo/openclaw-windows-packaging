using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Whether the user has asked for, or ruled out, isolated-session execution.
/// </summary>
internal enum SessionMode
{
    /// <summary>Use a session wherever the backend reports support.</summary>
    Automatic,

    /// <summary>Never use a session.</summary>
    Disabled,

    /// <summary>Use a session, and fail rather than run outside one.</summary>
    Required,
}

/// <summary>
/// Where an <c>openclaw</c> invocation will run.
/// </summary>
internal enum SessionRouting
{
    /// <summary>Inside the owned isolated session.</summary>
    Session,

    /// <summary>Directly on the host, as before this feature existed.</summary>
    Direct,
}

/// <summary>
/// The routing choice and the reason for it, so diagnostics can explain it.
/// </summary>
internal sealed record SessionRoutingDecision(SessionRouting Routing, string Reason);

/// <summary>
/// Chooses between isolated-session and direct execution.
/// </summary>
internal static class SessionRoutingPolicy
{
    /// <summary>
    /// Overrides the automatic choice. Unset means automatic.
    /// </summary>
    public const string ModeVariable = "OPENCLAW_SESSION";

    public static SessionMode ReadMode(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        string? value = readEnvironmentVariable(ModeVariable)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return SessionMode.Automatic;
        }

        // Upper-case normalization, because CA1308 warns that lower-casing can
        // lose information for some cultures. The comparison set is ASCII, so
        // either direction matches; upper-case is the safe convention.
        return value.ToUpperInvariant() switch
        {
            "0" or "FALSE" or "OFF" or "NO" => SessionMode.Disabled,
            "1" or "TRUE" or "ON" or "YES" => SessionMode.Required,

            // An unrecognized value is not treated as "off". Silently ignoring
            // it would run outside the session the user was trying to request.
            _ => throw new SessionException(
                $"{ModeVariable} is set to '{value}', which is not one of " +
                "1, 0, true, false, on, off, yes, or no."),
        };
    }

    /// <summary>
    /// Decides where to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disabled selection runs directly without probing. An enabled
    /// selection is required and never degrades to direct execution.
    /// </para>
    /// <para>
    /// There is deliberately no fallback once isolation is enabled. A backend
    /// that breaks on a supported machine must surface, not quietly relocate
    /// the user's work onto the host with a different profile and different
    /// isolation.
    /// </para>
    /// </remarks>
    public static SessionRoutingDecision Decide(
        GatewayIsolationSelection selection,
        string? packageFamilyName,
        MxcReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(readiness);

        if (selection.Mode == GatewayIsolationMode.Disabled)
        {
            return new SessionRoutingDecision(
                SessionRouting.Direct,
                selection.Reason);
        }

        if (packageFamilyName is null)
        {
            return Unavailable(
                selection,
                "OpenClaw is not running from its installed package, so it has " +
                "no identity to provision an isolated session with.");
        }

        if (!readiness.RuntimeAvailable)
        {
            return Unavailable(
                selection,
                "The isolated-session runtime is unavailable: " +
                (readiness.RuntimeUnavailableReason ?? "no reason was reported."));
        }

        if (readiness.BackendProbe is { IsolationSessionAvailable: false })
        {
            return Unavailable(
                selection,
                "This machine's isolated-session backend reported that it is " +
                "not available.");
        }

        if (readiness.BackendProbe is null &&
            readiness.HostSupport != MxcHostSupport.Supported)
        {
            string detail = readiness.HostSupport == MxcHostSupport.Unsupported
                ? "This Windows build does not support isolated agent sessions."
                : "Isolated-session support could not be determined on this machine.";
            return Unavailable(
                selection,
                readiness.BackendProbeFailureReason is null
                    ? detail
                    : $"{detail} The backend probe failed: " +
                      readiness.BackendProbeFailureReason);
        }

        return new SessionRoutingDecision(
            SessionRouting.Session,
            $"{selection.Reason} The isolated-session backend is available.");
    }

    private static SessionRoutingDecision Unavailable(
        GatewayIsolationSelection selection,
        string reason) =>
        throw new SessionException(
            $"{selection.Reason} An isolated session is required, but one cannot " +
            $"be used. {reason}");

    public static SessionRoutingDecision Decide(
        SessionMode mode,
        string? packageFamilyName,
        MxcReadinessReport readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        if (mode == SessionMode.Disabled)
        {
            return new SessionRoutingDecision(
                SessionRouting.Direct,
                $"{ModeVariable} is set to 0.");
        }

        if (packageFamilyName is null)
        {
            return UnavailableForCapability(
                mode,
                "OpenClaw is not running from its installed package.");
        }

        if (!readiness.RuntimeAvailable)
        {
            return UnavailableForCapability(
                mode,
                readiness.RuntimeUnavailableReason ?? "The runtime is unavailable.");
        }

        if (readiness.BackendProbe is { IsolationSessionAvailable: false })
        {
            return UnavailableForCapability(
                mode,
                "The isolated-session backend is unavailable.");
        }

        if (readiness.BackendProbe is null &&
            readiness.HostSupport != MxcHostSupport.Supported)
        {
            return UnavailableForCapability(
                mode,
                readiness.HostSupport == MxcHostSupport.Unsupported
                    ? "This Windows build does not support isolated agent sessions."
                    : "Isolated-session support could not be determined.");
        }

        return new SessionRoutingDecision(
            SessionRouting.Session,
            "The isolated-session backend is available.");
    }

    private static SessionRoutingDecision UnavailableForCapability(
        SessionMode mode,
        string reason) =>
        mode == SessionMode.Required
            ? throw new SessionException(
                $"{ModeVariable} requires an isolated session. {reason}")
            : new SessionRoutingDecision(SessionRouting.Direct, reason);
}
