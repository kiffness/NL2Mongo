using System.Text.Json;

namespace NL2Mongo.Api.Records;

public record AggregationRequest(string Description);

public record AggregationPreview(
    IReadOnlyList<JsonElement> Results,
    int Total,
    string GeneratedPipeline,
    long ElapsedMs,
    string? Hint = null);
