using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;

public static class QueryValidator
{
    // Matches ISO 8601 datetime strings e.g. "2024-01-01T00:00:00Z"
    private static readonly Regex IsoDatePattern =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);
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

        doc = NormalizeKeys(doc);

        var prohibited = FindProhibitedOperators(doc);
        if (prohibited.Count > 0)
            return Result<BsonDocument>.Fail(new Error(
                $"Generated filter contains prohibited operators: {string.Join(", ", prohibited)}",
                ErrorType.Validation));

        return Result<BsonDocument>.Ok(doc);
    }

    // Recursively trim whitespace from all key names.
    // LLMs occasionally emit " occupation" instead of "occupation".
    private static BsonDocument NormalizeKeys(BsonDocument doc)
    {
        var result = new BsonDocument();
        foreach (var element in doc)
        {
            var value = element.Value switch
            {
                BsonDocument nested => NormalizeKeys(nested),
                BsonArray arr      => NormalizeArray(arr),
                BsonString s       => NormalizeString(s.Value),
                _                  => element.Value
            };
            result[element.Name.Trim()] = value;
        }
        return result;
    }

    private static BsonArray NormalizeArray(BsonArray arr)
    {
        var result = new BsonArray();
        foreach (var item in arr)
        {
            var normalized = item switch
            {
                BsonDocument doc => NormalizeKeys(doc),
                BsonString s    => NormalizeString(s.Value),
                _               => item
            };
            result.Add(normalized);
        }
        return result;
    }

    // Trim whitespace and coerce ISO 8601 datetime strings to BsonDateTime
    // so they compare correctly against MongoDB date fields.
    private static BsonValue NormalizeString(string raw)
    {
        var s = raw.Trim();
        if (IsoDatePattern.IsMatch(s) &&
            DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt))
            return new BsonDateTime(dt);
        return new BsonString(s);
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
