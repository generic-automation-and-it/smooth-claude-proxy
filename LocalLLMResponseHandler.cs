using System.Text;
using System.Text.Json;

/// <summary>
/// Handles converting local LLM responses to Anthropic SSE format.
/// Implement this interface for each model type to handle model-specific response processing.
/// </summary>
public interface ILocalLLMResponseHandler
{
    /// <summary>
    /// Process a response from the local LLM and stream it as Anthropic-compatible SSE to the client.
    /// </summary>
    Task HandleResponseAsync(
        HttpContext context,
        HttpResponseMessage lmResp,
        string targetModel,
        ILogger logger);
}

/// <summary>
/// Handles Liquid model responses (LM Studio compatible).
/// Processes SSE streams with tool call token detection and converts to Anthropic format.
/// Uses LiquidToolMapper to translate Liquid tool calls to Claude Code tool format.
/// </summary>
public class LiquidResponseHandler : ILocalLLMResponseHandler
{
    private readonly LiquidToolMapper _toolMapper;

    public LiquidResponseHandler(LiquidToolMapper toolMapper)
    {
        _toolMapper = toolMapper;
    }

    public async Task HandleResponseAsync(
        HttpContext context,
        HttpResponseMessage lmResp,
        string targetModel,
        ILogger logger)
    {
        await using var stream = await lmResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Check if response is SSE or plain JSON BEFORE writing any response headers
        var firstLine = await reader.ReadLineAsync();
        var isSSE = firstLine?.StartsWith("event:") == true || firstLine?.StartsWith("data:") == true;

        logger.LogInformation("Response format: {Format}", isSSE ? "SSE" : "JSON");

        // Now set response headers (only once, before any WriteAsync)
        var msgId = $"msg_{Guid.NewGuid():N}";
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";

        logger.LogInformation("Setting up SSE response with message ID: {MsgId}", msgId);

        if (!isSSE && firstLine is not null)
        {
            logger.LogInformation("Handling JSON response (non-streaming)");
            // Handle plain JSON response (non-streaming)
            var fullResponse = firstLine;
            string? jsonLine;
            while ((jsonLine = await reader.ReadLineAsync()) is not null)
                fullResponse += jsonLine;

            logger.LogInformation("JSON response body size: {Bytes}", fullResponse.Length);
            logger.LogInformation("Raw JSON response: {Response}", fullResponse.Length > 2000 ? fullResponse[..2000] + "..." : fullResponse);

            using var doc = JsonDocument.Parse(fullResponse);
            var root = doc.RootElement;

            // Write start event for JSON response
            var jsonStartEvent = $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"{msgId}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}\n\n";
            await context.Response.WriteAsync(jsonStartEvent);
            await context.Response.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            await context.Response.Body.FlushAsync();

            // Pass through all content blocks (text, tool_use, etc.)
            var blockCount = 0;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type))
                    {
                        var blockType = type.GetString() ?? "";
                        blockCount++;
                        logger.LogInformation("Processing content block #{Count}, type: {Type}", blockCount, blockType);

                        if (blockType == "text" && block.TryGetProperty("text", out var text))
                        {
                            var textContent = text.GetString() ?? "";

                            // First try to extract tool calls in Liquid JSON format: <|tool_call_start|>{...}<|tool_call_end|>
                            var toolCallRegex = new System.Text.RegularExpressions.Regex(@"<\|tool_call_start\|>(.*?)<\|tool_call_end\|>", System.Text.RegularExpressions.RegexOptions.Singleline);
                            var toolMatch = toolCallRegex.Match(textContent);
                            if (toolMatch.Success)
                            {
                                var toolCallJson = toolMatch.Groups[1].Value.Trim();
                                logger.LogInformation("Extracted Liquid tool call (JSON format): {ToolCall}", toolCallJson);

                                try
                                {
                                    using var toolDoc = JsonDocument.Parse(toolCallJson);
                                    var toolDef = toolDoc.RootElement;

                                    // Convert Liquid format to tool_use block
                                    using var ms = new MemoryStream();
                                    using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                                    {
                                        w.WriteStartObject();
                                        w.WriteString("type", "tool_use");
                                        w.WriteString("id", $"toolu_{Guid.NewGuid():N}");

                                        // Get tool name
                                        var toolName = "Bash";
                                        if (toolDef.TryGetProperty("name", out var nameElem))
                                            toolName = nameElem.GetString() ?? "Bash";

                                        w.WriteString("name", toolName);
                                        w.WritePropertyName("input");

                                        // Get arguments/command
                                        w.WriteStartObject();
                                        if (toolDef.TryGetProperty("arguments", out var argsElem))
                                        {
                                            foreach (var arg in argsElem.EnumerateObject())
                                                arg.Value.WriteTo(w);
                                        }
                                        w.WriteEndObject();
                                        w.WriteEndObject();
                                    }

                                    var toolUseJson = System.Text.Json.JsonDocument.Parse(ms.ToArray()).RootElement;
                                    var escaped = System.Text.Json.JsonSerializer.Serialize(toolUseJson)[1..^1];
                                    await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"tool_use\",\"json\":\"{escaped}\"}}}}\n\n");
                                    await context.Response.Body.FlushAsync();
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Failed to parse Liquid tool call JSON: {ToolCall}", toolCallJson);
                                    // Fall through to text handling below
                                }
                            }
                            else
                            {
                                // No Liquid tool calls - try to extract bash code blocks from markdown
                                logger.LogInformation("No Liquid tool tokens found, checking for markdown bash blocks");
                                var bashCodeRegex = new System.Text.RegularExpressions.Regex(@"```bash\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline);
                                var bashMatch = bashCodeRegex.Match(textContent);

                                if (bashMatch.Success)
                                {
                                    var bashCode = bashMatch.Groups[1].Value.Trim();
                                    logger.LogInformation("✓ Extracted bash code block: {Code}", bashCode.Length > 200 ? bashCode[..200] + "..." : bashCode);

                                    // Convert bash code to tool_use block
                                    try
                                    {
                                        using var ms = new MemoryStream();
                                        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                                        {
                                            w.WriteStartObject();
                                            w.WriteString("type", "tool_use");
                                            w.WriteString("id", $"toolu_{Guid.NewGuid():N}");
                                            w.WriteString("name", "Bash");
                                            w.WritePropertyName("input");
                                            w.WriteStartObject();
                                            w.WriteString("command", bashCode);
                                            w.WriteEndObject();
                                            w.WriteEndObject();
                                        }

                                        var toolUseJson = System.Text.Json.JsonDocument.Parse(ms.ToArray()).RootElement;
                                        var escaped = System.Text.Json.JsonSerializer.Serialize(toolUseJson)[1..^1];
                                        logger.LogInformation("✓ Converted to tool_use block, sending to Claude Code");
                                        await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"tool_use\",\"json\":\"{escaped}\"}}}}\n\n");
                                        await context.Response.Body.FlushAsync();
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogError(ex, "✗ Failed to convert bash block to tool_use");
                                        // Fall back to text
                                        var escaped = System.Text.Json.JsonSerializer.Serialize(textContent)[1..^1];
                                        await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{escaped}\"}}}}\n\n");
                                        await context.Response.Body.FlushAsync();
                                    }
                                }
                                else
                                {
                                    // No bash blocks either - send as plain text
                                    logger.LogInformation("No bash code blocks found, sending as text");
                                    var escaped = System.Text.Json.JsonSerializer.Serialize(textContent)[1..^1];
                                    await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{escaped}\"}}}}\n\n");
                                    await context.Response.Body.FlushAsync();
                                }
                            }
                        }
                        else if (blockType == "tool_use")
                        {
                            // Pass through tool_use block as-is in SSE format
                            var blockJson = System.Text.Json.JsonSerializer.Serialize(block);
                            logger.LogInformation("Forwarding tool_use block, size: {Size}", blockJson.Length);
                            await context.Response.WriteAsync($"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":{blockCount - 1},\"content_block\":{blockJson}}}\n\n");
                            await context.Response.Body.FlushAsync();
                            await context.Response.WriteAsync($"event: content_block_stop\ndata: {{\"type\":\"content_block_stop\",\"index\":{blockCount - 1}}}\n\n");
                            await context.Response.Body.FlushAsync();
                        }
                    }
                }
            }

            logger.LogInformation("JSON response processed: {BlockCount} content blocks sent", blockCount);
            await context.Response.WriteAsync("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            await context.Response.WriteAsync("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":0}}\n\n");
            await context.Response.WriteAsync("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            await context.Response.Body.FlushAsync();
            logger.LogInformation("JSON response fully sent to client");
            return;
        }

        // Handle SSE response - write start event first
        var startEvent = $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"{msgId}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"{targetModel}\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}\n\n";
        logger.LogInformation("Writing SSE start event, length: {Length}", startEvent.Length);
        await context.Response.WriteAsync(startEvent);
        await context.Response.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        await context.Response.Body.FlushAsync();

        string? line;
        var chunkCount = 0;
        var textBuffer = new StringBuilder();
        var inToolCall = false;
        var toolCallBuffer = new StringBuilder();

        // Process first line if it exists
        if (firstLine is not null && firstLine.StartsWith("data: "))
        {
            var data = firstLine["data: ".Length..];
            if (data != "[DONE]")
            {
                // Process the first line
                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    if (chunk.RootElement.TryGetProperty("type", out var typeElem)
                        && typeElem.GetString() == "text_chunk"
                        && chunk.RootElement.TryGetProperty("data", out var textElem))
                    {
                        var text = textElem.GetString() ?? "";
                        textBuffer.Append(text);
                        chunkCount++;
                    }
                }
                catch { }
            }
        }

        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                logger.LogInformation("SSE stream end marker received. Buffer size: {BufferSize} chars", textBuffer.Length);

                // Flush any remaining buffered text before finishing
                if (textBuffer.Length > 0)
                {
                    var fullText = textBuffer.ToString();
                    logger.LogInformation("Processing buffered text, checking for bash blocks");

                    // Try to extract bash code blocks and convert to tool calls
                    var bashCodeRegex = new System.Text.RegularExpressions.Regex(@"```bash\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline);
                    var match = bashCodeRegex.Match(fullText);
                    if (match.Success)
                    {
                        var bashCode = match.Groups[1].Value.Trim();
                        logger.LogInformation("✓ Extracted bash code from SSE stream: {Code}", bashCode.Length > 200 ? bashCode[..200] + "..." : bashCode);

                        // Convert bash code to tool_use block
                        using var ms = new MemoryStream();
                        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                        {
                            w.WriteStartObject();
                            w.WriteString("type", "tool_use");
                            w.WriteString("id", $"toolu_{Guid.NewGuid():N}");
                            w.WriteString("name", "Bash");
                            w.WritePropertyName("input");
                            w.WriteStartObject();
                            w.WriteString("command", bashCode);
                            w.WriteString("description", "Execute bash commands");
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }
                        var toolUseJson = System.Text.Json.JsonDocument.Parse(ms.ToArray()).RootElement;
                        var escaped = System.Text.Json.JsonSerializer.Serialize(toolUseJson)[1..^1];
                        await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"tool_use\",\"json\":\"{escaped}\"}}}}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                    else
                    {
                        // No bash code block, send as text
                        logger.LogInformation("No bash blocks found in SSE stream, sending as text");
                        var escaped = System.Text.Json.JsonSerializer.Serialize(fullText)[1..^1];
                        await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{escaped}\"}}}}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                }
                else
                {
                    logger.LogInformation("SSE stream ended with empty buffer");
                }

                logger.LogInformation("LM Studio stream completed: {ChunkCount} chunks translated", chunkCount);
                await context.Response.WriteAsync("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
                await context.Response.WriteAsync($"event: message_delta\ndata: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"end_turn\",\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":0}}}}\n\n");
                await context.Response.WriteAsync("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                await context.Response.Body.FlushAsync();
                break;
            }

            try
            {
                using var chunk = JsonDocument.Parse(data);
                // LM Studio streaming format: {index: 0, type: "text_chunk", data: "..."}
                if (chunk.RootElement.TryGetProperty("type", out var typeElem)
                    && typeElem.GetString() == "text_chunk"
                    && chunk.RootElement.TryGetProperty("data", out var textElem))
                {
                    var text = textElem.GetString() ?? "";

                    // Handle tool call detection
                    while (text.Length > 0)
                    {
                        if (inToolCall)
                        {
                            // Look for end of tool call
                            var endIdx = text.IndexOf("<|tool_call_end|>");
                            if (endIdx >= 0)
                            {
                                logger.LogInformation("⚙️ Detected tool call end token");
                                toolCallBuffer.Append(text[..endIdx]);
                                inToolCall = false;

                                // Try to parse and convert the tool call
                                var rawToolCall = toolCallBuffer.ToString();
                                logger.LogInformation("Raw Liquid tool call: {RawCall}", rawToolCall);

                                var toolUseJson = LiquidToolTranslator.TryParseAndConvertToolCall(rawToolCall);
                                if (toolUseJson is not null)
                                {
                                    try
                                    {
                                        // Parse and translate using the mapper
                                        using var doc = JsonDocument.Parse(toolUseJson);
                                        var (success, translated) = _toolMapper.TryTranslateTool(doc.RootElement);
                                        if (success && translated.HasValue)
                                        {
                                            logger.LogInformation("✓ Successfully translated Liquid tool: {RawCall} → {TargetTool}",
                                                rawToolCall,
                                                translated.Value.TryGetProperty("name", out var n) ? n.GetString() : "?");
                                            // Output tool_use block in Anthropic format
                                            var escaped = System.Text.Json.JsonSerializer.Serialize(translated.Value)[1..^1];
                                            await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"tool_use\",\"json\":\"{escaped}\"}}}}\n\n");
                                            await context.Response.Body.FlushAsync();
                                        }
                                        else
                                        {
                                            logger.LogWarning("⚠️ Tool not in translation table or disabled: {ToolCall}", rawToolCall);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogError(ex, "✗ Error translating Liquid tool call: {RawCall}", rawToolCall);
                                    }
                                }
                                else
                                {
                                    logger.LogWarning("✗ Failed to parse Liquid tool call: {RawCall}", rawToolCall);
                                }

                                toolCallBuffer.Clear();
                                text = text[(endIdx + "<|tool_call_end|>".Length)..];
                            }
                            else
                            {
                                toolCallBuffer.Append(text);
                                text = "";
                            }
                        }
                        else
                        {
                            // Look for start of tool call
                            var startIdx = text.IndexOf("<|tool_call_start|>");
                            if (startIdx >= 0)
                            {
                                logger.LogInformation("⚙️ Detected tool call start token");
                                // Output any buffered text before tool call
                                textBuffer.Append(text[..startIdx]);
                                if (textBuffer.Length > 0)
                                {
                                    var escaped = System.Text.Json.JsonSerializer.Serialize(textBuffer.ToString())[1..^1];
                                    await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{escaped}\"}}}}\n\n");
                                    await context.Response.Body.FlushAsync();
                                    textBuffer.Clear();
                                }

                                inToolCall = true;
                                text = text[(startIdx + "<|tool_call_start|>".Length)..];
                            }
                            else
                            {
                                // Regular text
                                textBuffer.Append(text);
                                logger.LogDebug("Text chunk: {Text}", text);
                                text = "";
                            }
                        }
                    }

                    chunkCount++;
                }
            }
            catch { /* skip unparseable chunks */ }
        }
    }
}
