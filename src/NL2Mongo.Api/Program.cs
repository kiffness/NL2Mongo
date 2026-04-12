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

// ── Endpoints ─────────────────────────────────────────────────────────────────

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
    var contacts = db!.GetCollection<Contact>(collectionName);

    var total = await contacts.CountDocumentsAsync(FilterDefinition<Contact>.Empty);
    var items = await contacts.Find(FilterDefinition<Contact>.Empty)
        .SortBy(c => c.LastName)
        .Skip((page - 1) * pageSize)
        .Limit(pageSize)
        .ToListAsync();

    return Result<PagedResult<Contact>>.Ok(new PagedResult<Contact>(items, total, page, pageSize))
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
    var schema = await inspector.InspectAsync(db!, collectionName);

    var filterResult = await ollama.GenerateFilterAsync(req.Description, schema);
    if (!filterResult.IsSuccess) return filterResult.ToApiResult();

    var validationResult = QueryValidator.Validate(filterResult.Value!);
    if (!validationResult.IsSuccess) return validationResult.ToApiResult();

    var contacts = db!.GetCollection<Contact>(collectionName);
    var matched  = await contacts
        .Find(new BsonDocumentFilterDefinition<Contact>(validationResult.Value!))
        .Limit(200)
        .ToListAsync();

    return Result<SegmentPreview>.Ok(
        new SegmentPreview(matched, matched.Count, filterResult.Value!))
        .ToApiResult();
});

app.Run();
