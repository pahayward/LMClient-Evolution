using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("lmstudio", client =>
{
    var baseUrl = builder.Configuration["LMStudio:BaseUrl"] ?? "http://localhost:1234";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(120);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ── Document ────────────────────────────────────────────────────────────────
var docPath = Path.Combine(app.Environment.ContentRootPath, "Documents", "evolution.md");
var documentContent = await File.ReadAllTextAsync(docPath);

// ── MCP client → booking server ─────────────────────────────────────────────
var mcpProjectPath = Path.GetFullPath(
    Path.Combine(app.Environment.ContentRootPath, "..", "EvolutionBookingMcp"));

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["run", "--project", mcpProjectPath],
    Name = "EvolutionBookingMcp",
});

var mcpClient = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation { Name = "LMStudioChat", Version = "1.0" }
}, app.Services.GetRequiredService<ILoggerFactory>());

app.Lifetime.ApplicationStopping.Register(() =>
    mcpClient.DisposeAsync().AsTask().GetAwaiter().GetResult());

// Load tools from MCP server and build OpenAI-format tool definitions
var mcpTools = (await mcpClient.ListToolsAsync()).ToList();
var mcpToolMap = mcpTools.ToDictionary(t => t.Name);

var openAiToolDefs = mcpTools.Select(t =>
{
    // t.JsonSchema is a JsonElement containing the full JSON Schema for the tool
    var schemaNode = JsonNode.Parse(t.JsonSchema.GetRawText())!;
    return new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description ?? "",
            ["parameters"] = schemaNode
        }
    };
}).ToList();

app.Logger.LogInformation("Loaded {Count} booking tools from MCP server: {Names}",
    mcpTools.Count, string.Join(", ", mcpTools.Select(t => t.Name)));

// ── System prompt ────────────────────────────────────────────────────────────
var systemPrompt = $"""
    You are a helpful assistant with two roles:

    1. KNOWLEDGE ASSISTANT — You answer questions about the Theory of Evolution using the
       document provided below. Only use information from this document; if a question
       cannot be answered from it, say so clearly.

    2. BOOKING ASSISTANT — You can help users book a one-hour consultation slot with our
       evolution expert. Follow this exact flow when booking is requested:
       a. Ask: "Would you like to book a one-hour slot with our evolution expert?"
       b. Once confirmed, ask for their preferred date (yyyy-MM-dd) and time (HH:mm, 24-hour).
       c. Ask for their full name and email address.
       d. Call check_availability with the date and time.
       e. If available, call book_slot with all four details and confirm to the user.
       f. If not available, apologise and ask them to suggest a different time.

    Today's date is {DateTime.Today:yyyy-MM-dd}. Do not book slots in the past.

    When booking, collect ALL required information (date, time, name, email) before
    calling any tools. You may ask for them across multiple turns.

    --- DOCUMENT START ---
    {documentContent}
    --- DOCUMENT END ---
    """;

// ── Chat endpoint ────────────────────────────────────────────────────────────
app.MapPost("/api/chat", async (ChatRequest request, IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Message cannot be empty." });

    // Build the initial message array
    var messages = new JsonArray();
    messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

    if (request.History is { Count: > 0 })
        foreach (var h in request.History)
            messages.Add(new JsonObject { ["role"] = h.Role, ["content"] = h.Content });

    messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Message });

    var lmClient = httpClientFactory.CreateClient("lmstudio");

    // Agentic loop — continues while the model returns tool calls (max 6 iterations)
    for (int iteration = 0; iteration < 6; iteration++)
    {
        var payload = new JsonObject
        {
            ["model"] = "local-model",
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0.7,
            ["max_tokens"] = 1024,
            ["stream"] = false,
        };

        if (openAiToolDefs.Count > 0)
        {
            var toolsArray = new JsonArray();
            foreach (var def in openAiToolDefs)
                toolsArray.Add(def.DeepClone());
            payload["tools"] = toolsArray;
            payload["tool_choice"] = "auto";
        }

        HttpResponseMessage httpResponse;
        try
        {
            using var body = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            httpResponse = await lmClient.PostAsync("/v1/chat/completions", body);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"Could not connect to LM Studio. Is it running? Error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Results.Problem("Request to LM Studio timed out.");
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errBody = await httpResponse.Content.ReadAsStringAsync();
            return Results.Problem($"LM Studio returned {(int)httpResponse.StatusCode}: {errBody}");
        }

        using var doc = JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync());
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

        // No tool calls — return the assistant's text reply
        if (finishReason != "tool_calls" || !message.TryGetProperty("tool_calls", out var toolCallsEl))
        {
            var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : "(empty response)";
            return Results.Ok(new { reply = text });
        }

        // ── Tool call handling ────────────────────────────────────────────────
        // 1. Add the assistant message (with tool_calls) to the conversation
        var contentEl = message.TryGetProperty("content", out var cont) && cont.ValueKind == JsonValueKind.String
            ? (JsonNode?)JsonValue.Create(cont.GetString())
            : null;

        var assistantMsg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = contentEl,
            ["tool_calls"] = JsonNode.Parse(toolCallsEl.GetRawText())
        };
        messages.Add(assistantMsg);

        // 2. Execute each tool call via the MCP booking server
        foreach (var toolCallEl in toolCallsEl.EnumerateArray())
        {
            var toolCallId = toolCallEl.GetProperty("id").GetString()!;
            var toolName = toolCallEl.GetProperty("function").GetProperty("name").GetString()!;
            var argsJson = toolCallEl.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";

            string toolResult;
            if (mcpToolMap.TryGetValue(toolName, out var tool))
            {
                try
                {
                    // Parse arguments — MCP tools expect string values for simple params
                    var argsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)
                        ?? [];
                    var args = argsDict.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object)(kvp.Value.ValueKind == JsonValueKind.String
                            ? kvp.Value.GetString()!
                            : kvp.Value.GetRawText()));

                    var callResult = await tool.CallAsync(args);

                    toolResult = string.Join("\n", callResult.Content
                        .OfType<TextContentBlock>()
                        .Select(b => b.Text));

                    if (string.IsNullOrWhiteSpace(toolResult))
                        toolResult = "(tool returned no text)";
                }
                catch (Exception ex)
                {
                    toolResult = $"Tool error: {ex.Message}";
                }
            }
            else
            {
                toolResult = $"Unknown tool '{toolName}'.";
            }

            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = toolCallId,
                ["content"] = toolResult
            });
        }

        // Loop continues — call LM Studio again with the tool results appended
    }

    return Results.Problem("The assistant made too many tool calls without producing a final response.");
});

app.Run();

// ── DTOs ─────────────────────────────────────────────────────────────────────
record ChatRequest(
    string Message,
    [property: JsonPropertyName("history")] List<HistoryMessage>? History
);

record HistoryMessage(string Role, string Content);
