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
        ToModel = localLlmConfig.GetValue("ToModel", "qwen/qwen2.5-coder-14b")!
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

        // Only read body if model routing is enabled (to extract model field)
        string? model = null;
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

        var isLmStudioRoute = modelRoute.Enabled
            && model?.Contains(modelRoute.FromModel, StringComparison.OrdinalIgnoreCase) == true;
        var routeTarget = isLmStudioRoute ? "LM Studio" : "Anthropic";

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, model={Model}, route={Route}]",
            req.Method, req.Path, req.QueryString, authType, model ?? "-", routeTarget);

        // Route matching models to local LLM via LM Studio native chat API
        if (isLmStudioRoute)
        {
            var httpFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            using var httpClient = httpFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            // Target LM Studio native /api/v1/chat endpoint
            var targetUrl = $"{localLlmUrl.TrimEnd('/')}/api/v1/chat";
            using var proxyReq = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            proxyReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {localLlmToken}");

            // Transform Anthropic Messages request → LM Studio chat request
            if (req.ContentLength > 0 || req.ContentType is not null)
            {
                req.Body.Position = 0;
                using var bodyReader = new StreamReader(req.Body, leaveOpen: true);
                var bodyText = await bodyReader.ReadToEndAsync();
                using var bodyDoc = JsonDocument.Parse(bodyText);
                var root = bodyDoc.RootElement;

                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("model", modelRoute.ToModel);
                    w.WriteBoolean("stream", true);

                    if (root.TryGetProperty("max_tokens", out var maxTok))
                        w.WriteNumber("max_output_tokens", maxTok.GetInt32());
                    if (root.TryGetProperty("temperature", out var temp))
                        w.WriteNumber("temperature", temp.GetDouble());
                    if (root.TryGetProperty("top_p", out var topP))
                        w.WriteNumber("top_p", topP.GetDouble());

                    // Extract system prompt
                    string? systemPrompt = null;
                    if (root.TryGetProperty("system", out var sys))
                    {
                        if (sys.ValueKind == JsonValueKind.String)
                        {
                            systemPrompt = sys.GetString();
                        }
                        else if (sys.ValueKind == JsonValueKind.Array)
                        {
                            // Anthropic system can be array of {type:"text", text:"..."}
                            var sb = new StringBuilder();
                            foreach (var block in sys.EnumerateArray())
                            {
                                if (block.TryGetProperty("text", out var t))
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(t.GetString());
                                }
                            }
                            systemPrompt = sb.ToString();
                        }
                    }
                    if (systemPrompt is not null)
                        w.WriteString("system_prompt", systemPrompt);

                    // Build LM Studio input array from Anthropic messages
                    // Convert messages to flat list of text content blocks
                    w.WriteStartArray("input");

                    if (root.TryGetProperty("messages", out var msgs))
                    {
                        foreach (var msg in msgs.EnumerateArray())
                        {
                            if (msg.TryGetProperty("content", out var content))
                            {
                                if (content.ValueKind == JsonValueKind.String)
                                {
                                    // Simple text content
                                    w.WriteStartObject();
                                    w.WriteString("type", "text");
                                    w.WriteString("text", content.GetString());
                                    w.WriteEndObject();
                                }
                                else if (content.ValueKind == JsonValueKind.Array)
                                {
                                    // Content block array — extract all text blocks
                                    foreach (var block in content.EnumerateArray())
                                    {
                                        if (block.TryGetProperty("type", out var bt)
                                            && bt.GetString() == "text"
                                            && block.TryGetProperty("text", out var txt))
                                        {
                                            w.WriteStartObject();
                                            w.WriteString("type", "text");
                                            w.WriteString("text", txt.GetString());
                                            w.WriteEndObject();
                                        }
                                    }
                                }
                            }
                        }
                    }

                    w.WriteEndArray(); // input
                    w.WriteEndObject();
                }

                var payload = ms.ToArray();
                proxyReq.Content = new ByteArrayContent(payload);
                proxyReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                logger.LogInformation("Translated Anthropic -> LM Studio chat: {From} -> {To}", model, modelRoute.ToModel);
            }

            var responseBuffering = context.Features
                .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            responseBuffering?.DisableBuffering();

            try
            {
                using var lmResp = await httpClient.SendAsync(proxyReq, HttpCompletionOption.ResponseHeadersRead);

                if (!lmResp.IsSuccessStatusCode)
                {
                    var errorBody = await lmResp.Content.ReadAsStringAsync();
                    logger.LogWarning("<- {StatusCode} from local LLM: {Error}", (int)lmResp.StatusCode, errorBody);
                    context.Response.StatusCode = (int)lmResp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(errorBody);
                    return;
                }

                // Translate LM Studio SSE stream → Anthropic SSE stream
                var msgId = $"msg_{Guid.NewGuid():N}";
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream; charset=utf-8";

                // Anthropic message_start envelope
                await context.Response.WriteAsync($"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"{msgId}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"{modelRoute.ToModel}\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}\n\n");
                await context.Response.WriteAsync("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
                await context.Response.Body.FlushAsync();

                await using var stream = await lmResp.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (!line.StartsWith("data: ")) continue;

                    var data = line["data: ".Length..];
                    if (data == "[DONE]")
                    {
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
                            var text = textElem.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                // Escape for JSON
                                var escaped = System.Text.Json.JsonSerializer.Serialize(text)[1..^1]; // strip surrounding quotes
                                await context.Response.WriteAsync($"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{escaped}\"}}}}\n\n");
                                await context.Response.Body.FlushAsync();
                            }
                        }
                    }
                    catch { /* skip unparseable chunks */ }
                }

                logger.LogInformation("<- 200 POST {Path} [Local LLM via /api/v1/chat]", req.Path);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "Local LLM connection failed at {Url} — is it running?", localLlmUrl);
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":\"Local LLM unreachable at {localLlmUrl}: {ex.Message}\"}}");
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
        if (body.ToModel is not null) settings.ToModel = body.ToModel;
        cache.Set("model_route_settings", settings);
        return Results.Ok(new
        {
            settings.Enabled,
            settings.FromModel,
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
    public string ToModel { get; set; } = "qwen/qwen2.5-coder-14b";
}

public class ModelRouteRequest
{
    public bool? Enabled { get; set; }
    public string? FromModel { get; set; }
    public string? ToModel { get; set; }
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
