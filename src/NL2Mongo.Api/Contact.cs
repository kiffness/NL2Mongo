using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

[BsonIgnoreExtraElements]
public class Contact
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public int Age { get; init; }
    public string Occupation { get; init; } = "";
    public string City { get; init; } = "";
    public string Country { get; init; } = "";
    public string[] Groups { get; init; } = [];
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);
