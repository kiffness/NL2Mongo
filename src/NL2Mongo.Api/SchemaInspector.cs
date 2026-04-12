using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using MongoDB.Driver;

public record SchemaDescription(IReadOnlyList<FieldDescriptor> Fields);
public record FieldDescriptor(string Name, string Type, IReadOnlyList<string> SampleValues);

public class SchemaInspector(IMemoryCache cache)
{
    public async Task<SchemaDescription> InspectAsync(IMongoDatabase db, string collectionName)
    {
        var cacheKey = $"schema:{db.DatabaseNamespace.DatabaseName}:{collectionName}";
        if (cache.TryGetValue(cacheKey, out SchemaDescription? cached))
            return cached!;

        var collection = db.GetCollection<BsonDocument>(collectionName);
        var samples = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Limit(50)
            .ToListAsync();

        var fieldTypes  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in samples)
        {
            foreach (var element in doc)
            {
                if (element.Name == "_id") continue;

                fieldTypes.TryAdd(element.Name, InferType(element.Value));

                if (!fieldValues.ContainsKey(element.Name))
                    fieldValues[element.Name] = [];

                CollectSampleValues(element.Value, fieldValues[element.Name]);
            }
        }

        var descriptors = fieldTypes.Keys.Select(name =>
        {
            var distinct = fieldValues[name].Distinct().ToList();
            IReadOnlyList<string> sampleValues = distinct.Count <= 15 ? distinct : [];
            return new FieldDescriptor(name, fieldTypes[name], sampleValues);
        }).ToList();

        var schema = new SchemaDescription(descriptors);
        cache.Set(cacheKey, schema, TimeSpan.FromMinutes(10));
        return schema;
    }

    private static string InferType(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 or BsonType.Int64 or BsonType.Double => "int",
        BsonType.Boolean                                    => "bool",
        BsonType.DateTime                                   => "date",
        BsonType.Array                                      => "string[]",
        _                                                   => "string"
    };

    private static void CollectSampleValues(BsonValue value, List<string> values)
    {
        if (value is BsonString s)
            values.Add(s.Value);
        else if (value is BsonArray arr)
            values.AddRange(arr.OfType<BsonString>().Select(x => x.Value));
    }
}
