using LiteDB;
using Microsoft.Extensions.Caching.Memory;
using SmoothClaudeProxy.Features.Logins;

namespace SmoothClaudeProxy.Features.Sessions;

public static class OverrideEndpoints
{
    public static IEndpointRouteBuilder MapOverrideEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/override/{identifier}", (string identifier, ILiteDatabase db, IMemoryCache cache) =>
        {
            var col = db.GetCollection<UserRecord>("users");
            var user = UserLookup.FindByIdentifier(col, identifier);
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

        return app;
    }
}
