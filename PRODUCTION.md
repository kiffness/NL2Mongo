# Production Readiness Tasks

## Security

- [ ] **Authentication** — add API key or OAuth to all endpoints. Right now anyone who can reach the API can query any tenant's data.
- [ ] **Tenant isolation** — validate that the authenticated caller is allowed to access the `X-Tenant` they're requesting. Currently the header is trusted blindly.
- [ ] **CORS** — lock down `AllowAnyOrigin()` to specific known frontend origins only.
- [ ] **HTTPS** — enforce TLS. Remove or redirect HTTP.
- [ ] **Description length limit** — cap the input length on `POST /segments/preview` to prevent oversized prompts being sent to Ollama.
- [ ] **Audit log** — record every query: who asked, which tenant, what description, what filter was generated, how many results came back, and when. Essential for compliance and debugging.

## Reliability

- [ ] **Ollama timeout** — the `HttpClient` for Ollama has no timeout configured. A slow or hung model will hold the request open indefinitely. Set a sensible timeout (e.g. 60s) and return a clear error to the user.
- [ ] **Retry logic** — add a retry policy (e.g. via Polly) for transient Ollama failures.
- [ ] **Ollama warm-up** — the model can take several seconds to load on first use after a restart. Consider a warm-up call on API startup so the first real user request isn't slow.
- [ ] **MongoDB authentication** — the connection string currently uses no credentials. Add username/password for production MongoDB.
- [ ] **Schema cache invalidation** — the cache expires after 10 minutes but there's no way to manually bust it if a tenant's schema changes. Add a cache invalidation endpoint or reduce TTL if schema changes are frequent.

## Observability

- [x] **Structured logging** — Serilog wired throughout the API (Console + Seq sinks). `ILogger` replaces `Console.WriteLine` in the Seed. Bootstrap logger captures startup failures. `app.UseSerilogRequestLogging()` provides structured HTTP access logs.
- [x] **Query logging** — every `POST /segments/preview` logs tenant, description, generated filter, match count, and total elapsed ms as structured properties queryable in Seq.
- [x] **Failed query tracking** — LLM failures, validation rejections, and zero-result queries each emit a `LogWarning` with a `Stage` property so they can be filtered and alerted on in Seq.
- [x] **Performance metrics** — `OllamaService` measures and logs `ElapsedMs` for every Ollama HTTP call. Zero-result and failure paths also carry elapsed time.

## Prompt hardening

- [ ] **Per-client example library** — after real users have been using the system for 1–2 weeks, review the query log and identify common phrasings and any failures. Build a client-specific set of few-shot examples to replace the generic hand-written ones in `OllamaService.cs`.
- [ ] **Per-tenant prompt config** — move the few-shot examples out of code and into a config store (e.g. a `prompts` collection in MongoDB per tenant) so they can be updated without a deployment.
- [ ] **Ambiguity detection** — if the model returns 0 results and the filter looks valid, consider returning a prompt to the user suggesting they rephrase rather than silently showing an empty table.

## Infrastructure

- [ ] **Containerise the API** — add a `Dockerfile` for `NL2Mongo.Api` so it can be deployed alongside MongoDB and Ollama via `docker compose`.
- [ ] **Environment config** — add `appsettings.Production.json` with production MongoDB connection string, Ollama URL, and any other environment-specific values. Remove hardcoded `localhost` URLs.
- [ ] **GPU driver documentation** — document the NVIDIA driver version and CUDA requirements for Ollama GPU passthrough. Someone setting this up on a new machine will need this.
- [ ] **Model pinning** — document which exact Ollama model version is in use (not just `llama3.1:8b` but the specific digest). Model updates can change output behaviour and affect accuracy.

## Testing

- [x] **Expand evaluation suite** — expanded from 16 → 49 cases. New coverage: negation (`$nin`/`$ne`), age range (`$gt`/`$lt`/`$between`), date queries (`$gte` on `createdAt`), multi-value OR (`$in`), and compound combinations of all of the above across both tenants. EvaluationSuite.json now supports `//` comments via `JsonCommentHandling.Skip`.
- [ ] **Per-client evaluation suites** — once real clients are onboarded, build a separate evaluation suite per tenant using their actual field names and values.
- [x] **Regression baseline** — `.github/workflows/evaluate.yml` runs `POST /segments/evaluate` on every push/PR to `main` (self-hosted runner). `scripts/check-accuracy.sh` fails the build if accuracy drops below `MIN_ACCURACY` (default 80%). Failed cases are printed with their generated query for debugging.
- [ ] **Load testing** — Ollama is single-threaded by default. Test what happens under concurrent requests and document the throughput limits.

## Operational

- [ ] **Runbook** — document how to restart the stack, pull a new model version, reseed a tenant, and clear the schema cache.
- [ ] **Model update process** — define a process for evaluating a new model version before switching: pull the new model, run the evaluation suite against it, compare accuracy, then cut over.
- [ ] **Monitoring and alerting** — set up alerts for Ollama errors, high response times, and 5xx rates on the API.
- [ ] **Backup** — ensure MongoDB data is backed up. The seed data is reproducible but real client data is not.
