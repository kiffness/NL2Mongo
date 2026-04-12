using MongoDB.Bson;

public static class QueryValidator
{
    private static readonly HashSet<string> ProhibitedOperators =
    [
        "$set", "$unset", "$rename", "$drop", "$delete",
        "$where", "$function", "$accumulator", "$out", "$merge"
    ];

    public static Result<BsonDocument> Validate(string json)
    {
        BsonDocument doc;
        try { doc = BsonDocument.Parse(json); }
        catch { return Result<BsonDocument>.Fail(new Error("Generated filter is not valid JSON", ErrorType.Validation)); }

        var prohibited = FindProhibitedOperators(doc);
        if (prohibited.Count > 0)
            return Result<BsonDocument>.Fail(new Error(
                $"Generated filter contains prohibited operators: {string.Join(", ", prohibited)}",
                ErrorType.Validation));

        return Result<BsonDocument>.Ok(doc);
    }

    private static List<string> FindProhibitedOperators(BsonDocument doc)
    {
        var found = new List<string>();
        foreach (var element in doc)
        {
            if (ProhibitedOperators.Contains(element.Name))
                found.Add(element.Name);
            if (element.Value is BsonDocument nested)
                found.AddRange(FindProhibitedOperators(nested));
            if (element.Value is BsonArray arr)
                found.AddRange(arr.OfType<BsonDocument>().SelectMany(FindProhibitedOperators));
        }
        return found;
    }
}
