# Milestone 3 — LLM Integration

## Goal
Implement `POST /segments/preview` — takes a plain English audience description and a tenant identifier, introspects that tenant's schema, generates a MongoDB filter via Ollama, validates it, executes it, and returns matched contacts + the raw generated query.

---

## File 1 — `src/NL2Mongo.Api/Segments.cs` (new file)

```csharp
public record SegmentRequest(string Description);

public record SegmentPreview(
    IReadOnlyList<Contact> Contacts,
    int Total,
    string GeneratedQuery);
```

---

## File 2 — `src/NL2Mongo.Api/QueryValidator.cs` (new file)

```csharp
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
```

---

## File 3 — `src/NL2Mongo.Api/SchemaInspector.cs` (new file)

```csharp
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
            // Only include sample values if low-cardinality (enum-like)
            var samples = distinct.Count <= 15 ? (IReadOnlyList<string>)distinct : [];
            return new FieldDescriptor(name, fieldTypes[name], samples);
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
```

---

## File 4 — `src/NL2Mongo.Api/OllamaService.cs` (new file)

```csharp
using System.Text.Json.Serialization;

public class OllamaService(HttpClient http)
{
    private const string Model = "llama3.1:8b";

    public async Task<Result<string>> GenerateFilterAsync(string description, SchemaDescription schema)
    {
        var request = new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt(schema) },
                new { role = "user",   content = description }
            },
            stream = false
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/api/chat", request);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(new Error($"Could not reach Ollama: {ex.Message}", ErrorType.Internal));
        }

        if (!response.IsSuccessStatusCode)
            return Result<string>.Fail(new Error($"Ollama returned HTTP {(int)response.StatusCode}", ErrorType.Internal));

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
        var text = body?.Message?.Content?.Trim();

        if (string.IsNullOrEmpty(text))
            return Result<string>.Fail(new Error("Empty response from Ollama", ErrorType.Internal));

        var json = ExtractJson(text);
        if (json is null)
            return Result<string>.Fail(new Error(
                $"Model did not return a JSON object. Raw response: {text}", ErrorType.Validation));

        return Result<string>.Ok(json);
    }

    // Pull the first { ... } block out of whatever the model returns.
    // The model sometimes wraps its answer in prose or markdown fences.
    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start == -1 || end == -1 || end < start) return null;
        return text[start..(end + 1)];
    }

    private static string BuildSystemPrompt(SchemaDescription schema)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a MongoDB query generator. Convert natural language audience descriptions into MongoDB filter documents.");
        sb.AppendLine();
        sb.AppendLine("Collection schema:");
        foreach (var field in schema.Fields)
        {
            var line = $"- {field.Name}: {field.Type}";
            if (field.SampleValues.Count > 0)
                line += $" — values: {string.Join(", ", field.SampleValues)}";
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY a valid JSON object. No explanation, no markdown, no code blocks.");
        sb.AppendLine("- Use standard MongoDB operators where needed: $gt, $lt, $gte, $lte, $in, $nin, $and, $or, $regex.");
        sb.AppendLine("- For array fields use: {\"fieldName\": \"Value\"} for a single value or {\"fieldName\": {\"$in\": [\"A\",\"B\"]}} for multiple.");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("User: active contacts");
        sb.AppendLine("Assistant: {\"isActive\": true}");
        sb.AppendLine();
        sb.AppendLine("User: engineers over 30");
        sb.AppendLine("Assistant: {\"occupation\": \"Engineer\", \"age\": {\"$gt\": 30}}");
        sb.AppendLine();
        sb.AppendLine("User: VIP or Newsletter members who are active");
        sb.AppendLine("Assistant: {\"groups\": {\"$in\": [\"VIP\", \"Newsletter\"]}, \"isActive\": true}");
        sb.AppendLine();
        sb.AppendLine("User: nurses or teachers under 40 in the United Kingdom");
        sb.AppendLine("Assistant: {\"occupation\": {\"$in\": [\"Nurse\", \"Teacher\"]}, \"age\": {\"$lt\": 40}, \"country\": \"United Kingdom\"}");
        return sb.ToString();
    }
}

record OllamaChatResponse(
    [property: JsonPropertyName("message")] OllamaMessage? Message);

record OllamaMessage(
    [property: JsonPropertyName("content")] string Content);
```

---

## File 5 — `src/NL2Mongo.Api/Program.cs` (full replacement)

```csharp
using MongoDB.Bson;
using MongoDB.Driver;

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

// ── Helpers ──────────────────────────────────────────────────────────────────

// Resolve the tenant database from the X-Tenant request header
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

// Find the contacts collection regardless of casing (Contacts vs contacts)
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
```

---

## File 6 — `frontend/index.html` (full replacement)

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>NL2Mongo — Contact Segments</title>
  <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" />
  <style>
    body { background: #f8f9fa; }
    .badge-group { font-size: 0.75rem; }
    #query-display { font-size: 0.85rem; word-break: break-all; }
  </style>
</head>
<body>
  <div class="container py-4">
    <h1 class="mb-1">Contact Segments</h1>
    <p class="text-muted mb-4">Describe an audience in plain English to preview a segment, or browse all contacts below.</p>

    <!-- Tenant + Segment Preview -->
    <div class="card shadow-sm mb-4">
      <div class="card-body">
        <div class="row g-2 mb-3">
          <div class="col-md-3">
            <label class="form-label fw-semibold">Tenant (X-Tenant)</label>
            <input id="tenant-input" type="text" class="form-control" value="nl2mongo" />
          </div>
        </div>
        <label class="form-label fw-semibold">Audience description</label>
        <div class="input-group">
          <textarea id="description-input" class="form-control" rows="2"
            placeholder="e.g. active engineers over 40 in the VIP group"></textarea>
          <button id="preview-btn" class="btn btn-primary px-4" type="button">Preview</button>
        </div>

        <!-- Results panel (hidden until a preview runs) -->
        <div id="preview-results" class="mt-3" style="display:none">
          <div class="d-flex align-items-center justify-content-between mb-2">
            <span id="preview-count" class="fw-semibold"></span>
            <button id="clear-btn" class="btn btn-sm btn-outline-secondary">Clear preview</button>
          </div>
          <div class="mb-2">
            <span class="text-muted small">Generated query:</span>
            <pre id="query-display" class="bg-light border rounded p-2 mt-1 mb-0"></pre>
          </div>
          <div class="table-responsive">
            <table class="table table-hover align-middle bg-white rounded mb-0">
              <thead class="table-light">
                <tr>
                  <th>Name</th><th>Email</th><th>Age</th>
                  <th>Occupation</th><th>Location</th><th>Groups</th><th>Active</th>
                </tr>
              </thead>
              <tbody id="preview-body"></tbody>
            </table>
          </div>
        </div>
      </div>
    </div>

    <!-- All contacts -->
    <h5 class="mb-2">All contacts</h5>
    <div id="status" class="text-muted small mb-2">Loading…</div>
    <div class="table-responsive">
      <table class="table table-hover align-middle bg-white shadow-sm rounded">
        <thead class="table-light">
          <tr>
            <th>Name</th><th>Email</th><th>Age</th>
            <th>Occupation</th><th>Location</th><th>Groups</th><th>Active</th>
          </tr>
        </thead>
        <tbody id="contacts-body">
          <tr><td colspan="7" class="text-center text-muted py-4">Loading…</td></tr>
        </tbody>
      </table>
    </div>
    <nav aria-label="Contacts pagination">
      <ul class="pagination justify-content-center" id="pagination"></ul>
    </nav>
  </div>

  <script>
    const API = 'http://localhost:5000';
    const PAGE_SIZE = 20;
    let currentPage = 1;

    function tenant() {
      return document.getElementById('tenant-input').value.trim() || 'nl2mongo';
    }

    function headers(extra = {}) {
      return { 'X-Tenant': tenant(), ...extra };
    }

    // ── All contacts ──────────────────────────────────────────────────────────

    async function loadContacts(page) {
      currentPage = page;
      const tbody = document.getElementById('contacts-body');
      tbody.innerHTML = '<tr><td colspan="7" class="text-center text-muted py-4">Loading…</td></tr>';

      try {
        const res = await fetch(`${API}/contacts?page=${page}&pageSize=${PAGE_SIZE}`, { headers: headers() });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        renderRows(tbody, data.items);
        renderPagination(data.total, data.page, data.pageSize);
        document.getElementById('status').textContent =
          `Showing ${(page - 1) * PAGE_SIZE + 1}–${Math.min(page * PAGE_SIZE, data.total)} of ${data.total} contacts`;
      } catch (err) {
        tbody.innerHTML = `<tr><td colspan="7" class="text-center text-danger py-4">Failed to load: ${err.message}</td></tr>`;
        document.getElementById('status').textContent = '';
      }
    }

    // ── Segment preview ───────────────────────────────────────────────────────

    document.getElementById('preview-btn').addEventListener('click', async () => {
      const description = document.getElementById('description-input').value.trim();
      if (!description) return;

      const btn = document.getElementById('preview-btn');
      btn.disabled = true;
      btn.textContent = 'Thinking…';

      const panel = document.getElementById('preview-results');
      panel.style.display = 'none';

      try {
        const res = await fetch(`${API}/segments/preview`, {
          method: 'POST',
          headers: headers({ 'Content-Type': 'application/json' }),
          body: JSON.stringify({ description })
        });

        const data = await res.json();

        if (!res.ok) {
          alert(data.error ?? `Error ${res.status}`);
          return;
        }

        document.getElementById('preview-count').textContent =
          `${data.total} contact${data.total !== 1 ? 's' : ''} matched`;
        document.getElementById('query-display').textContent =
          JSON.stringify(JSON.parse(data.generatedQuery), null, 2);
        renderRows(document.getElementById('preview-body'), data.contacts);
        panel.style.display = 'block';

      } catch (err) {
        alert(`Request failed: ${err.message}`);
      } finally {
        btn.disabled = false;
        btn.textContent = 'Preview';
      }
    });

    document.getElementById('clear-btn').addEventListener('click', () => {
      document.getElementById('preview-results').style.display = 'none';
      document.getElementById('description-input').value = '';
    });

    // ── Shared rendering ──────────────────────────────────────────────────────

    function renderRows(tbody, contacts) {
      if (!contacts.length) {
        tbody.innerHTML = '<tr><td colspan="7" class="text-center text-muted py-4">No contacts found.</td></tr>';
        return;
      }
      tbody.innerHTML = contacts.map(c => `
        <tr>
          <td>${esc(c.firstName)} ${esc(c.lastName)}</td>
          <td class="text-muted">${esc(c.email)}</td>
          <td>${c.age}</td>
          <td>${esc(c.occupation)}</td>
          <td>${esc(c.city)}, ${esc(c.country)}</td>
          <td>${(c.groups ?? []).map(g => `<span class="badge bg-secondary badge-group me-1">${esc(g)}</span>`).join('')}</td>
          <td>${c.isActive
            ? '<span class="badge bg-success">Yes</span>'
            : '<span class="badge bg-light text-secondary border">No</span>'}</td>
        </tr>`).join('');
    }

    function renderPagination(total, page, pageSize) {
      const totalPages = Math.ceil(total / pageSize);
      const ul = document.getElementById('pagination');
      ul.innerHTML = '';

      const addItem = (label, targetPage, disabled = false, active = false) => {
        ul.insertAdjacentHTML('beforeend', `
          <li class="page-item ${disabled ? 'disabled' : ''} ${active ? 'active' : ''}">
            <a class="page-link" href="#" data-page="${targetPage}">${label}</a>
          </li>`);
      };

      addItem('&laquo;', page - 1, page === 1);
      const range = visiblePages(page, totalPages);
      let prev = null;
      for (const p of range) {
        if (prev !== null && p - prev > 1) addItem('…', -1, true);
        addItem(p, p, false, p === page);
        prev = p;
      }
      addItem('&raquo;', page + 1, page === totalPages);

      ul.querySelectorAll('a[data-page]').forEach(a => {
        a.addEventListener('click', e => {
          e.preventDefault();
          const p = parseInt(a.dataset.page);
          if (p > 0 && p <= totalPages) loadContacts(p);
        });
      });
    }

    function visiblePages(current, total) {
      const delta = 2;
      const pages = new Set([1, total]);
      for (let i = current - delta; i <= current + delta; i++) {
        if (i > 0 && i <= total) pages.add(i);
      }
      return [...pages].sort((a, b) => a - b);
    }

    function esc(str) {
      return String(str ?? '')
        .replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    loadContacts(1);
  </script>
</body>
</html>
```

---

## Order to write the files

1. `src/NL2Mongo.Api/Segments.cs`
2. `src/NL2Mongo.Api/QueryValidator.cs`
3. `src/NL2Mongo.Api/SchemaInspector.cs`
4. `src/NL2Mongo.Api/OllamaService.cs`
5. `src/NL2Mongo.Api/Program.cs` — full replacement
6. `frontend/index.html` — full replacement
