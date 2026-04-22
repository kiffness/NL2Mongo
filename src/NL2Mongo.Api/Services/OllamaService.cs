using System.Diagnostics;
using System.Text.Json.Serialization;
using NL2Mongo.Api.Helpers;

namespace NL2Mongo.Api.Services;

public class OllamaService(HttpClient http, IConfiguration config, ILogger<OllamaService> logger)
{
    private readonly string _model = config["OllamaModel"] ?? "llama3.1:8b";

    public async Task<Result<string>> GenerateFilterAsync(string description, SchemaDescription schema)
    {
        var request = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = BuildSystemPrompt(schema) },
                new { role = "user",   content = description }
            },
            stream = false
        };

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/api/chat", request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ollama unreachable after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Result<string>.Fail(new Error($"Could not reach Ollama: {ex.Message}", ErrorType.Internal));
        }
        finally
        {
            sw.Stop();
        }

        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Ollama returned HTTP {StatusCode} in {ElapsedMs}ms",
                (int)response.StatusCode, sw.ElapsedMilliseconds);
            return Result<string>.Fail(new Error($"Ollama returned HTTP {(int)response.StatusCode}", ErrorType.Internal));
        }

        logger.LogInformation("Ollama responded in {ElapsedMs}ms for model {Model}",
            sw.ElapsedMilliseconds, _model);

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
        var text = body?.Message?.Content?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            logger.LogWarning("Ollama returned an empty response body");
            return Result<string>.Fail(new Error("Empty response from Ollama", ErrorType.Internal));
        }

        var json = ExtractJson(text);
        if (json is null)
        {
            logger.LogWarning("Ollama response contained no JSON object. RawResponse={RawResponse}", text);
            return Result<string>.Fail(new Error(
                $"Model did not return a JSON object. Raw response: {text}", ErrorType.Validation));
        }

        return Result<string>.Ok(json);
    }

    public async Task<Result<string>> GeneratePipelineAsync(string description, IReadOnlyList<(string Name, SchemaDescription Schema)> collections)
    {
        var request = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = BuildPipelineSystemPrompt(collections) },
                new { role = "user",   content = description }
            },
            stream = false
        };

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/api/chat", request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ollama unreachable after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Result<string>.Fail(new Error($"Could not reach Ollama: {ex.Message}", ErrorType.Internal));
        }
        finally
        {
            sw.Stop();
        }

        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Ollama returned HTTP {StatusCode} in {ElapsedMs}ms",
                (int)response.StatusCode, sw.ElapsedMilliseconds);
            return Result<string>.Fail(new Error($"Ollama returned HTTP {(int)response.StatusCode}", ErrorType.Internal));
        }

        logger.LogInformation("Ollama responded in {ElapsedMs}ms for model {Model}",
            sw.ElapsedMilliseconds, _model);

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>();
        var text = body?.Message?.Content?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            logger.LogWarning("Ollama returned an empty response body");
            return Result<string>.Fail(new Error("Empty response from Ollama", ErrorType.Internal));
        }

        var json = ExtractJsonArray(text);
        if (json is null)
        {
            logger.LogWarning("Ollama response contained no JSON array. RawResponse={RawResponse}", text);
            return Result<string>.Fail(new Error(
                $"Model did not return a JSON array. Raw response: {text}", ErrorType.Validation));
        }

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

    // Pull the first [ ... ] block out of whatever the model returns.
    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end   = text.LastIndexOf(']');
        if (start == -1 || end == -1 || end < start) return null;
        return text[start..(end + 1)];
    }

    private static string BuildSystemPrompt(SchemaDescription schema)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Today's date is {DateTime.UtcNow:yyyy-MM-dd}.");
        sb.AppendLine("You are a MongoDB query generator. Convert natural language audience descriptions into MongoDB filter documents.");
        sb.AppendLine();
        sb.AppendLine("Collection schema:");
        foreach (var field in schema.Fields)
        {
            var line = $"- {field.Name}: {field.Type}";
            if (field.SampleValues.Count > 0)
            {
                var label = field.IsExhaustive ? "values" : "example values";
                var suffix = field.IsExhaustive ? "" : "…";
                line += $" — {label}: {string.Join(", ", field.SampleValues)}{suffix}";
            }
            sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("Important: only use exact values that exist in the schema above. Do not invent values or use geographic regions (e.g. 'Europe') as country values — use specific country names instead.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY a valid JSON object. No explanation, no markdown, no code blocks.");
        sb.AppendLine("- Use standard MongoDB operators where needed: $gt, $lt, $gte, $lte, $in, $nin, $and, $or, $regex.");
        sb.AppendLine("- For array fields use: {\"fieldName\": \"Value\"} for a single value or {\"fieldName\": {\"$in\": [\"A\",\"B\"]}} for multiple.");
        sb.AppendLine("- For date comparisons use ISO 8601 strings, e.g. {\"createdAt\": {\"$gte\": \"2024-01-01T00:00:00Z\"}}. Never use ISODate().");
        sb.AppendLine("- For negation (\"not in\", \"excluding\", \"without\", \"who are not\"), use $nin for both array and scalar fields: {\"groups\": {\"$nin\": [\"VIP\"]}} or {\"occupation\": {\"$nin\": [\"Engineer\"]}}. Never use $ne or $not.");
        sb.AppendLine();

        // Compute anchors for date examples so the model sees concrete ISO strings
        // relative to today rather than placeholder text.
        var oneYearAgo  = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-ddT00:00:00Z");
        var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6).ToString("yyyy-MM-ddT00:00:00Z");

        sb.AppendLine("Examples:");

        sb.AppendLine("User: active contacts");
        sb.AppendLine("Assistant: {\"isActive\": true}");
        sb.AppendLine();

        sb.AppendLine("User: engineers over 30");
        sb.AppendLine("Assistant: {\"occupation\": \"Engineer\", \"age\": {\"$gt\": 30}}");
        sb.AppendLine();

        sb.AppendLine("User: contacts aged between 25 and 45");
        sb.AppendLine("Assistant: {\"age\": {\"$gte\": 25, \"$lte\": 45}}");
        sb.AppendLine();

        sb.AppendLine("User: VIP or Newsletter members who are active");
        sb.AppendLine("Assistant: {\"groups\": {\"$in\": [\"VIP\", \"Newsletter\"]}, \"isActive\": true}");
        sb.AppendLine();

        sb.AppendLine("User: nurses or teachers under 40 in the United Kingdom");
        sb.AppendLine("Assistant: {\"occupation\": {\"$in\": [\"Nurse\", \"Teacher\"]}, \"age\": {\"$lt\": 40}, \"country\": \"United Kingdom\"}");
        sb.AppendLine();

        sb.AppendLine("User: contacts not in the VIP group");
        sb.AppendLine("Assistant: {\"groups\": {\"$nin\": [\"VIP\"]}}");
        sb.AppendLine();

        sb.AppendLine("User: contacts who are not engineers");
        sb.AppendLine("Assistant: {\"occupation\": {\"$nin\": [\"Engineer\"]}}");
        sb.AppendLine();

        sb.AppendLine($"User: contacts who joined in the last year");
        sb.AppendLine($"Assistant: {{\"createdAt\": {{\"$gte\": \"{oneYearAgo}\"}}}}");
        sb.AppendLine();

        sb.AppendLine($"User: contacts added in the last 6 months");
        sb.AppendLine($"Assistant: {{\"createdAt\": {{\"$gte\": \"{sixMonthsAgo}\"}}}}");
        sb.AppendLine();

        sb.AppendLine("User: active contacts not in the Trial group added in the last year");
        sb.AppendLine($"Assistant: {{\"isActive\": true, \"groups\": {{\"$nin\": [\"Trial\"]}}, \"createdAt\": {{\"$gte\": \"{oneYearAgo}\"}}}}");

        sb.AppendLine("User: active engineers over 40");
        sb.AppendLine("Assistant: {\"isActive\": true, \"occupation\": \"Engineer\", \"age\": {\"$gt\": 40}}");
        sb.AppendLine();

        sb.AppendLine("User: active buyers with a budget between 400 and 700");
        sb.AppendLine("Assistant: {\"isActive\": true, \"propertyInterest\": \"Buying\", \"budget\": {\"$gte\": 400, \"$lte\": 700}}");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildPipelineSystemPrompt(IReadOnlyList<(string Name, SchemaDescription Schema)> collections)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Today's date is {DateTime.UtcNow:yyyy-MM-dd}.");
        sb.AppendLine("You are a MongoDB aggregation pipeline generator. Convert natural language questions into MongoDB aggregation pipelines.");
        sb.AppendLine();

        var primary = collections.Count > 0 ? collections[0].Name : "contacts";
        sb.AppendLine($"The pipeline runs on the '{primary}' collection.");

        if (collections.Count > 1)
        {
            var others = string.Join(", ", collections.Skip(1).Select(c => $"'{c.Name}'"));
            sb.AppendLine($"Other available collections you can join with $lookup: {others}.");
        }

        sb.AppendLine();
        sb.AppendLine("Collection schemas:");

        foreach (var (name, schema) in collections)
        {
            sb.AppendLine($"{name}:");
            foreach (var field in schema.Fields)
            {
                var line = $"  - {field.Name}: {field.Type}";
                if (field.SampleValues.Count > 0)
                {
                    var label  = field.IsExhaustive ? "values" : "example values";
                    var suffix = field.IsExhaustive ? "" : "…";
                    line += $" — {label}: {string.Join(", ", field.SampleValues)}{suffix}";
                }
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY a valid JSON array of stage documents. No explanation, no markdown, no code blocks.");
        sb.AppendLine("- Each element is a stage: {\"$stageName\": ...}");
        sb.AppendLine("- Allowed stages: $match, $group, $project, $sort, $limit, $skip, $unwind, $count, $addFields, $replaceRoot, $lookup.");
        sb.AppendLine("- $out and $merge are NOT allowed.");
        sb.AppendLine("- For grouping use $group with _id set to the field(s) to group by.");
        sb.AppendLine("- Accumulator operators: $sum, $avg, $min, $max, $push, $addToSet, $first, $last.");
        sb.AppendLine("- When referencing a field use \"$fieldName\" syntax.");
        sb.AppendLine("- For date comparisons use ISO 8601 strings, e.g. \"2024-01-01T00:00:00Z\".");
        sb.AppendLine("- IMPORTANT: Numeric comparisons ($gt, $lt, $gte, $lte) MUST use bare numbers, never quoted strings. Correct: {\"$gt\": 1}  Wrong: {\"$gt\": \"1\"}");
        sb.AppendLine("- If the pipeline could return many individual documents (no $group or $count), add a {\"$limit\": 200} stage at the end.");
        sb.AppendLine("- Each document also has an _id field (ObjectId) that can be used as a join key.");
        sb.AppendLine();

        if (collections.Count > 1)
        {
            sb.AppendLine("$lookup syntax for joining collections:");
            sb.AppendLine("{\"$lookup\": {\"from\": \"<otherCollection>\", \"localField\": \"<fieldInThisCollection>\", \"foreignField\": \"<fieldInOtherCollection>\", \"as\": \"<outputFieldName>\"}}");
            sb.AppendLine("After $lookup the joined field is an array of matched documents. Do NOT use $unwind + $group + $sum:1 to count joined items — that counts documents, not matches.");
            sb.AppendLine("To count how many joined items each document has, use $addFields with $size: {\"$addFields\": {\"joinedCount\": {\"$size\": \"$outputFieldName\"}}}");
            sb.AppendLine("Then filter with $match: {\"$match\": {\"joinedCount\": {\"$gt\": 2}}}");
            sb.AppendLine("Use $unwind only when you need to access individual fields of the joined documents, not to count them.");
            sb.AppendLine();
        }

        sb.AppendLine("Examples:");

        sb.AppendLine("User: count contacts by occupation");
        sb.AppendLine($"Assistant: [{{\"$group\": {{\"_id\": \"$occupation\", \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"count\": -1}}}}]");
        sb.AppendLine();

        sb.AppendLine("User: how many active vs inactive contacts are there");
        sb.AppendLine($"Assistant: [{{\"$group\": {{\"_id\": \"$isActive\", \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"_id\": -1}}}}]");
        sb.AppendLine();

        sb.AppendLine("User: average age by occupation");
        sb.AppendLine($"Assistant: [{{\"$group\": {{\"_id\": \"$occupation\", \"avgAge\": {{\"$avg\": \"$age\"}}}}}}, {{\"$sort\": {{\"avgAge\": -1}}}}]");
        sb.AppendLine();

        sb.AppendLine("User: top 5 cities by number of contacts");
        sb.AppendLine($"Assistant: [{{\"$group\": {{\"_id\": \"$city\", \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"count\": -1}}}}, {{\"$limit\": 5}}]");
        sb.AppendLine();

        sb.AppendLine("User: count of contacts per group");
        sb.AppendLine($"Assistant: [{{\"$unwind\": \"$groups\"}}, {{\"$group\": {{\"_id\": \"$groups\", \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"count\": -1}}}}]");
        sb.AppendLine();

        sb.AppendLine("User: total number of contacts");
        sb.AppendLine($"Assistant: [{{\"$count\": \"total\"}}]");
        sb.AppendLine();

        sb.AppendLine("User: average budget by property interest");
        sb.AppendLine($"Assistant: [{{\"$group\": {{\"_id\": \"$propertyInterest\", \"avgBudget\": {{\"$avg\": \"$budget\"}}, \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"avgBudget\": -1}}}}]");
        sb.AppendLine();

        var oneYearAgo = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-ddT00:00:00Z");
        sb.AppendLine("User: contacts added in the last year grouped by month");
        sb.AppendLine($"Assistant: [{{\"$match\": {{\"createdAt\": {{\"$gte\": \"{oneYearAgo}\"}}}}}}, {{\"$group\": {{\"_id\": {{\"year\": {{\"$year\": \"$createdAt\"}}, \"month\": {{\"$month\": \"$createdAt\"}}}}, \"count\": {{\"$sum\": 1}}}}}}, {{\"$sort\": {{\"_id.year\": 1, \"_id.month\": 1}}}}]");
        sb.AppendLine();

        if (collections.Count > 1)
        {
            var other = collections[1].Name;
            var foreignKey = $"{primary.TrimEnd('s')}Id";
            var joinedAs   = $"{other.TrimEnd('s')}Info";

            sb.AppendLine($"User: show {primary} with their {other} details");
            sb.AppendLine($"Assistant: [{{\"$lookup\": {{\"from\": \"{other}\", \"localField\": \"_id\", \"foreignField\": \"{foreignKey}\", \"as\": \"{joinedAs}\"}}}}, {{\"$unwind\": {{\"path\": \"${joinedAs}\", \"preserveNullAndEmptyArrays\": true}}}}, {{\"$limit\": 200}}]");
            sb.AppendLine();

            sb.AppendLine($"User: {primary} that have more than 1 {other.TrimEnd('s')}, show firstName lastName email");
            sb.AppendLine($"Assistant: [{{\"$lookup\": {{\"from\": \"{other}\", \"localField\": \"_id\", \"foreignField\": \"{foreignKey}\", \"as\": \"{joinedAs}\"}}}}, {{\"$addFields\": {{\"{other.TrimEnd('s')}Count\": {{\"$size\": \"${joinedAs}\"}}}}}}, {{\"$match\": {{\"{other.TrimEnd('s')}Count\": {{\"$gt\": 1}}}}}}, {{\"$project\": {{\"_id\": 0, \"firstName\": 1, \"lastName\": 1, \"email\": 1}}}}, {{\"$limit\": 200}}]");
            sb.AppendLine();

            sb.AppendLine($"User: count {primary} per {other.TrimEnd('s')}");
            sb.AppendLine($"Assistant: [{{\"$lookup\": {{\"from\": \"{other}\", \"localField\": \"_id\", \"foreignField\": \"{foreignKey}\", \"as\": \"{joinedAs}\"}}}}, {{\"$addFields\": {{\"{other.TrimEnd('s')}Count\": {{\"$size\": \"${joinedAs}\"}}}}}}, {{\"$sort\": {{\"{other.TrimEnd('s')}Count\": -1}}}}, {{\"$project\": {{\"_id\": 0, \"firstName\": 1, \"lastName\": 1, \"{other.TrimEnd('s')}Count\": 1}}}}, {{\"$limit\": 200}}]");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

record OllamaChatResponse(
    [property: JsonPropertyName("message")] OllamaMessage? Message);

record OllamaMessage(
    [property: JsonPropertyName("content")] string Content);
