using LiteDB;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Scalar.AspNetCore;
using Serilog;
using SmoothClaudeProxy.Features.Logins;
using SmoothClaudeProxy.Features.ModelRouting;
using SmoothClaudeProxy.Features.Proxy;
using SmoothClaudeProxy.Features.Sessions;
using SmoothClaudeProxy.Features.Usage;
using SmoothClaudeProxy.Infrastructure;

// Bootstrap paths — resolved before the host so Serilog and LiteDB can use them.
var workspace = Environment.GetEnvironmentVariable("WORKSPACE_PATH") ?? "/data";
var dbPath = Path.Combine(workspace, "claude-auth.db");
var logPath = Path.Combine(workspace, "logs", "claude-proxy-.log");
var llmLogPath = Path.Combine(workspace, "logs", "llm-service-.log");
var toolsLogPath = Path.Combine(workspace, "logs", "tools-log.log"); // Non-rolling tools log

Log.Logger = SerilogSetup.Build(logPath, llmLogPath, toolsLogPath);

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Feature: model routing — binds LlmService config (appsettings.json + env) and handlers.
    builder.AddModelRouting();

    // Shared infrastructure.
    builder.Services.Configure<KestrelServerOptions>(o => o.AllowSynchronousIO = true);
    builder.Services.AddOpenApi();
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
    builder.Services.AddHttpClient();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ILiteDatabase>(_ => new LiteDatabase(dbPath));

    // Feature: user tracking — async DB-write channel + background worker.
    builder.Services.AddUserTracking();

    // Feature: proxy forwarding — the core request middleware.
    builder.Services.AddTransient<ProxyForwardingMiddleware>();

    var app = builder.Build();

    // Seed the mutable runtime routing settings from the bound startup configuration.
    app.SeedModelRouteSettings();

    app.UseMiddleware<ProxyForwardingMiddleware>();

    app.MapOpenApi();
    app.MapScalarApiReference();

    // Feature endpoints (registered before MapReverseProxy so they take precedence).
    app.MapProxyEndpoints();
    app.MapLoginEndpoints();
    app.MapOverrideEndpoints();
    app.MapUsageEndpoints();
    app.MapModelRoutingEndpoints();

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

// Required for ILogger<Program> / WebApplicationFactory in top-level statements.
public partial class Program { }
