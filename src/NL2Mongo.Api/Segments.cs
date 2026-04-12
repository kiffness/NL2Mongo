public record SegmentRequest(string Description);

public record SegmentPreview(
    IReadOnlyList<Contact> Contacts,
    int Total,
    string GeneratedQuery);
