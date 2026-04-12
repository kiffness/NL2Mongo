using System.Text.Json;

public record SegmentRequest(string Description);

public record SegmentPreview(
    IReadOnlyList<JsonElement> Contacts,
    int Total,
    string GeneratedQuery);
