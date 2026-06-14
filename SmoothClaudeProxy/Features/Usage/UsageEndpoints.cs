using LiteDB;
using Microsoft.Extensions.Caching.Memory;
using SmoothClaudeProxy.Features.Sessions;
using SmoothClaudeProxy.Features.Logins;

namespace SmoothClaudeProxy.Features.Usage;

public static class UsageEndpoints
{
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}
