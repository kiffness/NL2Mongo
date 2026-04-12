using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// MongoDB
var connectionString = builder.Configuration.GetConnectionString("MongoDB")!;
var databaseName = builder.Configuration["MongoDB:DatabaseName"]!;

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

var app = builder.Build();

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

app.MapGet("/contacts", () => Results.StatusCode(501));

app.Run();
