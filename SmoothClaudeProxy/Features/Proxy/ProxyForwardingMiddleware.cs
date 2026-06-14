using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SmoothClaudeProxy.Features.Logins;
using SmoothClaudeProxy.Features.ModelRouting;
using SmoothClaudeProxy.Features.Sessions;

namespace SmoothClaudeProxy.Features.Proxy;

/// <summary>
/// Core forwarding middleware: extracts auth identity, applies model routing (Anthropic
/// passthrough, OpenAI conversion, or per-family override), forwards to the alternate LLM
/// upstream, and otherwise lets YARP forward to Anthropic — capturing rate-limit headers
/// into the user-tracking channel on the way out.
/// </summary>
public sealed class ProxyForwardingMiddleware : IMiddleware
{
    private readonly LlmServiceOptions _llm;
    private readonly bool _logTokenFormat;

    public ProxyForwardingMiddleware(IOptions<LlmServiceOptions> llm, IConfiguration config)
    {
        _llm = llm.Value;
        // Read from configuration so appsettings.json provides the default (true) while the
        // LOG_TOKEN_FORMAT env var still overrides it (env config provider wins over appsettings).
        _logTokenFormat = config.GetValue<bool>("LOG_TOKEN_FORMAT");
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var llmUrl = _llm.BaseUrl;
        var llmToken = _llm.AuthToken;
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var req = context.Request;

        var authType = "None";
        string? email = null;
        string? name = null;
        string? label = null;
        string? apiKey = null;
        string? anthropicVersion = null;
        string? bearerToken = null;

        if (req.Headers.TryGetValue("anthropic-version", out var versionHeader))
            anthropicVersion = versionHeader.ToString();

        if (req.Headers.TryGetValue("x-api-key", out var keyHeader))
        {
            authType = "API-Key";
            apiKey = keyHeader.ToString();
        }

        if (req.Headers.TryGetValue("Authorization", out var authHeader))
        {
            authType = "Bearer";
            var raw = authHeader.ToString().Replace("Bearer ", "");
            bearerToken = raw.Length > 0 ? raw : null;
            (email, name) = JwtIdentity.TryDecodeJwt(raw, logger);
        }

        if (req.Headers.TryGetValue("x-user-label", out var labelHeader))
        {
            label = labelHeader.ToString();
            req.Headers.Remove("x-user-label");
        }

        var memCache = context.RequestServices.GetRequiredService<IMemoryCache>();
        var modelRoute = memCache.Get<ModelRouteSettings>("model_route_settings") ?? new ModelRouteSettings();

        // Only read body if model routing is enabled (to extract model and prompt fields)
        string? model = null;
        string? firstUserPrompt = null;
        if (modelRoute.Enabled && req.ContentType?.Contains("application/json") == true && req.ContentLength > 0)
        {
            try
            {
                req.EnableBuffering();
                using var reader = new StreamReader(req.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                req.Body.Position = 0;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("model", out var m))
                    model = m.GetString();
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        if (msg.TryGetProperty("role", out var role) && role.GetString() == "user"
                            && msg.TryGetProperty("content", out var content))
                        {
                            if (content.ValueKind == JsonValueKind.String)
                                firstUserPrompt = content.GetString();
                            else if (content.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var part in content.EnumerateArray())
                                {
                                    if (part.TryGetProperty("type", out var t) && t.GetString() == "text"
                                        && part.TryGetProperty("text", out var txt))
                                    {
                                        firstUserPrompt = txt.GetString();
                                        break;
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch { /* non-JSON or parse failure — ignore */ }
        }

        if (_logTokenFormat && bearerToken is not null)
        {
            var parts = bearerToken.Split('.');
            var masked = bearerToken.Length > 20 ? bearerToken[..10] + "..." + bearerToken[^10..] : "***";
            logger.LogInformation("[token-debug] format: {Parts} parts, length: {Length}, preview: {Preview}",
                parts.Length, bearerToken.Length, masked);
            logger.LogInformation("[token-debug] type: {Type}",
                parts.Length >= 2 ? "JWT (header.payload.signature)" : "opaque token");
        }

        // Per-family model override: if the inbound claude-* model matches a configured family
        // prefix and that override is non-empty, swap the model and force LLM routing.
        var originalModel = model;
        var overrideModel = modelRoute.Enabled ? ModelOverrideResolver.ResolveModelOverride(model, modelRoute) : null;
        if (overrideModel is not null)
        {
            logger.LogInformation("Model override: {Original} -> {Override} (routing to LLM)", originalModel, overrideModel);
            model = overrideModel;
        }

        var routesToAnthropic = model?.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) == true;
        var isLlmRoute = modelRoute.Enabled && !string.IsNullOrWhiteSpace(model)
            && (!routesToAnthropic || overrideModel is not null);
        var routeTarget = isLlmRoute ? "OpenCode" : "Anthropic";

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, model={Model}, route={Route}]",
            req.Method, req.Path, req.QueryString, authType, model ?? "-", routeTarget);

        var isQwen = model?.Contains("qwen", StringComparison.OrdinalIgnoreCase) == true;

        // Route matching models to the alternate upstream.
        if (isLlmRoute)
        {
            // Anthropic-native passthrough (e.g. opencode.ai zen /v1/messages):
            // the upstream already speaks the Anthropic Messages API, so the inbound
            // request needs no conversion. Forward the body completely unchanged
            // (including the model field) and stream the SSE response straight back.
            if (modelRoute.ApiFormat.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var ptFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
                using var ptClient = ptFactory.CreateClient();
                ptClient.Timeout = TimeSpan.FromMinutes(10);

                var ptUrl = $"{llmUrl.TrimEnd('/')}{req.Path}{req.QueryString}";
                using var ptReq = new HttpRequestMessage(HttpMethod.Post, ptUrl);
                ptReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {llmToken}");
                ptReq.Headers.TryAddWithoutValidation("x-api-key", llmToken);
                ptReq.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion ?? "2023-06-01");
                ptReq.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                // Forward the body as-is — model field stays untouched. If the client set
                // cache_control anywhere (block-level or top-level), it passes through
                // unchanged. If the request has none at all, inject a top-level
                // cache_control so anthropic-compatible upstreams (e.g. MiniMax) enable
                // prompt caching automatically.
                req.Body.Position = 0;
                using var ptReader = new StreamReader(req.Body, leaveOpen: true);
                var ptBody = await ptReader.ReadToEndAsync();
                try
                {
                    using var ptDoc = JsonDocument.Parse(ptBody);
                    // Substring scan is a fast negative check only; it can false-positive on
                    // prompt text that mentions cache_control, so confirm structurally.
                    var hasCacheControl = ptBody.Contains("\"cache_control\"", StringComparison.Ordinal)
                        && ModelOverrideResolver.ContainsCacheControlProperty(ptDoc.RootElement);
                    var needsModelSwap = overrideModel is not null;
                    if (ptDoc.RootElement.ValueKind == JsonValueKind.Object && (needsModelSwap || !hasCacheControl))
                    {
                        using var ptMs = new MemoryStream();
                        using (var ptWriter = new Utf8JsonWriter(ptMs))
                        {
                            // Append after the original properties to keep their order intact.
                            ptWriter.WriteStartObject();
                            foreach (var prop in ptDoc.RootElement.EnumerateObject())
                            {
                                if (needsModelSwap && prop.NameEquals("model"))
                                    ptWriter.WriteString("model", model);
                                else
                                    prop.WriteTo(ptWriter);
                            }
                            if (!hasCacheControl)
                            {
                                ptWriter.WritePropertyName("cache_control");
                                ptWriter.WriteStartObject();
                                ptWriter.WriteString("type", "ephemeral");
                                ptWriter.WriteEndObject();
                            }
                            ptWriter.WriteEndObject();
                        }
                        ptBody = Encoding.UTF8.GetString(ptMs.ToArray());
                        if (needsModelSwap)
                            logger.LogInformation("Passthrough model swapped {Original} -> {Model}", originalModel, model);
                        if (!hasCacheControl)
                            logger.LogInformation("Injected top-level cache_control (ephemeral) — request had none");
                    }
                }
                catch (JsonException) { /* non-JSON body — forward unchanged */ }
                ptReq.Content = new StringContent(ptBody, Encoding.UTF8, "application/json");
                ptReq.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                logger.LogInformation("LLM passthrough -> {Url} [model={Model}]", ptUrl, model ?? "-");

                var ptBuffering = context.Features
                    .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
                ptBuffering?.DisableBuffering();

                try
                {
                    using var ptResp = await ptClient.SendAsync(ptReq, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    context.Response.StatusCode = (int)ptResp.StatusCode;
                    if (ptResp.Content.Headers.ContentType is not null)
                        context.Response.ContentType = ptResp.Content.Headers.ContentType.ToString();

                    if (!ptResp.IsSuccessStatusCode)
                        logger.LogWarning("<- {StatusCode} from LLM passthrough {Url}", (int)ptResp.StatusCode, ptUrl);

                    await using var ptStream = await ptResp.Content.ReadAsStreamAsync(context.RequestAborted);
                    await ptStream.CopyToAsync(context.Response.Body, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    logger.LogInformation("<- {StatusCode} {Path} [LLM passthrough]", (int)ptResp.StatusCode, req.Path);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogError(ex, "LLM passthrough connection failed at {Url} — is it reachable?", ptUrl);
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 502;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(
                            $"{{\"error\":\"LLM unreachable at {llmUrl}: {ex.Message}\"}}");
                    }
                }
                return;
            }

            // This LLM route does not support the count_tokens endpoint — intercept it and
            // return an estimate so Claude Code can manage its own context window.
            // Without this, Claude Code gets a 400, can't track context size, and
            // eventually sends an oversized request that Qwen silently truncates,
            // causing the model to lose conversation history and loop on the same commands.
            if (req.Path.Value?.EndsWith("count_tokens", StringComparison.OrdinalIgnoreCase) == true)
            {
                req.EnableBuffering();
                req.Body.Position = 0;
                using var ctReader = new StreamReader(req.Body, leaveOpen: true);
                var ctBody = await ctReader.ReadToEndAsync();
                var estimatedTokens = Math.Max(1000, ctBody.Length / 4);
                logger.LogInformation("count_tokens intercepted for LLM route — estimated {Tokens} tokens from {Bytes} bytes", estimatedTokens, ctBody.Length);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"input_tokens\":{estimatedTokens}}}");
                return;
            }

            var httpFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var httpClient = httpFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var targetUrl = $"{llmUrl.TrimEnd('/')}/v1/chat/completions";
            logger.LogInformation("Target endpoint: {Endpoint}", targetUrl.Split('/').Last());
            using var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            proxyReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {llmToken}");

            // StripNonClaudeModels off (default): forward the body verbatim and stream the
            // response straight back. Qwen always runs the conversion pipeline regardless —
            // Qwen2_5ResponseHandler requires the converted non-stream flow.
            var verbatim = !modelRoute.StripNonClaudeModels && !isQwen;

            // Convert and forward request to the configured LLM
            if (req.ContentLength > 0 || req.ContentType is not null)
            {
                req.Body.Position = 0;
                using var bodyReader = new StreamReader(req.Body, leaveOpen: true);
                var bodyText = await bodyReader.ReadToEndAsync();
                logger.LogInformation("Original request body size: {Bytes} bytes", bodyText.Length);

                if (verbatim)
                {
                    // No format conversion, no field rewriting, no filtering — except the
                    // per-family model swap, which must reach the upstream. The body is
                    // still Anthropic-shaped — the upstream's /v1/chat/completions must
                    // tolerate that, or StripNonClaudeModels must be turned on.
                    if (overrideModel is not null)
                    {
                        bodyText = ModelOverrideResolver.RewriteModelField(bodyText, model!);
                        logger.LogInformation("Verbatim model swapped {Original} -> {Model}", originalModel, model);
                    }
                    proxyReq.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
                    proxyReq.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    logger.LogWarning("StripNonClaudeModels off — forwarding Anthropic-shape body verbatim to {Endpoint}; enable StripNonClaudeModels if the upstream rejects it [model={Model}]",
                        "/v1/chat/completions", model);
                }
                else
                {
                    if (!modelRoute.StripNonClaudeModels && isQwen)
                        logger.LogInformation("StripNonClaudeModels off but model is Qwen — running conversion pipeline anyway (Qwen response handler requires it)");

                    using var bodyDoc = JsonDocument.Parse(bodyText);
                    var root = bodyDoc.RootElement;

                    // Log what fields are in the request
                    var requestFields = new List<string>();
                    foreach (var prop in root.EnumerateObject())
                        requestFields.Add(prop.Name);
                    logger.LogInformation("Request fields: {Fields}", string.Join(", ", requestFields));

                    // If tools are present, log their size
                    if (root.TryGetProperty("tools", out var tools))
                    {
                        var toolsJson = tools.GetRawText();
                        logger.LogInformation("Tools field size: {Bytes} bytes, tool count: {Count}",
                            toolsJson.Length, tools.GetArrayLength());
                    }

                    // Rewrite model field and filter out Anthropic-specific fields unsupported by local models
                    // metadata and context_management cause context bloat. cache_control is left in
                    // place (not recursively stripped) — most OpenAI-compatible servers ignore unknown
                    // fields; a strict upstream that rejects it needs cache_control disabled client-side.
                    var fieldsToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "model", "budget_tokens", "thinking", "metadata", "context_management" };

                    // For Qwen: skip tool_choice, system, and stream — all rewritten explicitly below
                    // stream must be false for Qwen: Qwen2_5ResponseHandler expects plain JSON, not SSE
                    if (isQwen)
                    {
                        fieldsToSkip.Add("tool_choice");
                        fieldsToSkip.Add("system");
                        fieldsToSkip.Add("stream");
                    }
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        w.WriteString("model", model);

                        logger.LogInformation("Request preprocessing for model: {Model}", model);

                        if (isQwen)
                        {
                            logger.LogInformation("Qwen: converting from Anthropic format to OpenAI chat format");
                            // For Qwen, convert Anthropic Messages API to OpenAI chat format
                            // System message becomes first message with role="system"
                            w.WritePropertyName("messages");
                            w.WriteStartArray();

                            // Use a minimal fixed system prompt — discard CLAUDE.md and all Claude Code system blocks
                            w.WriteStartObject();
                            w.WriteString("role", "system");
                            w.WriteString("content", "You are a coding assistant. You MUST use tools to complete tasks. NEVER explain or describe what you would do — always call the appropriate tool immediately. If the user asks you to run a command, call Bash. If asked to read a file, call Read. Act, don't explain.");
                            w.WriteEndObject();
                            logger.LogInformation("Qwen: using minimal fixed system prompt");

                            // Add all messages — convert Anthropic format to OpenAI chat format
                            if (root.TryGetProperty("messages", out var messagesField))
                            {
                                foreach (var msg in messagesField.EnumerateArray())
                                {
                                    var roleStr = msg.TryGetProperty("role", out var roleElem) ? roleElem.GetString() ?? "" : "";

                                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                                    {
                                        var blocks = content.EnumerateArray().ToList();
                                        var hasToolUse = roleStr == "assistant" && blocks.Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use");
                                        var hasToolResult = roleStr == "user" && blocks.Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_result");

                                        if (hasToolUse)
                                        {
                                            // Convert Anthropic tool_use → OpenAI tool_calls
                                            w.WriteStartObject();
                                            w.WriteString("role", "assistant");
                                            w.WriteNull("content");
                                            w.WritePropertyName("tool_calls");
                                            w.WriteStartArray();
                                            foreach (var block in blocks)
                                            {
                                                if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") continue;
                                                var tid = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : $"tool_{Guid.NewGuid():N}";
                                                var tname = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
                                                var tinput = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";
                                                w.WriteStartObject();
                                                w.WriteString("id", tid);
                                                w.WriteString("type", "function");
                                                w.WritePropertyName("function");
                                                w.WriteStartObject();
                                                w.WriteString("name", tname);
                                                w.WriteString("arguments", tinput);
                                                w.WriteEndObject();
                                                w.WriteEndObject();
                                            }
                                            w.WriteEndArray();
                                            w.WriteEndObject();
                                        }
                                        else if (hasToolResult)
                                        {
                                            // Convert Anthropic tool_result → OpenAI tool role messages (one per result)
                                            foreach (var block in blocks)
                                            {
                                                if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_result") continue;
                                                var callId = block.TryGetProperty("tool_use_id", out var cid) ? cid.GetString() ?? "" : "";
                                                string resultText;
                                                if (block.TryGetProperty("content", out var rc))
                                                {
                                                    if (rc.ValueKind == JsonValueKind.String)
                                                    {
                                                        resultText = await PersistedOutputResolver.ResolveAsync(rc.GetString() ?? "");
                                                    }
                                                    else if (rc.ValueKind == JsonValueKind.Array)
                                                    {
                                                        // Extract text blocks, resolving any persisted-output in each
                                                        var sb = new StringBuilder();
                                                        foreach (var cb in rc.EnumerateArray())
                                                        {
                                                            if (cb.TryGetProperty("type", out var cbt) && cbt.GetString() == "text" &&
                                                                cb.TryGetProperty("text", out var cbtxt))
                                                                sb.Append(await PersistedOutputResolver.ResolveAsync(cbtxt.GetString() ?? ""));
                                                        }
                                                        resultText = sb.ToString();
                                                    }
                                                    else
                                                    {
                                                        resultText = rc.GetRawText();
                                                    }
                                                }
                                                else
                                                    resultText = "";
                                                w.WriteStartObject();
                                                w.WriteString("role", "tool");
                                                w.WriteString("tool_call_id", callId);
                                                w.WriteString("content", resultText);
                                                w.WriteEndObject();
                                            }
                                        }
                                        else
                                        {
                                            // Regular message — filter noise, keep text blocks
                                            w.WriteStartObject();
                                            w.WriteString("role", roleStr);
                                            w.WritePropertyName("content");
                                            w.WriteStartArray();
                                            foreach (var block in blocks)
                                            {
                                                if (block.TryGetProperty("type", out var bType) && bType.GetString() == "text"
                                                    && block.TryGetProperty("text", out var bText))
                                                {
                                                    var text = LocalLlmRequestFilter.StripInlineNoise(bText.GetString() ?? "");
                                                    if (!LocalLlmRequestFilter.IsMessageNoise(text) && !string.IsNullOrWhiteSpace(text))
                                                    {
                                                        w.WriteStartObject();
                                                        w.WriteString("type", "text");
                                                        w.WriteString("text", text);
                                                        w.WriteEndObject();
                                                    }
                                                }
                                            }
                                            w.WriteEndArray();
                                            w.WriteEndObject();
                                        }
                                    }
                                    else if (msg.TryGetProperty("content", out var contentStr) && contentStr.ValueKind == JsonValueKind.String)
                                    {
                                        w.WriteStartObject();
                                        w.WriteString("role", roleStr);
                                        w.WriteString("content", LocalLlmRequestFilter.StripInlineNoise(contentStr.GetString() ?? ""));
                                        w.WriteEndObject();
                                    }
                                }
                            }

                            w.WriteEndArray();

                            // Add tools if present - convert from Anthropic to OpenAI format
                            if (root.TryGetProperty("tools", out var toolsField) && toolsField.ValueKind == JsonValueKind.Array)
                            {
                                w.WritePropertyName("tools");
                                w.WriteStartArray();

                                var qwenToolCount = 0;
                                foreach (var tool in toolsField.EnumerateArray())
                                {
                                    LocalLlmRequestFilter.WriteSlimTool(w, tool);
                                    qwenToolCount++;
                                }

                                w.WriteEndArray(); // end tools array
                                logger.LogInformation("✓ Qwen tools: {Included} included", qwenToolCount);
                            }

                            // Force tool_choice=required when tools are present — prevents Qwen from responding with text instead of calling tools
                            var hasTools = root.TryGetProperty("tools", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0;
                            w.WriteString("tool_choice", hasTools ? "required" : "none");

                            // Force stream=false — Qwen2_5ResponseHandler expects plain JSON, not SSE
                            w.WriteBoolean("stream", false);

                            logger.LogInformation("✓ Converted to OpenAI chat format (stream=false)");
                        }

                        // Qwen: no top-level system — already written as messages[0] in the Qwen block above
                        // Default (unknown models): pass through system field
                        if (!isQwen)
                        {
                            if (root.TryGetProperty("system", out var sysMsg))
                            {
                                w.WritePropertyName("system");
                                sysMsg.WriteTo(w);
                            }
                        }

                        // Copy all other fields from original request, except unsupported ones
                        // For messages, strip system-reminder tags to reduce token overhead
                        // For tools, simplify to basic format (name, description, parameters only)
                        var filteredFields = new List<string>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name.Equals("messages", StringComparison.OrdinalIgnoreCase))
                            {
                                // Skip messages for Qwen - already written in Qwen block
                                if (isQwen)
                                {
                                    logger.LogInformation("Qwen: Skipping messages field (already converted in Qwen block)");
                                    continue;
                                }

                                // Filter out system reminders from messages to save tokens
                                w.WritePropertyName("messages");
                                w.WriteStartArray();
                                foreach (var msg in prop.Value.EnumerateArray())
                                {
                                    w.WriteStartObject();
                                    if (msg.TryGetProperty("role", out var role)) { w.WritePropertyName("role"); role.WriteTo(w); }
                                    if (msg.TryGetProperty("content", out var content))
                                    {
                                        w.WritePropertyName("content");
                                        if (content.ValueKind == JsonValueKind.String)
                                        {
                                            var text = content.GetString() ?? "";
                                            // Strip system reminders
                                            text = System.Text.RegularExpressions.Regex.Replace(text, @"<system-reminder>.*?</system-reminder>\n*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                                            w.WriteStringValue(text);
                                        }
                                        else if (content.ValueKind == JsonValueKind.Array)
                                        {
                                            // Content is array of blocks, filter out system-reminder text blocks
                                            w.WriteStartArray();
                                            foreach (var block in content.EnumerateArray())
                                            {
                                                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out var txt))
                                                {
                                                    var text = txt.GetString() ?? "";
                                                    if (!text.Contains("<system-reminder>"))
                                                    {
                                                        block.WriteTo(w);
                                                    }
                                                }
                                                else
                                                {
                                                    block.WriteTo(w);
                                                }
                                            }
                                            w.WriteEndArray();
                                        }
                                        else
                                        {
                                            content.WriteTo(w);
                                        }
                                    }
                                    w.WriteEndObject();
                                }
                                w.WriteEndArray();
                            }
                            else if (prop.Name.Equals("tools", StringComparison.OrdinalIgnoreCase))
                            {
                                if (isQwen)
                                {
                                    // Qwen: tools already written in Qwen block, skip
                                }
                                else
                                {
                                    // Default: keep tools
                                    w.WritePropertyName("tools");
                                    prop.Value.WriteTo(w);
                                }
                            }
                            else if (!fieldsToSkip.Contains(prop.Name))
                            {
                                w.WritePropertyName(prop.Name);
                                prop.Value.WriteTo(w);
                            }
                            else
                            {
                                filteredFields.Add(prop.Name);
                            }
                        }
                        w.WriteEndObject();

                        if (filteredFields.Count > 0)
                            logger.LogInformation("Filtered out unsupported fields: {Fields}", string.Join(", ", filteredFields));
                    }

                    var payload = ms.ToArray();
                    proxyReq.Content = new ByteArrayContent(payload);
                    proxyReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                    logger.LogInformation("Forwarding to LLM /v1/chat/completions: model={Model}", model);
                }
            }

            var responseBuffering = context.Features
                .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            responseBuffering?.DisableBuffering();

            try
            {
                logger.LogInformation("Sending request to LLM: {Url}", targetUrl);
                using var lmResp = await httpClient.SendAsync(proxyReq, HttpCompletionOption.ResponseHeadersRead);

                logger.LogInformation("<- {StatusCode} from LLM, content type: {ContentType}, length: {Length}",
                    (int)lmResp.StatusCode,
                    lmResp.Content.Headers.ContentType,
                    lmResp.Content.Headers.ContentLength);

                if (!lmResp.IsSuccessStatusCode)
                {
                    var errorBody = await lmResp.Content.ReadAsStringAsync();
                    var isContextOverflow = errorBody.Contains("context length", StringComparison.OrdinalIgnoreCase)
                                        || errorBody.Contains("context window", StringComparison.OrdinalIgnoreCase)
                                        || errorBody.Contains("initial prompt", StringComparison.OrdinalIgnoreCase);
                    if (isContextOverflow)
                    {
                        logger.LogWarning("LLM context overflow — conversation too long for {Model}. Run /compact in Claude Code to reduce context, or increase the model context length for the configured LLM.", model);
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Context too long for the configured LLM. Run /compact in Claude Code to reduce context, or use a model with a larger context window.\"}}");
                    }
                    else
                    {
                        logger.LogWarning("<- {StatusCode} from LLM: {Error}", (int)lmResp.StatusCode, errorBody);
                        context.Response.StatusCode = (int)lmResp.StatusCode;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(errorBody);
                    }
                    return;
                }

                logger.LogInformation("Starting response handling");

                if (verbatim)
                {
                    // Verbatim mode: stream the upstream response straight back, untouched.
                    if (lmResp.Content.Headers.ContentType is not null)
                        context.Response.ContentType = lmResp.Content.Headers.ContentType.ToString();
                    await using var verbatimStream = await lmResp.Content.ReadAsStreamAsync(context.RequestAborted);
                    await verbatimStream.CopyToAsync(context.Response.Body, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    logger.LogInformation("<- 200 POST {Path} [LLM verbatim passthrough]", req.Path);
                }
                else
                {
                    // Resolve the response handler for the target model. Handlers are keyed by
                    // exact model name; if none is registered we cannot translate the upstream
                    // reply back to Anthropic format, so fail with an explicit, actionable error
                    // instead of letting the keyed lookup throw an opaque 500.
                    var handler = context.RequestServices.GetKeyedService<ILocalLLMResponseHandler>(model!);
                    if (handler is null)
                    {
                        logger.LogWarning("No response handler registered for model '{Model}' — cannot translate the upstream reply to Anthropic format. Register an ILocalLLMResponseHandler for this model, use ApiFormat=anthropic passthrough, or disable StripNonClaudeModels.", model);
                        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync($"{{\"type\":\"error\",\"error\":{{\"type\":\"api_error\",\"message\":\"No response handler registered for model '{model}'. Register a handler, use ApiFormat=anthropic passthrough, or disable StripNonClaudeModels.\"}}}}");
                        return;
                    }
                    await handler.HandleResponseAsync(context, lmResp, model!, logger);

                    logger.LogInformation("<- 200 POST {Path} [LLM via /api/v1/chat] translated to Anthropic SSE", req.Path);
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "LLM connection failed at {Url} — is it running?", llmUrl);
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":\"LLM unreachable at {llmUrl}: {ex.Message}\"}}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during LLM response handling");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync($"{{\"error\":\"Response handling failed: {ex.Message}\"}}");
                }
            }
            return;
        }

        memCache.TryGetValue<ActiveSession>("active_session", out var activeSession);

        // Override auth headers from active session if one is set
        if (activeSession is not null)
        {
            req.Headers["Authorization"] = $"Bearer {activeSession.BearerToken}";
            if (activeSession.ApiKey is not null)
                req.Headers["x-api-key"] = activeSession.ApiKey;
            logger.LogInformation("Auth overridden from active session: {Email}", activeSession.Email);
        }

        // Anthropic prefers the Authorization (Bearer) credential over the API key.
        // When BOTH a real Bearer token and an x-api-key are present, Anthropic's selection
        // is ambiguous and may consume the API key instead of the logged-in subscription —
        // so drop the outbound x-api-key, leaving the Bearer token as the only credential.
        // An API-key-only request passes through untouched: the strip requires a non-blank
        // Authorization value (an empty/blank Authorization header does not count, matching
        // the JWT-capture path which treats it as no token). Runs after the session override,
        // so session-injected credentials (always a real Bearer token) are covered too.
        // Identity capture above is untouched — the observed apiKey is still recorded.
        var hasBearerCredential = req.Headers.TryGetValue("Authorization", out var outboundAuth)
            && !string.IsNullOrWhiteSpace(outboundAuth.ToString().Replace("Bearer ", ""));
        if (hasBearerCredential && req.Headers.ContainsKey("x-api-key"))
        {
            req.Headers.Remove("x-api-key");
            logger.LogInformation("Both Authorization and x-api-key present — removed x-api-key (Bearer preferred for Anthropic)");
        }

        var responseBufferingFeature = context.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        responseBufferingFeature?.DisableBuffering();

        logger.LogInformation("Anthropic request: {Method} {Path} | Content-Length: {ContentLength}",
            req.Method, req.Path, req.ContentLength);

        await next(context);

        var status = context.Response.StatusCode;
        if (status >= 400)
            logger.LogWarning("<- {StatusCode} {Method} {Path} | PROXY ERROR — Remember you are running via proxy: is it started? do you have a valid key?",
                status, req.Method, req.Path);
        else
            logger.LogInformation("<- {StatusCode} {Method} {Path}",
                status, req.Method, req.Path);

        // Capture unified rate limit headers from upstream response and write to channel
        if (bearerToken is not null && activeSession is null)
        {
            var resp = context.Response;

            double? util5h = null;
            double? util7d = null;
            long? reset5h = null;
            long? reset7d = null;

            if (resp.Headers.TryGetValue("anthropic-ratelimit-unified-5h-utilization", out var u5h)
                && double.TryParse(u5h.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var u5hVal))
                util5h = Math.Round(u5hVal, 2);

            if (resp.Headers.TryGetValue("anthropic-ratelimit-unified-7d-utilization", out var u7d)
                && double.TryParse(u7d.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var u7dVal))
                util7d = Math.Round(u7dVal, 2);

            if (resp.Headers.TryGetValue("anthropic-ratelimit-unified-5h-reset", out var r5h)
                && long.TryParse(r5h.ToString(), out var r5hVal))
                reset5h = r5hVal;

            if (resp.Headers.TryGetValue("anthropic-ratelimit-unified-7d-reset", out var r7d)
                && long.TryParse(r7d.ToString(), out var r7dVal))
                reset7d = r7dVal;

            if (util5h.HasValue || util7d.HasValue)
                logger.LogInformation("Anthropic rate limits: 5h={Util5h}% (reset {Reset5h}), 7d={Util7d}% (reset {Reset7d})",
                    util5h ?? 0, reset5h.HasValue ? DateTimeOffset.FromUnixTimeSeconds(reset5h.Value).ToString("HH:mm UTC") : "N/A",
                    util7d ?? 0, reset7d.HasValue ? DateTimeOffset.FromUnixTimeSeconds(reset7d.Value).ToString("HH:mm UTC") : "N/A");

            var channel = context.RequestServices.GetRequiredService<Channel<UserRecord>>();
            await channel.Writer.WriteAsync(new UserRecord
            {
                BearerToken = bearerToken,
                Email = email,
                Label = label,
                ApiKey = apiKey,
                AnthropicVersion = anthropicVersion,
                CreatedUtc = DateTime.UtcNow,
                Utilization5h = util5h,
                Utilization7d = util7d,
                Reset5h = reset5h,
                Reset7d = reset7d
            });
        }
    }
}
