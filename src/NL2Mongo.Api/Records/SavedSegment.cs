using System.Text.Json;

namespace NL2Mongo.Api.Records;

public record SegmentSaveRequest(string Name, string Description, string Query, int MatchCount);

public record SavedSegment(
    string Id,
    string Name,
    string Description,
    string Query,
    int MatchCount,
    DateTime CreatedAt);

public record SegmentRunResult(
    string SegmentId,
    string Name,
    IReadOnlyList<JsonElement> Contacts,
    int Total,
    string Query);
