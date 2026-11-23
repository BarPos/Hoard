using System.Text.Json.Serialization;

namespace Hoard.Cli;

internal sealed record JsonReport(
    string Hoard,
    string Root,
    DateTimeOffset ScannedAt,
    string SizeMode,
    bool ExactSizes,
    string Probe,
    double ElapsedSeconds,
    long Size,
    long Apparent,
    long Files,
    long Directories,
    long HardLinkDuplicates,
    long HardLinkBytesSaved,
    bool Cancelled,
    IReadOnlyList<JsonEntry> Top,
    long OtherCount,
    long OtherSize,
    IReadOnlyList<JsonError> Errors);

internal sealed record JsonEntry(string Name, string Path, string Kind, long Size, long Apparent, long Files);

internal sealed record JsonError(string Path, string Message);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonReport))]
internal sealed partial class HoardJson : JsonSerializerContext;
