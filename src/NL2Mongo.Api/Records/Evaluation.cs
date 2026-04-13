using System.Text.Json;

namespace NL2Mongo.Api.Records;

public record EvaluationCase(
    string Id,
    string Description,
    string Tenant,
    int MinCount,
    Dictionary<string, JsonElement>? AllMustHave,
    Dictionary<string, JsonElement>? NoneCanHave);

public record EvaluationResult(
    string Id,
    string Description,
    bool Passed,
    string? FailReason,
    int MatchCount,
    string GeneratedQuery);

public record EvaluationReport(
    int Total,
    int Passed,
    int Failed,
    double AccuracyPercent,
    IReadOnlyList<EvaluationResult> Results);
