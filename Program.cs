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
using Serilog;
using MEL = Microsoft.Extensions.Logging;

var workspace = Environment.GetEnvironmentVariable("WORKSPACE_PATH") ?? "/data";
var dbPath = Path.Combine(workspace, "claude-auth.db");
var logPath = Path.Combine(workspace, "logs", "claude-proxy-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Services.Configure<KestrelServerOptions>(options =>
    {
        options.AllowSynchronousIO = true;
    });

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    builder.Services.AddHttpClient();
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

        if (Environment.GetEnvironmentVariable("LOG_TOKEN_FORMAT") == "true" && bearerToken is not null)
        {
            var parts = bearerToken.Split('.');
            var masked = bearerToken.Length > 20 ? bearerToken[..10] + "..." + bearerToken[^10..] : "***";
            logger.LogInformation("[token-debug] format: {Parts} parts, length: {Length}, preview: {Preview}",
                parts.Length, bearerToken.Length, masked);
            logger.LogInformation("[token-debug] type: {Type}",
                parts.Length >= 2 ? "JWT (header.payload.signature)" : "opaque token");
        }

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, user={User}]",
            req.Method, req.Path, req.QueryString, authType, email ?? "unknown");

        if (bearerToken is not null)
        {
            var channel = context.RequestServices.GetRequiredService<Channel<UserRecord>>();
            await channel.Writer.WriteAsync(new UserRecord
            {
                BearerToken = bearerToken,
                Email = email,
                Name = name,
                ApiKey = apiKey,
                AnthropicVersion = anthropicVersion,
                LastUsedUtc = DateTime.UtcNow
            });
        }

        var responseBufferingFeature = context.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        responseBufferingFeature?.DisableBuffering();

        await next();

        logger.LogInformation("<- {StatusCode} {Method} {Path}",
            context.Response.StatusCode, req.Method, req.Path);
    });

    app.MapGet("/health", () => Results.Content("{\"status\":\"ok\",\"target\":\"https://api.anthropic.com\"}", "application/json"));

    app.MapGet("/users", (ILiteDatabase db) =>
    {
        var col = db.GetCollection<UserRecord>("users");
        var users = col.FindAll().ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(users, AppJsonContext.Default.ListUserRecord);
        return Results.Content(json, "application/json");
    });

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
    public string? Name { get; set; }
    public string? ApiKey { get; set; }
    public string? AnthropicVersion { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

public class UserUpsertWorker : BackgroundService
{
    private readonly Channel<UserRecord> _channel;
    private readonly ILiteDatabase _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserUpsertWorker> _logger;

    public UserUpsertWorker(
        Channel<UserRecord> channel,
        ILiteDatabase db,
        IServiceScopeFactory scopeFactory,
        ILogger<UserUpsertWorker> logger)
    {
        _channel = channel;
        _db = db;
        _scopeFactory = scopeFactory;
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
                // Already tracked — skip
                if (col.FindById(record.BearerToken) is not null)
                    continue;

                // No email from JWT — try resolving from Anthropic /me
                if (record.Email is null)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                    var http = httpFactory.CreateClient();
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/me");
                    req.Headers.Add("Authorization", $"Bearer {record.BearerToken}");
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    var resp = await http.SendAsync(req, stoppingToken);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(stoppingToken);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        record.Email = root.TryGetProperty("email", out var em) ? em.GetString() : null;
                        record.Name  = root.TryGetProperty("name",  out var nm) ? nm.GetString() : record.Name;
                        _logger.LogInformation("Resolved user from /me: {Email}", record.Email ?? "unknown");
                    }
                    else
                    {
                        _logger.LogWarning("/me returned {Status} for token", resp.StatusCode);
                    }
                }

                // Dedup: if we now have an email, remove other tokens registered to the same email
                if (record.Email is not null)
                {
                    var duplicates = col.Find(x => x.Email == record.Email).ToList();
                    foreach (var dup in duplicates)
                    {
                        col.Delete(dup.BearerToken);
                        _logger.LogInformation("Removed stale token for {Email}", record.Email);
                    }
                }

                col.Insert(record);
                _logger.LogInformation("Inserted user: {Email}", record.Email ?? "unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Insert failed");
            }
        }
    }
}

// Required for ILogger<Program> in top-level statements
public partial class Program { }

[System.Text.Json.Serialization.JsonSerializable(typeof(List<UserRecord>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(UserRecord))]
internal partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
