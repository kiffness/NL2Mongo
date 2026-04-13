using System.Text.Json.Serialization;
using NL2Mongo.Api.Helpers;

namespace NL2Mongo.Api.Services;

public class OllamaService(HttpClient http, IConfiguration config)
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
