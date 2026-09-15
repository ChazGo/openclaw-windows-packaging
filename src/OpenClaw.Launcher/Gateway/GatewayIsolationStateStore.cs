using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

internal enum GatewayIsolationMode
{
    Unknown,
    Enabled,
    Disabled,
}

internal sealed record GatewayIsolationRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<GatewayIsolationMode>))]
    public GatewayIsolationMode Mode { get; init; }

    [JsonPropertyName("ownerSid")]
    public string OwnerSid { get; init; } = string.Empty;

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; init; }
}

internal enum GatewayIsolationStateFault
{
    Missing,
    Unreadable,
    UnsupportedSchema,
    Invalid,
    ForeignOwner,
}

internal sealed record GatewayIsolationStateResult(
    GatewayIsolationRecord? Record,
    GatewayIsolationStateFault? Fault,
    string? Detail);

internal sealed record GatewayIsolationSelection(
    GatewayIsolationMode Mode,
    string Reason);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GatewayIsolationRecord))]
internal sealed partial class GatewayIsolationJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and atomically writes the user-owned gateway-isolation selection.
/// </summary>
internal sealed class GatewayIsolationStateStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _utcNow;

    public GatewayIsolationStateStore(
        string filePath,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string FilePath => _filePath;

    public GatewayIsolationStateResult Read(string expectedOwnerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerSid);

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.Missing,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.Unreadable,
                $"The gateway-isolation state could not be read: {exception.Message}");
        }

        GatewayIsolationRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(
                text,
                GatewayIsolationJsonContext.Default.GatewayIsolationRecord);
        }
        catch (JsonException exception)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.Unreadable,
                $"The gateway-isolation state is not valid JSON: {exception.Message}");
        }

        if (record is null)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.Unreadable,
                "The gateway-isolation state is empty.");
        }

        if (record.SchemaVersion > CurrentSchemaVersion)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.UnsupportedSchema,
                $"The gateway-isolation state uses schema version " +
                $"{record.SchemaVersion}, which is newer than this installation " +
                $"supports ({CurrentSchemaVersion}).");
        }

        if (record.SchemaVersion < 1 ||
            record.Mode is not GatewayIsolationMode.Enabled and
                not GatewayIsolationMode.Disabled ||
            string.IsNullOrWhiteSpace(record.OwnerSid) ||
            record.UpdatedUtc == default)
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.Invalid,
                "The gateway-isolation state is missing required metadata.");
        }

        if (!string.Equals(
                record.OwnerSid,
                expectedOwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            return new GatewayIsolationStateResult(
                null,
                GatewayIsolationStateFault.ForeignOwner,
                $"The gateway-isolation state belongs to '{record.OwnerSid}', " +
                $"not the signed-in user '{expectedOwnerSid}'.");
        }

        return new GatewayIsolationStateResult(record, null, null);
    }

    public void Write(GatewayIsolationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.OwnerSid);
        if (record.Mode is not GatewayIsolationMode.Enabled and
            not GatewayIsolationMode.Disabled)
        {
            throw new GatewayIsolationException(
                "Gateway-isolation mode must be Enabled or Disabled.");
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            record with
            {
                SchemaVersion = CurrentSchemaVersion,
                UpdatedUtc = _utcNow(),
            },
            GatewayIsolationJsonContext.Default.GatewayIsolationRecord);

        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}

/// <summary>
/// Resolves the persisted selection without allowing development overrides to
/// weaken an installed package's policy.
/// </summary>
internal static class GatewayIsolationPolicy
{
    public static GatewayIsolationSelection Resolve(
        GatewayIsolationStateStore store,
        bool isPackaged,
        string currentUserSid,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        GatewayIsolationStateResult state = store.Read(currentUserSid);
        if (state.Record is GatewayIsolationRecord record)
        {
            return new GatewayIsolationSelection(
                record.Mode,
                $"Gateway isolation is {Describe(record.Mode)} by persisted user state.");
        }

        if (state.Fault != GatewayIsolationStateFault.Missing)
        {
            throw new GatewayIsolationException(
                $"The gateway-isolation state could not be used: {state.Detail}");
        }

        if (isPackaged)
        {
            return new GatewayIsolationSelection(
                GatewayIsolationMode.Enabled,
                "Gateway isolation is enabled because installed packages require it by default.");
        }

        SessionMode developmentMode =
            SessionRoutingPolicy.ReadMode(readEnvironmentVariable);
        return developmentMode switch
        {
            SessionMode.Required => new GatewayIsolationSelection(
                GatewayIsolationMode.Enabled,
                $"{SessionRoutingPolicy.ModeVariable} requires gateway isolation for this unpackaged launch."),
            _ => new GatewayIsolationSelection(
                GatewayIsolationMode.Disabled,
                developmentMode == SessionMode.Disabled
                    ? $"{SessionRoutingPolicy.ModeVariable} disables gateway isolation for this unpackaged launch."
                    : "Gateway isolation is disabled by default for this unpackaged launch."),
        };
    }

    public static string GetCurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new GatewayIsolationException(
            "The signed-in user's security identifier is unavailable, so gateway-isolation state cannot be validated.");

    internal static string EnvironmentValue(GatewayIsolationMode mode) =>
        mode switch
        {
            GatewayIsolationMode.Enabled => "enabled",
            GatewayIsolationMode.Disabled => "disabled",
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Gateway-isolation mode must be Enabled or Disabled."),
        };

    private static string Describe(GatewayIsolationMode mode) =>
        EnvironmentValue(mode);
}

internal sealed class GatewayIsolationException : Exception
{
    public GatewayIsolationException()
    {
    }

    public GatewayIsolationException(string message)
        : base(message)
    {
    }

    public GatewayIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
