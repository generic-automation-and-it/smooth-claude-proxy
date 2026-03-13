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

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Async(a => a.Console())
    .WriteTo.Async(a => a.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7))
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

        string? model = null;
        if (req.ContentType?.Contains("application/json") == true && req.ContentLength > 0)
        {
            req.EnableBuffering();
            try
            {
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

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, model={Model}]",
            req.Method, req.Path, req.QueryString, authType, model ?? "-");

        var memCache = context.RequestServices.GetRequiredService<IMemoryCache>();
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

    app.MapReverseProxy();

    Log.Information("Claude proxy starting on http://+:5066");
    Log.Information("Workspace: {Workspace}", workspace);
    Log.Information("Database: {DbPath}", dbPath);
    Log.Information("Logs: {LogPath}", logPath);

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
internal partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
