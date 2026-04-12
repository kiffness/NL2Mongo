using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

var pack = new ConventionPack { new CamelCaseElementNameConvention() };
ConventionRegistry.Register("CamelCase", pack, t => true);

var builder = WebApplication.CreateBuilder(args);

// MongoDB — client is a singleton; database is resolved per request from X-Tenant header
var connectionString = builder.Configuration.GetConnectionString("MongoDB")!;
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

// Schema caching + LLM
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SchemaInspector>();
builder.Services.AddHttpClient<OllamaService>(client =>
    client.BaseAddress = new Uri("http://localhost:11434"));

// CORS — allow the static frontend to call the API
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

// ── Helpers ───────────────────────────────────────────────────────────────────

static IResult? TryGetTenantDb(HttpRequest request, IMongoClient client, out IMongoDatabase? db)
{
    var tenant = request.Headers["X-Tenant"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(tenant))
    {
        db = null;
        return Results.BadRequest(new { error = "X-Tenant header is required" });
    }
    db = client.GetDatabase(tenant);
    return null;
}

static async Task<string> ResolveCollectionNameAsync(IMongoDatabase db)
{
    var cursor = await db.ListCollectionNamesAsync();
    var names  = await cursor.ToListAsync();
    return names.FirstOrDefault(n => n.Equals("Contacts", StringComparison.OrdinalIgnoreCase))
           ?? "contacts";
}

var jsonSettings = new MongoDB.Bson.IO.JsonWriterSettings
{
    OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson
};

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapGet("/tenants", async (IMongoClient client) =>
{
    var excluded = new HashSet<string> { "admin", "local", "config" };
    var cursor   = await client.ListDatabaseNamesAsync();
    var names    = await cursor.ToListAsync();
    return Results.Ok(names.Where(n => !excluded.Contains(n)).OrderBy(n => n));
});

app.MapGet("/health", async (HttpRequest request, IMongoClient client) =>
{
    var error = TryGetTenantDb(request, client, out var db);
    if (error is not null) return error;

    try
    {
        await db!.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
        return Results.Ok(new { status = "ok", database = db.DatabaseNamespace.DatabaseName });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "fail", error = ex.Message }, statusCode: 503);
    }
});

app.MapGet("/contacts", async (HttpRequest request, IMongoClient client, int page = 1, int pageSize = 20) =>
{
    var error = TryGetTenantDb(request, client, out var db);
    if (error is not null) return error;

    var collectionName = await ResolveCollectionNameAsync(db!);
    var collection     = db!.GetCollection<BsonDocument>(collectionName);

    var total = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
    var docs  = await collection.Find(FilterDefinition<BsonDocument>.Empty)
        .Sort(Builders<BsonDocument>.Sort.Ascending("lastName"))
        .Skip((page - 1) * pageSize)
        .Limit(pageSize)
        .Project(Builders<BsonDocument>.Projection.Exclude("_id"))
        .ToListAsync();

    var items = docs
        .Select(d => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJson(jsonSettings)))
        .ToList();

    return Result<PagedResult<System.Text.Json.JsonElement>>.Ok(
        new PagedResult<System.Text.Json.JsonElement>(items, total, page, pageSize))
        .ToApiResult();
});

app.MapPost("/segments/preview", async (
    SegmentRequest req,
    HttpRequest request,
    IMongoClient client,
    SchemaInspector inspector,
    OllamaService ollama) =>
{
    if (string.IsNullOrWhiteSpace(req.Description))
        return Result<SegmentPreview>.Fail(new Error("Description is required", ErrorType.Validation)).ToApiResult();

    var headerError = TryGetTenantDb(request, client, out var db);
    if (headerError is not null) return headerError;

    var collectionName = await ResolveCollectionNameAsync(db!);
    var schema         = await inspector.InspectAsync(db!, collectionName);

    var filterResult = await ollama.GenerateFilterAsync(req.Description, schema);
    if (!filterResult.IsSuccess) return filterResult.ToApiResult();

    var validationResult = QueryValidator.Validate(filterResult.Value!);
    if (!validationResult.IsSuccess) return validationResult.ToApiResult();

    var cleanQuery = validationResult.Value!.ToJson(jsonSettings);

    var rawCollection = db!.GetCollection<BsonDocument>(collectionName);
    var matchedDocs   = await rawCollection
        .Find(validationResult.Value!)
        .Project(Builders<BsonDocument>.Projection.Exclude("_id"))
        .Limit(200)
        .ToListAsync();

    var items = matchedDocs
        .Select(d => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJson(jsonSettings)))
        .ToList();

    return Result<SegmentPreview>.Ok(new SegmentPreview(items, items.Count, cleanQuery))
        .ToApiResult();
});

app.MapPost("/segments/evaluate", async (
    IWebHostEnvironment env,
    IMongoClient client,
    SchemaInspector inspector,
    OllamaService ollama) =>
{
    var suitePath = Path.Combine(env.ContentRootPath, "EvaluationSuite.json");
    if (!File.Exists(suitePath))
        return Results.NotFound(new { error = "EvaluationSuite.json not found" });

    var suite = System.Text.Json.JsonSerializer.Deserialize<List<EvaluationCase>>(
        await File.ReadAllTextAsync(suitePath),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (suite is null || suite.Count == 0)
        return Results.BadRequest(new { error = "Evaluation suite is empty" });

    var results = new List<EvaluationResult>();
    foreach (var testCase in suite)
        results.Add(await RunEvaluationCaseAsync(testCase, client, inspector, ollama, jsonSettings));

    var passed = results.Count(r => r.Passed);
    return Results.Ok(new EvaluationReport(
        results.Count,
        passed,
        results.Count - passed,
        results.Count > 0 ? Math.Round((double)passed / results.Count * 100, 1) : 0,
        results));
});

app.Run();

// ── Evaluation helpers ────────────────────────────────────────────────────────

static async Task<EvaluationResult> RunEvaluationCaseAsync(
    EvaluationCase testCase,
    IMongoClient client,
    SchemaInspector inspector,
    OllamaService ollama,
    MongoDB.Bson.IO.JsonWriterSettings jsonSettings)
{
    try
    {
        var db             = client.GetDatabase(testCase.Tenant);
        var collectionName = await ResolveCollectionNameAsync(db);
        var schema         = await inspector.InspectAsync(db, collectionName);

        var filterResult = await ollama.GenerateFilterAsync(testCase.Description, schema);
        if (!filterResult.IsSuccess)
            return new EvaluationResult(testCase.Id, testCase.Description, false,
                $"LLM failed: {filterResult.Error!.Message}", 0, "");

        var validationResult = QueryValidator.Validate(filterResult.Value!);
        if (!validationResult.IsSuccess)
            return new EvaluationResult(testCase.Id, testCase.Description, false,
                $"Validation failed: {validationResult.Error!.Message}", 0, filterResult.Value!);

        var cleanQuery  = validationResult.Value!.ToJson(jsonSettings);
        var collection  = db.GetCollection<BsonDocument>(collectionName);
        var docs        = await collection
            .Find(validationResult.Value!)
            .Project(Builders<BsonDocument>.Projection.Exclude("_id"))
            .Limit(200)
            .ToListAsync();

        var items = docs
            .Select(d => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(d.ToJson(jsonSettings)))
            .ToList();

        if (items.Count < testCase.MinCount)
            return new EvaluationResult(testCase.Id, testCase.Description, false,
                $"Expected at least {testCase.MinCount} result(s), got {items.Count}", items.Count, cleanQuery);

        if (testCase.AllMustHave is not null)
        {
            foreach (var item in items)
            {
                var failedField = CheckMustHave(item, testCase.AllMustHave, mustMatch: true);
                if (failedField is not null)
                    return new EvaluationResult(testCase.Id, testCase.Description, false,
                        $"allMustHave failed on field '{failedField}'", items.Count, cleanQuery);
            }
        }

        if (testCase.NoneCanHave is not null)
        {
            foreach (var item in items)
            {
                var failedField = CheckMustHave(item, testCase.NoneCanHave, mustMatch: false);
                if (failedField is not null)
                    return new EvaluationResult(testCase.Id, testCase.Description, false,
                        $"noneCanHave failed on field '{failedField}'", items.Count, cleanQuery);
            }
        }

        return new EvaluationResult(testCase.Id, testCase.Description, true, null, items.Count, cleanQuery);
    }
    catch (Exception ex)
    {
        return new EvaluationResult(testCase.Id, testCase.Description, false,
            $"Exception: {ex.Message}", 0, "");
    }
}

// Returns the name of the first field that fails the check, or null if all pass.
// mustMatch=true  → field must equal expected value  (allMustHave)
// mustMatch=false → field must NOT equal expected value (noneCanHave)
static string? CheckMustHave(
    System.Text.Json.JsonElement doc,
    Dictionary<string, System.Text.Json.JsonElement> assertions,
    bool mustMatch)
{
    foreach (var (field, expected) in assertions)
    {
        if (!doc.TryGetProperty(field, out var actual)) return mustMatch ? field : null;
        var matches = JsonValuesMatch(actual, expected);
        if (mustMatch && !matches) return field;
        if (!mustMatch && matches) return field;
    }
    return null;
}

static bool JsonValuesMatch(System.Text.Json.JsonElement actual, System.Text.Json.JsonElement expected)
{
    switch (expected.ValueKind)
    {
        case System.Text.Json.JsonValueKind.True:
        case System.Text.Json.JsonValueKind.False:
            return actual.ValueKind == expected.ValueKind;

        case System.Text.Json.JsonValueKind.String:
            var expectedStr = expected.GetString();
            // Array field (e.g. groups): check if value is contained
            if (actual.ValueKind == System.Text.Json.JsonValueKind.Array)
                return actual.EnumerateArray().Any(item =>
                    item.ValueKind == System.Text.Json.JsonValueKind.String &&
                    item.GetString() == expectedStr);
            return actual.ValueKind == System.Text.Json.JsonValueKind.String &&
                   actual.GetString() == expectedStr;

        case System.Text.Json.JsonValueKind.Number:
            return actual.ValueKind == System.Text.Json.JsonValueKind.Number &&
                   actual.GetDouble() == expected.GetDouble();

        default:
            return false;
    }
}
