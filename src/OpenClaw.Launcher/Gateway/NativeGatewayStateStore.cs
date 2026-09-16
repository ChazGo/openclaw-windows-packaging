using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Durable ownership evidence for a signed-in-user gateway.</summary>
internal sealed record NativeGatewayRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("launchPending")]
    public bool LaunchPending { get; init; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("processCreationTimeUtc")]
    public DateTimeOffset ProcessCreationTimeUtc { get; init; }

    [JsonPropertyName("ownerSid")]
    public string OwnerSid { get; init; } = string.Empty;

    [JsonPropertyName("windowsSessionId")]
    public int WindowsSessionId { get; init; }

    [JsonPropertyName("nodePath")]
    public string NodePath { get; init; } = string.Empty;

    [JsonPropertyName("entryPointPath")]
    public string EntryPointPath { get; init; } = string.Empty;

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; init; } = string.Empty;

    [JsonPropertyName("packageGeneration")]
    public string PackageGeneration { get; init; } = string.Empty;

    [JsonPropertyName("configuredPort")]
    public int? ConfiguredPort { get; init; }

    [JsonPropertyName("observedPorts")]
    public IReadOnlyList<int>? ObservedPorts { get; init; }

    [JsonPropertyName("logPath")]
    public string LogPath { get; init; } = string.Empty;

    [JsonPropertyName("intentCreatedUtc")]
    public DateTimeOffset IntentCreatedUtc { get; init; }

    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; init; }

}

internal enum NativeGatewayStateFault
{
    Missing,
    Unreadable,
    UnsupportedSchema,
    Incomplete,
}

internal sealed record NativeGatewayStateResult(
    NativeGatewayRecord? Record,
    NativeGatewayStateFault? Fault,
    string? Detail);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeGatewayRecord))]
internal sealed partial class NativeGatewayStateJsonContext : JsonSerializerContext;

internal class NativeGatewayStateStore
{
    public const int CurrentSchemaVersion = 2;

    private readonly string _filePath;

    public NativeGatewayStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public virtual NativeGatewayStateResult Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.Missing,
                "No signed-in-user gateway has been recorded.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.Unreadable,
                $"The signed-in-user gateway record could not be read: {exception.Message}");
        }

        NativeGatewayRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(
                text,
                NativeGatewayStateJsonContext.Default.NativeGatewayRecord);
        }
        catch (JsonException exception)
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.Unreadable,
                $"The signed-in-user gateway record is not valid JSON: {exception.Message}");
        }

        if (record is null)
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.Unreadable,
                "The signed-in-user gateway record is empty.");
        }

        if (record.SchemaVersion != CurrentSchemaVersion)
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.UnsupportedSchema,
                $"The signed-in-user gateway record uses schema version " +
                $"{record.SchemaVersion}, which this installation does not support.");
        }

        if (!IsComplete(record))
        {
            return new NativeGatewayStateResult(
                null,
                NativeGatewayStateFault.Incomplete,
                "The signed-in-user gateway record is missing required ownership evidence.");
        }

        return new NativeGatewayStateResult(record, null, null);
    }

    public virtual void Write(NativeGatewayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(
            record with { SchemaVersion = CurrentSchemaVersion },
            NativeGatewayStateJsonContext.Default.NativeGatewayRecord);
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, text);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public virtual void Clear()
    {
        try
        {
            File.Delete(_filePath);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static bool IsComplete(NativeGatewayRecord record) =>
        !string.IsNullOrWhiteSpace(record.OwnerSid) &&
        record.WindowsSessionId >= 0 &&
        !string.IsNullOrWhiteSpace(record.NodePath) &&
        Path.IsPathFullyQualified(record.NodePath) &&
        !string.IsNullOrWhiteSpace(record.EntryPointPath) &&
        Path.IsPathFullyQualified(record.EntryPointPath) &&
        !string.IsNullOrWhiteSpace(record.WorkingDirectory) &&
        Path.IsPathFullyQualified(record.WorkingDirectory) &&
        !string.IsNullOrWhiteSpace(record.PackageGeneration) &&
        !string.IsNullOrWhiteSpace(record.LogPath) &&
        Path.IsPathFullyQualified(record.LogPath) &&
        record.IntentCreatedUtc != default &&
        record.ConfiguredPort is not (< 1 or > 65535) &&
        (record.ObservedPorts is null ||
         record.ObservedPorts.All(static port => port is > 0 and <= 65535)) &&
        (record.LaunchPending ||
         (record.ProcessId > 0 &&
          record.ProcessCreationTimeUtc != default &&
          record.StartedUtc != default));
}
