using System.Text;
using System.Text.Json;

/// <summary>
/// Handles Qwen2.5-Coder model responses (LM Studio compatible).
/// Converts OpenAI-compatible chat completion format with tool_calls array
/// to Anthropic SSE format with tool_use blocks.
/// </summary>
public class Qwen2_5ResponseHandler : ILocalLLMResponseHandler
{
    public async Task HandleResponseAsync(
        HttpContext context,
        HttpResponseMessage lmResp,
        string targetModel,
        ILogger logger)
    {
        logger.LogInformation("🔄 Qwen2_5ResponseHandler: Processing response");

        await using var stream = await lmResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Read entire response (Qwen returns full JSON, not streaming)
        var responseBody = await reader.ReadToEndAsync();
        logger.LogInformation("Qwen response body size: {Bytes} bytes", responseBody.Length);
        logger.LogInformation("Raw Qwen response: {Response}",
            responseBody.Length > 1000 ? responseBody[..1000] + "..." : responseBody);

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            logger.LogInformation("✓ Parsed Qwen JSON response");

            // Extract message content and tool_calls
            string? assistantContent = null;
            List<JsonElement> toolCalls = new();

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var choicesArray = choices.EnumerateArray().ToList();
                logger.LogInformation("Qwen response has {Count} choices", choicesArray.Count);

                if (choicesArray.Count > 0)
                {
                    var firstChoice = choicesArray[0];
                    if (firstChoice.TryGetProperty("message", out var message))
                    {
                        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            assistantContent = content.GetString() ?? "";
                            logger.LogInformation("Extracted assistant content: {Length} chars", assistantContent.Length);
                        }

                        // Extract tool_calls if present
                        if (message.TryGetProperty("tool_calls", out var toolCallsArray) && toolCallsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in toolCallsArray.EnumerateArray())
                            {
                                toolCalls.Add(tc);
                            }
                            logger.LogInformation("✓ Extracted {Count} tool_calls from Qwen response", toolCalls.Count);
                        }
                    }
                }
            }

            // Build Anthropic SSE response
            var msgId = $"msg_{Guid.NewGuid():N}";
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream; charset=utf-8";

            logger.LogInformation("Setting up SSE response with message ID: {MsgId}", msgId);

            // Write message_start event
            var startEvent = $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"{msgId}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"{targetModel}\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}\n\n";
            await context.Response.WriteAsync(startEvent);
            await context.Response.Body.FlushAsync();

            int contentBlockIndex = 0;

            // If there's text content, send it first
            if (!string.IsNullOrEmpty(assistantContent))
            {
                logger.LogInformation("Sending text content block: {Length} chars", assistantContent.Length);

                // Build proper SSE content_block_start
                using var textStartMs = new MemoryStream();
                using (var textStartWriter = new Utf8JsonWriter(textStartMs))
                {
                    textStartWriter.WriteStartObject();
                    textStartWriter.WriteString("type", "content_block_start");
                    textStartWriter.WriteNumber("index", 0);
                    textStartWriter.WritePropertyName("content_block");
                    textStartWriter.WriteStartObject();
                    textStartWriter.WriteString("type", "text");
                    textStartWriter.WriteString("text", "");
                    textStartWriter.WriteEndObject();
                    textStartWriter.WriteEndObject();
                }
                var textStartData = Encoding.UTF8.GetString(textStartMs.ToArray());
                await context.Response.WriteAsync($"event: content_block_start\ndata: {textStartData}\n\n");
                await context.Response.Body.FlushAsync();

                // Build proper SSE content_block_delta
                using var textDeltaMs = new MemoryStream();
                using (var textDeltaWriter = new Utf8JsonWriter(textDeltaMs))
                {
                    textDeltaWriter.WriteStartObject();
                    textDeltaWriter.WriteString("type", "content_block_delta");
                    textDeltaWriter.WriteNumber("index", 0);
                    textDeltaWriter.WritePropertyName("delta");
                    textDeltaWriter.WriteStartObject();
                    textDeltaWriter.WriteString("type", "text_delta");
                    textDeltaWriter.WriteString("text", assistantContent);
                    textDeltaWriter.WriteEndObject();
                    textDeltaWriter.WriteEndObject();
                }
                var textDeltaData = Encoding.UTF8.GetString(textDeltaMs.ToArray());
                await context.Response.WriteAsync($"event: content_block_delta\ndata: {textDeltaData}\n\n");
                await context.Response.Body.FlushAsync();

                await context.Response.WriteAsync("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                await context.Response.Body.FlushAsync();

                contentBlockIndex++;
            }

            // Convert and send each tool_call as a tool_use block
            foreach (var toolCall in toolCalls)
            {
                try
                {
                    logger.LogInformation("Processing tool_call #{Index}", contentBlockIndex);

                    // Extract tool call structure
                    string? toolName = null;
                    string? toolId = null;
                    JsonElement? toolFunction = null;

                    if (toolCall.TryGetProperty("id", out var idElem))
                        toolId = idElem.GetString() ?? $"toolu_{Guid.NewGuid():N}";

                    if (toolCall.TryGetProperty("function", out var func))
                    {
                        toolFunction = func;
                        if (func.TryGetProperty("name", out var nameElem))
                            toolName = nameElem.GetString();
                    }

                    if (toolName is null || toolFunction is null)
                    {
                        logger.LogWarning("⚠️ Tool call missing name or function field");
                        continue;
                    }

                    logger.LogInformation("✓ Tool call: name={ToolName}, id={ToolId}", toolName, toolId ?? "auto");

                    // Extract arguments as raw JSON string (avoids JsonDocument disposal issues)
                    string inputJson = "{}";
                    if (toolFunction.Value.TryGetProperty("arguments", out var argsElem))
                    {
                        if (argsElem.ValueKind == JsonValueKind.String)
                        {
                            var argsStr = argsElem.GetString() ?? "{}";
                            // Validate it's JSON; if not, wrap as raw string value
                            try
                            {
                                JsonDocument.Parse(argsStr).Dispose();
                                inputJson = argsStr;
                                logger.LogInformation("Arguments from JSON string: {Len} chars", argsStr.Length);
                            }
                            catch
                            {
                                inputJson = JsonSerializer.Serialize(new { raw = argsStr });
                                logger.LogInformation("Arguments is raw string, not JSON");
                            }
                        }
                        else if (argsElem.ValueKind == JsonValueKind.Object)
                        {
                            inputJson = argsElem.GetRawText();
                            logger.LogInformation("Arguments from object: {Len} chars", inputJson.Length);
                        }
                    }

                    var resolvedId = toolId ?? $"toolu_{Guid.NewGuid():N}";

                    logger.LogInformation("✓ Converted to tool_use block: {Name}({InputJson})", toolName, inputJson);

                    // content_block_start: tool_use header with empty input (Anthropic SSE format)
                    using var startMs = new MemoryStream();
                    using (var startWriter = new Utf8JsonWriter(startMs))
                    {
                        startWriter.WriteStartObject();
                        startWriter.WriteString("type", "content_block_start");
                        startWriter.WriteNumber("index", contentBlockIndex);
                        startWriter.WritePropertyName("content_block");
                        startWriter.WriteStartObject();
                        startWriter.WriteString("type", "tool_use");
                        startWriter.WriteString("id", resolvedId);
                        startWriter.WriteString("name", toolName);
                        startWriter.WritePropertyName("input");
                        startWriter.WriteStartObject(); // empty input — filled via delta below
                        startWriter.WriteEndObject();
                        startWriter.WriteEndObject();
                        startWriter.WriteEndObject();
                    }
                    await context.Response.WriteAsync($"event: content_block_start\ndata: {Encoding.UTF8.GetString(startMs.ToArray())}\n\n");
                    await context.Response.Body.FlushAsync();

                    // content_block_delta: stream the input JSON via input_json_delta
                    var escapedInputJson = JsonSerializer.Serialize(inputJson);
                    await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":{contentBlockIndex},\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{escapedInputJson}}}}}\n\n");
                    await context.Response.Body.FlushAsync();

                    await context.Response.WriteAsync($"event: content_block_stop\ndata: {{\"type\":\"content_block_stop\",\"index\":{contentBlockIndex}}}\n\n");
                    await context.Response.Body.FlushAsync();

                    contentBlockIndex++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "✗ Failed to process tool_call");
                }
            }

            // Send final SSE events — stop_reason must be "tool_use" when tool calls were made
            var stopReason = toolCalls.Count > 0 ? "tool_use" : "end_turn";
            logger.LogInformation("Sending final SSE events, stop_reason={StopReason}", stopReason);
            await context.Response.WriteAsync($"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{stopReason}\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":0}}}}\n\n");
            await context.Response.WriteAsync("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            await context.Response.Body.FlushAsync();

            logger.LogInformation("✓ Qwen2_5ResponseHandler: Response sent successfully ({ContentBlocks} blocks)", contentBlockIndex + 1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "✗ Qwen2_5ResponseHandler: Failed to process response");
            throw;
        }
    }
}
