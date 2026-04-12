using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// MongoDB
var connectionString = builder.Configuration.GetConnectionString("MongoDB")!;
var databaseName = builder.Configuration["MongoDB:DatabaseName"]!;

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
builder.Services.AddSingleton<IMongoCollection<Contact>>(sp =>
    sp.GetRequiredService<IMongoDatabase>().GetCollection<Contact>("contacts"));

// CORS — allow the static frontend to call the API
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

app.MapGet("/health", async (IMongoDatabase db) =>
{
    try
    {
        await db.RunCommandAsync((Command<BsonDocument>)"{ping:1}");
        return Results.Ok(new { status = "ok", database = databaseName });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "fail", error = ex.Message },
            statusCode: 503);
    }
});

app.MapGet("/contacts", async (IMongoCollection<Contact> contacts, int page = 1, int pageSize = 20) =>
{
    var filter = FilterDefinition<Contact>.Empty;
    var total = await contacts.CountDocumentsAsync(filter);
    var items = await contacts.Find(filter)
        .SortBy(c => c.LastName)
        .Skip((page - 1) * pageSize)
        .Limit(pageSize)
        .ToListAsync();

    return Result<PagedResult<Contact>>.Ok(new PagedResult<Contact>(items, total, page, pageSize))
        .ToApiResult();
});

app.Run();
