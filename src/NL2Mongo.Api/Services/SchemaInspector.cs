using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;

namespace NL2Mongo.Api.Services;

/// <summary>
/// Describes a collection schema as a list of field descriptors.
/// </summary>
public record SchemaDescription(IReadOnlyList<FieldDescriptor> Fields);

/// <summary>
/// Describes a single field discovered in a collection.
/// <para>
/// <c>IsExhaustive</c> indicates whether <c>SampleValues</c> contains the complete
/// set of observed values (useful for enums) or just a representative sample.
/// </para>
/// </summary>
public record FieldDescriptor(string Name, string Type, IReadOnlyList<string> SampleValues, bool IsExhaustive);

/// <summary>
/// Inspects MongoDB collections to infer a lightweight schema description.
/// Results are cached in the provided <see cref="IMemoryCache"/> to avoid
/// repeated sampling of the database.
/// </summary>
public class SchemaInspector(IMemoryCache cache)
{
    // Inspects all non-system collections in the database.
    // The first element is the primary contacts collection (if found); others follow.
    public async Task<IReadOnlyList<(string Name, SchemaDescription Schema)>> InspectAllAsync(IMongoDatabase db)
    {
        var cursor = await db.ListCollectionNamesAsync();
        var names  = await cursor.ToListAsync();

        // Exclude internal collections; put the contacts collection first so the prompt
        // makes it clear what the pipeline runs on.
        var skip    = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "segments" };
        var ordered = names
            .Where(n => !skip.Contains(n))
            .OrderByDescending(n => n.Equals("contacts", StringComparison.OrdinalIgnoreCase)
                                 || n.Equals("Contacts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<(string, SchemaDescription)>();
        foreach (var name in ordered)
            result.Add((name, await InspectAsync(db, name)));
        return result;
    }

    public async Task<SchemaDescription> InspectAsync(IMongoDatabase db, string collectionName)
    {
        // Use a per-database/per-collection cache key so schemas are scoped by tenant DB.
        var cacheKey = $"schema:{db.DatabaseNamespace.DatabaseName}:{collectionName}";
        // If cached, return immediately to avoid scanning the collection.
        if (cache.TryGetValue(cacheKey, out SchemaDescription? cached))
            return cached!;

        var collection = db.GetCollection<BsonDocument>(collectionName);
        // Sample up to 50 documents to infer field names, types and example values.
        var samples = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Limit(50)
            .ToListAsync();

        var fieldTypes  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in samples)
        {
            foreach (var element in doc)
            {
                // Skip MongoDB internal id field; it's not relevant to schema prompts.
                if (element.Name == "_id") continue;

                // Record the first-seen inferred type for this field.
                fieldTypes.TryAdd(element.Name, InferType(element.Value));

                // Ensure a list exists for sample values and collect examples.
                if (!fieldValues.ContainsKey(element.Name))
                    fieldValues[element.Name] = new List<string>();

                CollectSampleValues(element.Value, fieldValues[element.Name]);
            }
        }

        var descriptors = fieldTypes.Keys.Select(name =>
        {
            var distinct = fieldValues[name].Distinct().ToList();
            var isExhaustive = distinct.Count <= 15;
            // Always show some sample values so the model knows the data format.
            // For low-cardinality fields show all values; for high-cardinality show 5.
            IReadOnlyList<string> sampleValues = distinct.Count > 0
                ? (isExhaustive ? distinct : distinct.Take(5).ToList())
                : [];
            return new FieldDescriptor(name, fieldTypes[name], sampleValues, isExhaustive);
        }).ToList();

        var schema = new SchemaDescription(descriptors);
        // Cache the computed schema for 10 minutes to reduce DB IO.
        cache.Set(cacheKey, schema, TimeSpan.FromMinutes(10));
        return schema;
    }

    /// <summary>
    /// Infer a simple type name from a BSON value. The set of types is intentionally
    /// small because the downstream LLM prompt only needs coarse-grained types.
    /// </summary>
    private static string InferType(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 or BsonType.Int64 or BsonType.Double => "int",
        BsonType.Boolean                                    => "bool",
        BsonType.DateTime                                   => "date",
        BsonType.Array                                      => "string[]",
        _                                                   => "string"
    };

    /// <summary>
    /// Collects representative string sample values from a BSON value into the provided list.
    /// Only string values are recorded (arrays of strings are flattened); other value kinds are ignored.
    /// </summary>
    private static void CollectSampleValues(BsonValue value, List<string> values)
    {
        if (value is BsonString s)
            values.Add(s.Value);
        else if (value is BsonArray arr)
            values.AddRange(arr.OfType<BsonString>().Select(x => x.Value));
    }
}
