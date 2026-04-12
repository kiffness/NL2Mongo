# NL2Mongo

A proof of concept that lets non-technical users describe a contact audience in plain English. The API translates that description into a MongoDB filter via a locally-hosted LLM, validates it, and executes it — no query syntax knowledge required.

**Hard constraint:** No data or LLM inference leaves the on-premise environment. All processing runs locally via [Ollama](https://ollama.com).

---

## How it works

```
User types: "active nurses over 40 in the VIP group"
        ↓
Schema introspection — samples the tenant's collection to discover fields, types, and enum values
        ↓
LLM prompt — schema-aware prompt sent to Llama 3.1 8B via Ollama
        ↓
Generated filter: { "isActive": true, "occupation": "Nurse", "age": { "$gt": 40 }, "groups": "VIP" }
        ↓
Query validation — rejects write operators, JS injection, malformed JSON
        ↓
MongoDB query — filter executed against the tenant's contacts collection
        ↓
Matched contacts returned to the frontend
```

### Multi-tenant

Each tenant is a separate MongoDB database on the same server. The tenant is identified by an `X-Tenant` request header. Schema introspection runs per-tenant and is cached for 10 minutes, so different tenants can have completely different contact schemas — no code changes required.

---

## Solution structure

```
NL2Mongo.sln
├── src/NL2Mongo.Api/       — .NET 9 Minimal API
│   ├── Program.cs          — endpoints and DI wiring
│   ├── OllamaService.cs    — LLM integration (Ollama /api/chat)
│   ├── SchemaInspector.cs  — dynamic schema discovery + caching
│   ├── QueryValidator.cs   — filter validation and normalisation
│   ├── Result.cs           — Result<T> Railway-Oriented error handling
│   ├── Contact.cs          — contact model + PagedResult<T>
│   ├── Segments.cs         — segment request/response types
│   ├── Evaluation.cs       — evaluation types
│   └── EvaluationSuite.json — accuracy test cases
├── src/NL2Mongo.Seed/      — console app to generate synthetic contacts
└── frontend/               — static Bootstrap 5 site (no build step)
```

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) with WSL2 backend
- A GPU is recommended for Ollama inference (tested on NVIDIA RTX 5070 Ti)

---

## Setup

### 1. Start MongoDB and Ollama

```bash
docker compose up -d
```

### 2. Pull the LLM model (first time only)

```bash
docker exec -it ollama ollama pull llama3.1:8b
```

Verify it's working:

```bash
docker exec -it ollama ollama run llama3.1:8b "say hello"
```

### 3. Seed the database

```bash
cd src/NL2Mongo.Seed
dotnet run
```

This generates two tenants with reproducible synthetic data (fixed random seed):

| Tenant | Collection | Records | Schema |
|--------|-----------|---------|--------|
| `nl2mongo` | contacts | 500 | General CRM — name, age, occupation, city, country, groups, isActive |
| `dunmowPropertyGroup` | contacts | 300 | Estate agent CRM — name, age, status, propertyInterest, budget, bedrooms, county, source, isActive |

Safe to rerun — clears and reseeds both tenants each time.

### 4. Run the API

```bash
cd src/NL2Mongo.Api
dotnet run
```

API listens on `http://localhost:5000`.

### 5. Open the frontend

Open `frontend/index.html` directly in your browser. No web server needed.

---

## API endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/tenants` | List available tenant databases |
| GET | `/health` | Ping MongoDB for the given tenant |
| GET | `/contacts` | Paginated contact list |
| POST | `/segments/preview` | Natural language → filter → matched contacts |
| POST | `/segments/evaluate` | Run the full evaluation suite and return accuracy metrics |

All endpoints except `/tenants` require an `X-Tenant` header with the database name.

### Example: segment preview

```http
POST /segments/preview
X-Tenant: nl2mongo
Content-Type: application/json

{ "description": "active nurses over 40 in the VIP group" }
```

```json
{
  "contacts": [...],
  "total": 8,
  "generatedQuery": "{ \"isActive\": true, \"occupation\": \"Nurse\", \"age\": { \"$gt\": 40 }, \"groups\": \"VIP\" }"
}
```

---

## Accuracy evaluation

```http
POST /segments/evaluate
```

Runs 16 test cases across both tenants and returns pass/fail per case with an overall accuracy percentage. The test suite is defined in `src/NL2Mongo.Api/EvaluationSuite.json`.

Each test case asserts:
- `allMustHave` — every returned contact must match these field/value pairs
- `noneCanHave` — no returned contact can match these field/value pairs
- `minCount` — at least N results must be returned

Current baseline: **100% (16/16)** on Llama 3.1 8B.

---

## Key design decisions

**No data leaves the environment** — Ollama runs in Docker on the local machine. The LLM never sees data from MongoDB; it only sees the schema description and the natural language query.

**Dynamic schema introspection** — Rather than hardcoding a schema, the API samples 50 documents from the tenant's collection on first request and builds the prompt dynamically. Low-cardinality fields (≤15 distinct values) are shown exhaustively so the model knows valid enum values. High-cardinality fields show 5 example values so the model understands the data format.

**Schema cache** — Introspection results are cached per tenant for 10 minutes to avoid sampling on every request.

**Query validation** — Generated filters are validated before touching MongoDB: must be valid JSON, must be a JSON object, and must not contain prohibited operators (`$set`, `$unset`, `$where`, `$function`, etc.). Key names and string values are also whitespace-normalised, and ISO 8601 date strings are coerced to BSON DateTime so date comparisons work correctly.

**Result<T>** — All service operations return `Result<T>` (Railway-Oriented Programming). Errors are mapped to HTTP responses at the endpoint boundary via `ToApiResult()`.

**Preview cap** — Segment preview results are capped at 200 contacts to avoid returning large result sets.
