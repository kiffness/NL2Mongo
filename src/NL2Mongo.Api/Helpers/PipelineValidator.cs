using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace NL2Mongo.Api.Helpers;

public static class PipelineValidator
{
    private static readonly HashSet<string> ProhibitedStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "$out", "$merge"
    };

    // Operators whose values must be numeric — coerce quoted strings to numbers.
    private static readonly HashSet<string> NumericComparisonOps = new()
    {
        "$gt", "$lt", "$gte", "$lte"
    };

    public static Result<BsonArray> Validate(string json)
    {
        BsonArray pipeline;
        try { pipeline = BsonSerializer.Deserialize<BsonArray>(json); }
        catch { return Result<BsonArray>.Fail(new Error("Generated pipeline is not a valid JSON array", ErrorType.Validation)); }

        if (pipeline.Count == 0)
            return Result<BsonArray>.Fail(new Error("Pipeline must contain at least one stage", ErrorType.Validation));

        foreach (var element in pipeline)
        {
            if (element is not BsonDocument stage)
                return Result<BsonArray>.Fail(new Error("Each pipeline stage must be a document", ErrorType.Validation));

            foreach (var key in stage.Names)
            {
                if (ProhibitedStages.Contains(key))
                    return Result<BsonArray>.Fail(new Error($"Pipeline stage '{key}' is not allowed", ErrorType.Validation));
            }
        }

        // LLMs sometimes wrap numeric values in quotes (e.g. "$gt": "1").
        // Coerce those to actual numbers so BSON type comparisons work correctly.
        CoerceNumericStrings(pipeline);

        return Result<BsonArray>.Ok(pipeline);
    }

    // Walks the entire pipeline tree. When a comparison operator holds a
    // quoted string that parses as a number, replace it with a BsonInt64 or
    // BsonDouble so MongoDB can compare it against numeric fields.
    private static void CoerceNumericStrings(BsonValue node)
    {
        if (node is BsonArray arr)
        {
            foreach (var item in arr)
                CoerceNumericStrings(item);
        }
        else if (node is BsonDocument doc)
        {
            foreach (var name in doc.Names.ToList())
            {
                var child = doc[name];
                if (NumericComparisonOps.Contains(name) && child is BsonString s)
                {
                    if (long.TryParse(s.Value, out var l))
                        doc[name] = new BsonInt64(l);
                    else if (double.TryParse(s.Value,
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out var d))
                        doc[name] = new BsonDouble(d);
                }
                else
                {
                    CoerceNumericStrings(child);
                }
            }
        }
    }
}
