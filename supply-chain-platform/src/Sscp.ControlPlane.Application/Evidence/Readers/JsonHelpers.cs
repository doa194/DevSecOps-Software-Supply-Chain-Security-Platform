// Small, defensive helpers for reading scanner JSON. Reports are untrusted input: missing
// or wrongly typed fields must produce a clear rejection, never a crash or a silent pass.
using System.Globalization;
using System.Text.Json;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence.Readers;

internal static class JsonHelpers
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 64, AllowTrailingCommas = false };

    public static JsonDocument Parse(byte[] content, string format)
    {
        try
        {
            return JsonDocument.Parse(content, Options);
        }
        catch (JsonException error)
        {
            throw new ReportRejectedException($"The {format} report is not valid JSON: {error.Message}");
        }
    }

    public static string? String(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Array(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    public static JsonElement? Object(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static Severity ParseSeverity(string? value) => value?.ToUpperInvariant() switch
    {
        "CRITICAL" => Severity.Critical,
        "HIGH" => Severity.High,
        "MEDIUM" => Severity.Medium,
        "LOW" => Severity.Low,
        _ => Severity.Info,
    };

    // Scanners run against the commit checked out at /work/src (the CI helper's layout).
    // Findings are stored with repository-relative paths, so fingerprints do not depend on
    // where a scan happened.
    public const string ScanRoot = "/work/src/";

    public static string RelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "unknown-file";
        }

        var relative = path.StartsWith(ScanRoot, StringComparison.Ordinal) ? path[ScanRoot.Length..] : path;
        return relative.TrimStart('/');
    }

    public static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
