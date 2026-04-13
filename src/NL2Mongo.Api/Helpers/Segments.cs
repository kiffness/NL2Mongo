using System.Text.Json;

namespace NL2Mongo.Api.Helpers;

public record SegmentRequest(string Description);

public record SegmentPreview(
    IReadOnlyList<JsonElement> Contacts,
    int Total,
    string GeneratedQuery,
    long ElapsedMs);
