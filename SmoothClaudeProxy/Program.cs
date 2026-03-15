using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Scalar.AspNetCore;
using Serilog;
using MEL = Microsoft.Extensions.Logging;

var workspace = Environment.GetEnvironmentVariable("WORKSPACE_PATH") ?? "/data";
var dbPath = Path.Combine(workspace, "claude-auth.db");
var logPath = Path.Combine(workspace, "logs", "claude-proxy-.log");
var localLlmLogPath = Path.Combine(workspace, "logs", "local-llm-service-.log");
var toolsLogPath = Path.Combine(workspace, "logs", "tools-log.log"); // Non-rolling tools log
var localLlmUrl = Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"); // env var overrides appsettings
var localLlmToken = Environment.GetEnvironmentVariable("LMSTUDIO_AUTH_TOKEN"); // env var overrides appsettings

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Async(a => a.Console())
    .WriteTo.Async(a => a.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7))
    .WriteTo.Logger(lc => lc
        .MinimumLevel.Information()
        .Filter.ByIncludingOnly(le =>
            le.MessageTemplate?.Text?.Contains("Local LLM", StringComparison.OrdinalIgnoreCase) == true ||
            le.MessageTemplate?.Text?.Contains("LM Studio", StringComparison.OrdinalIgnoreCase) == true ||
            le.MessageTemplate?.Text?.Contains("/api/v1/chat", StringComparison.OrdinalIgnoreCase) == true ||
            le.Properties.ContainsKey("Route") && le.Properties["Route"]?.ToString() == "\"LM Studio\"")
        .WriteTo.Async(a => a.File(
            localLlmLogPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)))
    .WriteTo.Logger(lc => lc
        .MinimumLevel.Information()
        .Filter.ByIncludingOnly(le =>
            le.MessageTemplate?.Text?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true &&
            (le.Level >= Serilog.Events.LogEventLevel.Warning ||
             le.MessageTemplate?.Text?.Contains("unsupported", StringComparison.OrdinalIgnoreCase) == true ||
             le.MessageTemplate?.Text?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
             le.MessageTemplate?.Text?.Contains("Failed", StringComparison.OrdinalIgnoreCase) == true))
        .WriteTo.File(
            toolsLogPath,
            rollingInterval: RollingInterval.Infinite, // No rolling
            retainedFileCountLimit: null)) // Keep all
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.Configure<KestrelServerOptions>(options =>
    {
        options.AllowSynchronousIO = true;
    });

    builder.Services.AddOpenApi();
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddHttpClient();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ILiteDatabase>(_ => new LiteDatabase(dbPath));

    // Register local LLM response handlers with keyed DI (open-closed principle)
    builder.Services.AddKeyedScoped<ILocalLLMResponseHandler>(
        "qwen/qwen2.5-coder-14b",
        (sp, key) => new Qwen2_5ResponseHandler());

    var upsertChannel = Channel.CreateUnbounded<UserRecord>(
        new UnboundedChannelOptions { SingleReader = true });
    builder.Services.AddSingleton(upsertChannel);
    builder.Services.AddHostedService<UserUpsertWorker>();

    var app = builder.Build();

    // Seed local LLM service routing settings from appsettings.json
    var startupCache = app.Services.GetRequiredService<IMemoryCache>();
    var localLlmConfig = builder.Configuration.GetSection("LocalLLMService");
    localLlmUrl ??= localLlmConfig.GetValue<string>("BaseUrl") ?? "http://host.docker.internal:1234";
    localLlmToken ??= localLlmConfig.GetValue<string>("AuthToken") ?? "lmstudio";
    var modelRouteDefaults = new ModelRouteSettings
    {
        Enabled = localLlmConfig.GetValue("Enabled", true),
        FromModel = localLlmConfig.GetValue("FromModel", "Haiku")!,
        FromPrompt = localLlmConfig.GetValue("FromPrompt", "")!,
        ToModel = localLlmConfig.GetValue("ToModel", "qwen/qwen2.5-coder-14b")!,
        IncludedTools = localLlmConfig.GetSection("IncludedTools").Get<List<string>>() ?? new(),
        AllowedMcpTools = localLlmConfig.GetSection("AllowedMcpTools").Get<List<string>>() ?? new(),
        BlockedMcpTools = localLlmConfig.GetSection("BlockedMcpTools").Get<List<string>>() ?? new()
    };
    startupCache.Set("model_route_settings", modelRouteDefaults);

    app.Use(async (context, next) =>
    {
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
            (email, name) = TryDecodeJwt(raw, logger);
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

        if (Environment.GetEnvironmentVariable("LOG_TOKEN_FORMAT") == "true" && bearerToken is not null)
        {
            var parts = bearerToken.Split('.');
            var masked = bearerToken.Length > 20 ? bearerToken[..10] + "..." + bearerToken[^10..] : "***";
            logger.LogInformation("[token-debug] format: {Parts} parts, length: {Length}, preview: {Preview}",
                parts.Length, bearerToken.Length, masked);
            logger.LogInformation("[token-debug] type: {Type}",
                parts.Length >= 2 ? "JWT (header.payload.signature)" : "opaque token");
        }

        var matchesFromModel = model?.Contains(modelRoute.FromModel, StringComparison.OrdinalIgnoreCase) == true;
        var matchesFromPrompt = !string.IsNullOrWhiteSpace(modelRoute.FromPrompt)
            && firstUserPrompt?.StartsWith(modelRoute.FromPrompt, StringComparison.OrdinalIgnoreCase) == true;
        var isLmStudioRoute = modelRoute.Enabled && (matchesFromModel || matchesFromPrompt);
        var routeTarget = isLmStudioRoute ? "LM Studio" : "Anthropic";

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, model={Model}, route={Route}]",
            req.Method, req.Path, req.QueryString, authType, model ?? "-", routeTarget);

        var isQwen = modelRoute.ToModel.Contains("qwen", StringComparison.OrdinalIgnoreCase);

        // Route matching models to local LLM via LM Studio
        if (isLmStudioRoute)
        {
            // LM Studio does not support the count_tokens endpoint — intercept it and
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
                logger.LogInformation("count_tokens intercepted for LM Studio route — estimated {Tokens} tokens from {Bytes} bytes", estimatedTokens, ctBody.Length);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"input_tokens\":{estimatedTokens}}}");
                return;
            }

            var httpFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var httpClient = httpFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var targetUrl = $"{localLlmUrl.TrimEnd('/')}/v1/chat/completions";
            logger.LogInformation("Target endpoint: {Endpoint}", targetUrl.Split('/').Last());
            using var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            proxyReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {localLlmToken}");

            // Convert and forward request to local LLM
            if (req.ContentLength > 0 || req.ContentType is not null)
            {
                req.Body.Position = 0;
                using var bodyReader = new StreamReader(req.Body, leaveOpen: true);
                var bodyText = await bodyReader.ReadToEndAsync();
                logger.LogInformation("Original request body size: {Bytes} bytes", bodyText.Length);

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
                // metadata and context_management cause context bloat; cache_control is Anthropic-only
                var fieldsToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "model", "budget_tokens", "thinking", "metadata", "context_management" };

                // For Qwen: skip tool_choice, system, and stream — all handled explicitly
                // stream must be false for Qwen: Qwen2_5ResponseHandler expects plain JSON, not SSE
                if (isQwen)
                {
                    fieldsToSkip.Add("tool_choice");
                    fieldsToSkip.Add("system");
                    fieldsToSkip.Add("stream");
                }
                var fieldsToRemoveNested = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "cache_control" };

                // Helper to recursively remove nested fields
                void WriteElementWithoutNested(Utf8JsonWriter writer, JsonElement elem)
                {
                    switch (elem.ValueKind)
                    {
                        case JsonValueKind.Object:
                            writer.WriteStartObject();
                            foreach (var prop in elem.EnumerateObject())
                            {
                                if (!fieldsToRemoveNested.Contains(prop.Name))
                                {
                                    writer.WritePropertyName(prop.Name);
                                    WriteElementWithoutNested(writer, prop.Value);
                                }
                            }
                            writer.WriteEndObject();
                            break;
                        case JsonValueKind.Array:
                            writer.WriteStartArray();
                            foreach (var item in elem.EnumerateArray())
                                WriteElementWithoutNested(writer, item);
                            writer.WriteEndArray();
                            break;
                        default:
                            elem.WriteTo(writer);
                            break;
                    }
                }

                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", modelRoute.ToModel);

                    logger.LogInformation("Request preprocessing for Qwen: {ToModel}", modelRoute.ToModel);

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
                            var qwenSkippedCount = 0;
                            foreach (var tool in toolsField.EnumerateArray())
                            {
                                var toolName = tool.TryGetProperty("name", out var nameCheck) ? nameCheck.GetString() ?? "" : "";
                                if (!LocalLlmToolFilter.IsAllowed(toolName, modelRoute.IncludedTools, modelRoute.AllowedMcpTools, modelRoute.BlockedMcpTools))
                                {
                                    qwenSkippedCount++;
                                    continue;
                                }

                                LocalLlmRequestFilter.WriteSlimTool(w, tool);
                                qwenToolCount++;
                            }

                            w.WriteEndArray(); // end tools array
                            logger.LogInformation("✓ Qwen tools: {Included} included, {Skipped} skipped (IncludedTools filter + MCP filter)", qwenToolCount, qwenSkippedCount);
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
                            WriteElementWithoutNested(w, prop.Value);
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

                logger.LogInformation("Forwarding to LM Studio /v1/chat/completions: {From} -> {To}", model, modelRoute.ToModel);
            }

            var responseBuffering = context.Features
                .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            responseBuffering?.DisableBuffering();

            try
            {
                logger.LogInformation("Sending request to LM Studio: {Url}", targetUrl);
                using var lmResp = await httpClient.SendAsync(proxyReq, HttpCompletionOption.ResponseHeadersRead);

                logger.LogInformation("<- {StatusCode} from local LLM, content type: {ContentType}, length: {Length}",
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
                        logger.LogWarning("LM Studio context overflow — conversation too long for {Model}. Run /compact in Claude Code to reduce context, or increase the model context length in LM Studio.", modelRoute.ToModel);
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Context too long for local model. Run /compact in Claude Code to reduce context, or load the model with a larger context window in LM Studio.\"}}");
                    }
                    else
                    {
                        logger.LogWarning("<- {StatusCode} from local LLM: {Error}", (int)lmResp.StatusCode, errorBody);
                        context.Response.StatusCode = (int)lmResp.StatusCode;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(errorBody);
                    }
                    return;
                }

                logger.LogInformation("Starting response handling");

                // Resolve and use the appropriate handler for the target model
                var handler = context.RequestServices.GetRequiredKeyedService<ILocalLLMResponseHandler>(modelRoute.ToModel);
                await handler.HandleResponseAsync(context, lmResp, modelRoute.ToModel, logger);

                logger.LogInformation("<- 200 POST {Path} [Local LLM via /api/v1/chat] translated to Anthropic SSE", req.Path);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Local LLM connection failed at {Url} — is it running?", localLlmUrl);
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":\"Local LLM unreachable at {localLlmUrl}: {ex.Message}\"}}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during LM Studio response handling");
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

        var responseBufferingFeature = context.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        responseBufferingFeature?.DisableBuffering();

        logger.LogInformation("Anthropic request: {Method} {Path} | Content-Length: {ContentLength}",
            req.Method, req.Path, req.ContentLength);

        await next();

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
    });

    app.MapOpenApi();
    app.MapScalarApiReference();

    app.MapGet("/health", () => Results.Content("{\"status\":\"ok\",\"target\":\"https://api.anthropic.com\"}", "application/json"))
        .WithName("Health")
        .WithSummary("Health check")
        .WithDescription("Returns proxy status and upstream target.")
        .WithTags("Proxy");

    app.MapGet("/logins", (ILiteDatabase db) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        var logins = col.FindAll().Select(u => new
        {
            Token = u.BearerToken.Length > 20
                ? u.BearerToken[..10] + "..." + u.BearerToken[^10..]
                : "***",
            u.Label,
            u.IsActiveInbound,
            u.Utilization5h,
            u.Utilization7d,
            Reset5h = u.Reset5h.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(u.Reset5h.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            Reset7d = u.Reset7d.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(u.Reset7d.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            u.LastUsedUtc,
            u.CreatedUtc
        }).ToList();
        return Results.Ok(logins);
    })
        .WithName("ListLogins")
        .WithSummary("List tracked keys")
        .WithDescription("Returns all tracked keys with masked token, label, remaining tokens, and last used time.")
        .WithTags("Logins");

    app.MapPatch("/logins/{bearerToken}/label", (string bearerToken, LabelRequest body, ILiteDatabase db) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        var user = col.FindById(bearerToken);
        if (user is null)
            return Results.NotFound(new { error = $"No user found for that token" });

        user.Label = body.Label;
        col.Update(user);
        return Results.Ok(new { user.BearerToken, user.Label, user.Email });
    })
        .WithName("LabelLogin")
        .WithSummary("Label a login token")
        .WithDescription("Assigns a friendly name (e.g. 'company', 'personal') to a tracked bearer token.")
        .WithTags("Logins");

    app.MapPost("/override/{identifier}", (string identifier, ILiteDatabase db, IMemoryCache cache) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        var user = col.FindOne(x => x.Email == identifier || x.Label == identifier);
        if (user is null)
            return Results.NotFound(new { error = $"No user found for '{identifier}'" });

        var session = new ActiveSession
        {
            Email = user.Email!,
            BearerToken = user.BearerToken,
            ApiKey = user.ApiKey,
            AnthropicVersion = user.AnthropicVersion ?? "2023-06-01",
            ActivatedUtc = DateTime.UtcNow
        };
        cache.Set("active_session", session);
        var masked = session.BearerToken.Length > 20
            ? session.BearerToken[..10] + "..." + session.BearerToken[^10..]
            : "***";
        return Results.Ok(new { session.Email, token = masked, session.AnthropicVersion, session.ActivatedUtc });
    })
        .WithName("SetOverride")
        .WithSummary("Override active session")
        .WithDescription("Loads the user's auth headers into memory cache. Resolves by email or label (e.g. 'company', 'personal'). All proxied requests use this session until cleared.")
        .WithTags("Override");

    app.MapGet("/override", (IMemoryCache cache, ILiteDatabase db) =>
    {
        if (!cache.TryGetValue<ActiveSession>("active_session", out var session) || session is null)
            return Results.NotFound(new { error = "No override session set" });

        var col = db.GetCollection<UserRecord>("users");
        var user = col.FindById(session.BearerToken);

        var masked = session.BearerToken.Length > 20
            ? session.BearerToken[..10] + "..." + session.BearerToken[^10..]
            : "***";
        return Results.Ok(new
        {
            session.Email,
            Label = user?.Label,
            Token = masked,
            session.AnthropicVersion,
            session.ActivatedUtc,
            user?.Utilization5h,
            user?.Utilization7d,
            Reset5h = user?.Reset5h.HasValue == true
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset5h!.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            Reset7d = user?.Reset7d.HasValue == true
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset7d!.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null
        });
    })
        .WithName("GetOverride")
        .WithSummary("Get current override session with usage")
        .WithDescription("Returns the currently cached override session with label, utilization, and rate limit resets from the database. 404 if no override is active.")
        .WithTags("Override");

    app.MapDelete("/override", (IMemoryCache cache) =>
    {
        cache.Remove("active_session");
        return Results.Ok(new { status = "override cleared" });
    })
        .WithName("ClearOverride")
        .WithSummary("Clear override session")
        .WithDescription("Removes the override session from memory cache. Proxy returns to pass-through mode using the inbound request's own credentials.")
        .WithTags("Override");

    app.MapGet("/current", (ILiteDatabase db) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        var user = col.FindOne(u => u.IsActiveInbound);
        if (user is null)
            return Results.NotFound(new { error = "No active inbound token" });

        var masked = user.BearerToken.Length > 20
            ? user.BearerToken[..10] + "..." + user.BearerToken[^10..]
            : "***";
        return Results.Ok(new
        {
            Token = masked,
            user.Label,
            user.IsActiveInbound,
            user.Utilization5h,
            user.Utilization7d,
            Reset5h = user.Reset5h.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset5h.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            Reset7d = user.Reset7d.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset7d.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            user.LastUsedUtc,
            user.CreatedUtc
        });
    })
        .WithName("GetCurrentInbound")
        .WithSummary("Get the current inbound token")
        .WithDescription("Returns the token currently marked as the active inbound — the one whose credentials are being used if no session override is set.")
        .WithTags("Usage");

    app.MapGet("/usage", (HttpContext context, IMemoryCache cache, ILiteDatabase db) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        string? token = null;

        if (cache.TryGetValue<ActiveSession>("active_session", out var session) && session is not null)
        {
            token = session.BearerToken;
        }
        else if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            token = authHeader.ToString().Replace("Bearer ", "");
        }

        UserRecord? user = null;
        if (!string.IsNullOrEmpty(token))
            user = col.FindById(token);

        // Fallback: most recently created token
        user ??= col.FindAll().OrderByDescending(u => u.CreatedUtc).FirstOrDefault();

        if (user is null)
            return Results.NotFound(new { error = "No tracked tokens" });

        return Results.Ok(new
        {
            user.Label,
            user.IsActiveInbound,
            user.Utilization5h,
            user.Utilization7d,
            Reset5h = user.Reset5h.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset5h.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            Reset7d = user.Reset7d.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(user.Reset7d.Value).ToString("yyyy-MM-dd HH:mm:ss UTC")
                : null,
            user.LastUsedUtc,
            user.CreatedUtc
        });
    })
        .WithName("GetUsage")
        .WithSummary("Get usage for current session")
        .WithDescription("Returns utilization and rate limit data for whoever is currently being proxied — the active session override if set, otherwise the inbound bearer token.")
        .WithTags("Usage");

    app.MapGet("/override-model", (IMemoryCache cache) =>
    {
        var settings = cache.Get<ModelRouteSettings>("model_route_settings") ?? new ModelRouteSettings();
        return Results.Ok(new
        {
            settings.Enabled,
            settings.FromModel,
            settings.FromPrompt,
            settings.ToModel,
            Target = localLlmUrl
        });
    })
        .WithName("GetModelRoute")
        .WithSummary("Get LM Studio model routing settings")
        .WithDescription("Returns the current model routing configuration. When enabled, requests matching FromModel are forwarded to LM Studio. If ToModel is set, the model field in the request body is rewritten.")
        .WithTags("Model Routing");

    app.MapPost("/override-model", (ModelRouteRequest body, IMemoryCache cache) =>
    {
        var settings = cache.Get<ModelRouteSettings>("model_route_settings") ?? new ModelRouteSettings();
        if (body.Enabled.HasValue) settings.Enabled = body.Enabled.Value;
        if (body.FromModel is not null) settings.FromModel = body.FromModel;
        if (body.FromPrompt is not null) settings.FromPrompt = body.FromPrompt;
        if (body.ToModel is not null) settings.ToModel = body.ToModel;
        cache.Set("model_route_settings", settings);
        return Results.Ok(new
        {
            settings.Enabled,
            settings.FromModel,
            settings.FromPrompt,
            settings.ToModel,
            Target = localLlmUrl
        });
    })
        .WithName("SetModelRoute")
        .WithSummary("Update LM Studio model routing settings")
        .WithDescription("Updates the model routing config. Set FromModel to the pattern to intercept (e.g. 'Haiku'). Set ToModel to rewrite the model field in the request body (e.g. a model loaded in LM Studio). Set Enabled to false to disable routing. Send empty string for ToModel to clear it.")
        .WithTags("Model Routing");

    app.MapDelete("/override-model", (IMemoryCache cache) =>
    {
        cache.Set("model_route_settings", new ModelRouteSettings());
        return Results.Ok(new { status = "model routing reset to defaults", Enabled = true, FromModel = "Haiku", ToModel = "qwen/qwen2.5-coder-14b" });
    })
        .WithName("ResetModelRoute")
        .WithSummary("Reset model routing to defaults")
        .WithDescription("Resets model routing settings to defaults: enabled=true, fromModel=Haiku, toModel=null.")
        .WithTags("Model Routing");

    app.MapReverseProxy();

    Log.Information("Claude proxy starting on http://+:5066");
    Log.Information("Workspace: {Workspace}", workspace);
    Log.Information("Database: {DbPath}", dbPath);
    Log.Information("Logs: {LogPath}", logPath);
    Log.Information("Local LLM: {LocalLlmUrl} (routing '{FromModel}' -> '{ToModel}')",
        localLlmUrl, modelRouteDefaults.FromModel, modelRouteDefaults.ToModel);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Proxy terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static (string? email, string? name) TryDecodeJwt(string token, MEL.ILogger logger)
{
    try
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            return (null, null);

        var payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString()
                 : root.TryGetProperty("sub", out var s) ? s.GetString()
                 : null;

        if (email is null)
            logger.LogInformation("JWT claims (no email found): {Claims}", json);

        return (email, name);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "JWT decode failed");
        return (null, null);
    }
}

public class UserRecord
{
    [BsonId]
    public string BearerToken { get; set; } = default!;
    public string? Email { get; set; }
    public string? Label { get; set; }
    public string? ApiKey { get; set; }
    public string? AnthropicVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool IsActiveInbound { get; set; }
    public double? Utilization5h { get; set; }
    public double? Utilization7d { get; set; }
    public long? Reset5h { get; set; }
    public long? Reset7d { get; set; }
}

public class UserUpsertWorker : BackgroundService
{
    private readonly Channel<UserRecord> _channel;
    private readonly ILiteDatabase _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserUpsertWorker> _logger;

    public UserUpsertWorker(
        Channel<UserRecord> channel,
        ILiteDatabase db,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<UserUpsertWorker> logger)
    {
        _channel = channel;
        _db = db;
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var col = _db.GetCollection<UserRecord>("users");
        col.EnsureIndex(x => x.BearerToken, unique: true);
        col.EnsureIndex(x => x.Email);

        await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var existing = col.FindById(record.BearerToken);

                if (existing is not null)
                {
                    var changed = false;
                    var utilizationChanged = false;

                    if (record.Label is not null && record.Label != existing.Label)
                    {
                        existing.Label = record.Label;
                        changed = true;
                    }
                    if (record.Email is not null && record.Email != existing.Email)
                    {
                        existing.Email = record.Email;
                        changed = true;
                    }
                    if (record.Utilization5h is not null
                        && record.Utilization5h != existing.Utilization5h)
                    {
                        existing.Utilization5h = record.Utilization5h;
                        changed = true;
                        utilizationChanged = true;
                    }
                    if (record.Utilization7d is not null
                        && record.Utilization7d != existing.Utilization7d)
                    {
                        existing.Utilization7d = record.Utilization7d;
                        changed = true;
                        utilizationChanged = true;
                    }
                    if (record.Reset5h is not null
                        && record.Reset5h != existing.Reset5h)
                    {
                        existing.Reset5h = record.Reset5h;
                        changed = true;
                    }
                    if (record.Reset7d is not null
                        && record.Reset7d != existing.Reset7d)
                    {
                        existing.Reset7d = record.Reset7d;
                        changed = true;
                    }

                    if (utilizationChanged || existing.LastUsedUtc is null)
                    {
                        existing.LastUsedUtc = DateTime.UtcNow;
                        changed = true;
                    }

                    // Set as active inbound if not already
                    if (!existing.IsActiveInbound)
                    {
                        // Deactivate all others
                        foreach (var other in col.Find(u => u.IsActiveInbound && u.BearerToken != existing.BearerToken))
                        {
                            other.IsActiveInbound = false;
                            col.Update(other);
                        }
                        existing.IsActiveInbound = true;
                        changed = true;

                        // Purge tokens not used in over 1 week
                        var cutoff = DateTime.UtcNow.AddDays(-7);
                        var stale = col.Find(u => u.LastUsedUtc != null && u.LastUsedUtc < cutoff).ToList();
                        foreach (var s in stale)
                        {
                            col.Delete(s.BearerToken);
                            _logger.LogInformation("Purged stale token {Label} (last used {LastUsed})", s.Label, s.LastUsedUtc);
                        }
                    }

                    if (changed)
                        col.Update(existing);
                }
                else
                {
                    // New token — deactivate all others
                    foreach (var other in col.Find(u => u.IsActiveInbound))
                    {
                        other.IsActiveInbound = false;
                        col.Update(other);
                    }

                    if (string.IsNullOrEmpty(record.Label))
                        record.Label = new Bogus.Faker().Name.FullName().ToLowerInvariant().Replace(" ", "-");
                    record.IsActiveInbound = true;
                    record.LastUsedUtc = DateTime.UtcNow;
                    col.Insert(record);
                    _cache.Remove("active_session");
                    _logger.LogInformation("New token tracked as {Label} — active session cleared", record.Label);

                    // Purge tokens not used in over 1 week
                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    var stale = col.Find(u => u.LastUsedUtc != null && u.LastUsedUtc < cutoff).ToList();
                    foreach (var s in stale)
                    {
                        col.Delete(s.BearerToken);
                        _logger.LogInformation("Purged stale token {Label} (last used {LastUsed})", s.Label, s.LastUsedUtc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Insert failed");
            }
        }
    }
}

public class LabelRequest
{
    public string Label { get; set; } = default!;
}

public class ModelRouteSettings
{
    public bool Enabled { get; set; } = true;
    public string FromModel { get; set; } = "Haiku";
    /// <summary>If non-empty, route to local LLM when the first user message starts with this prefix (case-insensitive).</summary>
    public string FromPrompt { get; set; } = "";
    public string ToModel { get; set; } = "liquid/lfm2.5-1.2b";
    /// <summary>Built-in tools to include when forwarding to local LLM. Empty = allow all non-MCP tools.</summary>
    public List<string> IncludedTools { get; set; } = new();
    /// <summary>MCP tool name prefixes to allow for local LLM (starts-with, case-insensitive). Empty = no MCP tools.</summary>
    public List<string> AllowedMcpTools { get; set; } = new();
    /// <summary>Exact MCP tool names to block even if matched by AllowedMcpTools.</summary>
    public List<string> BlockedMcpTools { get; set; } = new();
}

public class ModelRouteRequest
{
    public bool? Enabled { get; set; }
    public string? FromModel { get; set; }
    public string? FromPrompt { get; set; }
    public string? ToModel { get; set; }
}

/// <summary>
/// Determines whether a tool should be forwarded to the local LLM.
/// MCP tools (name starts with "mcp__") are allowed only if their server is in allowedMcpServers.
/// Built-in tools are allowed only if they are in includedTools (empty list = allow all built-ins).
/// This filter applies exclusively to the local LLM path — the Anthropic path is always pass-through.
/// </summary>
/// <summary>
/// When Claude Code truncates a large tool result and saves it to disk, the tool result
/// content contains a &lt;persisted-output&gt; block with the file path. This helper reads
/// the file and replaces the block with the actual content so Qwen sees the full result.
/// </summary>
public static class PersistedOutputResolver
{
    public static async Task<string> ResolveAsync(string text)
    {
        const string openTag = "<persisted-output>";
        const string closeTag = "</persisted-output>";

        if (!text.Contains(openTag)) return text;

        var start = text.IndexOf(openTag);
        var end = text.IndexOf(closeTag);
        if (end < 0) return text;

        var inner = text[(start + openTag.Length)..end];

        const string marker = "Full output saved to: ";
        var markerIdx = inner.IndexOf(marker);
        if (markerIdx < 0) return text;

        var pathStart = markerIdx + marker.Length;
        var pathEnd = inner.IndexOf('\n', pathStart);
        var filePath = (pathEnd >= 0 ? inner[pathStart..pathEnd] : inner[pathStart..]).Trim();

        if (!File.Exists(filePath)) return text;

        try
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            // File is a JSON array of content blocks — extract text values
            using var doc = JsonDocument.Parse(fileContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in doc.RootElement.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        block.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                }
                return text[..start] + sb.ToString() + text[(end + closeTag.Length)..];
            }
            return text[..start] + fileContent + text[(end + closeTag.Length)..];
        }
        catch
        {
            return text;
        }
    }
}

public static class LocalLlmToolFilter
{
    public static bool IsAllowed(string toolName, IList<string> includedTools, IList<string> allowedMcpTools, IList<string> blockedMcpTools)
    {
        if (toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            if (allowedMcpTools.Count == 0) return false;
            // Each entry is a case-insensitive prefix: "mcp__conductor" allows all conductor tools
            if (!allowedMcpTools.Any(s => toolName.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return false;
            // Blocked list takes precedence — exact match, case-insensitive
            return !blockedMcpTools.Any(b => toolName.Equals(b, StringComparison.OrdinalIgnoreCase));
        }

        if (includedTools.Count == 0) return true;
        return includedTools.Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Strips Anthropic-specific noise from requests before forwarding to local LLM.
/// Applies only to the local LLM path — Anthropic path is always pass-through.
/// </summary>
public static class LocalLlmRequestFilter
{
    // Tags whose blocks should be dropped entirely from message content
    private static readonly string[] NoiseTags =
        ["system-reminder", "local-command-caveat", "command-name", "local-command-stdout", "available-deferred-tools"];

    /// <summary>Returns true if a system-array text block is Anthropic infrastructure noise.</summary>
    public static bool IsSystemNoise(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("x-anthropic-billing-header:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("You are Claude Code, Anthropic", StringComparison.Ordinal);
    }

    /// <summary>Returns true if a message content text block is purely Anthropic metadata.</summary>
    public static bool IsMessageNoise(string text)
    {
        var t = text.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        foreach (var tag in NoiseTags)
            if (t.StartsWith($"<{tag}>") || t.StartsWith($"<{tag} "))
                return true;
        return false;
    }

    /// <summary>Strips system-reminder XML tags from a text string (inline clean-up).</summary>
    public static string StripInlineNoise(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"<system-reminder>.*?</system-reminder>\n*", "",
            System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

    /// <summary>
    /// Strips mermaid diagrams and the Changelog section from CLAUDE.md-style system content.
    /// Keeps Non-Negotiables, Key Behaviors, API Endpoints, and other task-relevant sections.
    /// </summary>
    public static string TrimSystemContent(string text)
    {
        // Remove mermaid diagrams (visual, no value as text)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"```mermaid.*?```", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        // Remove Changelog section to end (it's always last and never useful for task execution)
        var changelogIdx = text.IndexOf("\n## Changelog", StringComparison.Ordinal);
        if (changelogIdx > 0) text = text[..changelogIdx];
        return text.Trim();
    }

    /// <summary>
    /// Writes a slim OpenAI-format tool definition from an Anthropic-format tool element.
    /// Strips verbose descriptions and optional parameter details to reduce token overhead.
    /// </summary>
    public static void WriteSlimTool(System.Text.Json.Utf8JsonWriter w, JsonElement anthropicTool)
    {
        w.WriteStartObject();
        w.WriteString("type", "function");
        w.WritePropertyName("function");
        w.WriteStartObject();

        if (anthropicTool.TryGetProperty("name", out var name))
            { w.WritePropertyName("name"); name.WriteTo(w); }

        // Truncate description to first line, max 120 chars
        if (anthropicTool.TryGetProperty("description", out var desc))
        {
            var d = desc.GetString() ?? "";
            var nl = d.IndexOf('\n');
            if (nl > 0) d = d[..nl].Trim();
            if (d.Length > 120) { var dot = d.IndexOf(". "); d = dot > 0 ? d[..(dot + 1)] : d[..120]; }
            w.WriteString("description", d);
        }

        if (anthropicTool.TryGetProperty("input_schema", out var schema))
        {
            w.WritePropertyName("parameters");
            WriteSlimSchema(w, schema);
        }

        w.WriteEndObject(); // function
        w.WriteEndObject(); // tool
    }

    private static void WriteSlimSchema(System.Text.Json.Utf8JsonWriter w, JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        JsonElement reqElem = default;
        if (schema.TryGetProperty("required", out reqElem) && reqElem.ValueKind == JsonValueKind.Array)
            foreach (var r in reqElem.EnumerateArray())
                if (r.GetString() is string s) required.Add(s);

        w.WriteStartObject();
        w.WriteString("type", "object");

        if (schema.TryGetProperty("properties", out var props))
        {
            w.WritePropertyName("properties");
            w.WriteStartObject();
            foreach (var prop in props.EnumerateObject())
            {
                w.WritePropertyName(prop.Name);
                w.WriteStartObject();
                if (prop.Value.TryGetProperty("type", out var t)) { w.WritePropertyName("type"); t.WriteTo(w); }
                if (prop.Value.TryGetProperty("enum", out var e)) { w.WritePropertyName("enum"); e.WriteTo(w); }
                // Description only for required params, truncated to 80 chars
                if (required.Contains(prop.Name) && prop.Value.TryGetProperty("description", out var d))
                {
                    var dt = d.GetString() ?? "";
                    var nl = dt.IndexOf('\n'); if (nl > 0) dt = dt[..nl].Trim();
                    if (dt.Length > 80) dt = dt[..80];
                    w.WriteString("description", dt);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        if (reqElem.ValueKind == JsonValueKind.Array) { w.WritePropertyName("required"); reqElem.WriteTo(w); }
        w.WriteEndObject();
    }
}

// Tool translator: converts Liquid format tool calls to Anthropic format
public static class LiquidToolTranslator
{
    private static readonly Dictionary<string, ToolDefinition> SupportedTools = new()
    {
        { "bash", new ToolDefinition { Name = "bash", Description = "Execute shell command", Params = new[] { "command" } } },
        { "read", new ToolDefinition { Name = "read", Description = "Read file content", Params = new[] { "file_path" } } },
        { "glob", new ToolDefinition { Name = "glob", Description = "Find files by pattern", Params = new[] { "pattern" } } },
        { "grep", new ToolDefinition { Name = "grep", Description = "Search file content", Params = new[] { "pattern" } } },
        { "edit", new ToolDefinition { Name = "edit", Description = "Edit file", Params = new[] { "file_path", "old_string", "new_string" } } },
        { "write", new ToolDefinition { Name = "write", Description = "Write file", Params = new[] { "file_path", "content" } } },
    };

    public class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] Params { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Converts Liquid format: tool_name(param1="value1", param2="value2")
    /// To Anthropic format tool_use block
    /// </summary>
    public static string? TryParseAndConvertToolCall(string liquidFormat)
    {
        // Parse: tool_name(params...)
        var match = System.Text.RegularExpressions.Regex.Match(
            liquidFormat,
            @"^(\w+)\((.*)\)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!match.Success)
            return null;

        var toolName = match.Groups[1].Value.ToLowerInvariant();
        var paramsStr = match.Groups[2].Value;

        if (!SupportedTools.TryGetValue(toolName, out var toolDef))
            return null;

        // Parse parameters: key="value", key2="value2"
        var input = new Dictionary<string, object>();
        var paramPattern = @"(\w+)=""([^""]*)""";
        var paramMatches = System.Text.RegularExpressions.Regex.Matches(paramsStr, paramPattern);

        foreach (System.Text.RegularExpressions.Match m in paramMatches)
        {
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            input[key] = value;
        }

        // Build Anthropic tool_use block
        var toolId = $"toolu_{Guid.NewGuid():N}".Substring(0, 24); // Mimic Anthropic ID format
        var toolUse = new
        {
            type = "tool_use",
            id = toolId,
            name = toolName,
            input
        };

        return System.Text.Json.JsonSerializer.Serialize(toolUse);
    }
}

public class ActiveSession
{
    public string Email { get; set; } = default!;
    public string BearerToken { get; set; } = default!;
    public string? ApiKey { get; set; }
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public DateTime ActivatedUtc { get; set; }
}

// Required for ILogger<Program> in top-level statements
public partial class Program { }

[System.Text.Json.Serialization.JsonSerializable(typeof(List<UserRecord>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UserRecord))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ActiveSession))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LabelRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ModelRouteSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ModelRouteRequest))]
internal partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
