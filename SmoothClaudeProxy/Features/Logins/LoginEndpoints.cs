using LiteDB;

namespace SmoothClaudeProxy.Features.Logins;

public static class LoginEndpoints
{
    public static IEndpointRouteBuilder MapLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/logins", (ILiteDatabase db) =>
        {
            var col = db.GetCollection<UserRecord>("users");
            var logins = col.FindAll()
                .OrderByDescending(u => u.LastUsedUtc)
                .ThenByDescending(u => u.CreatedUtc)
                .Select(u => new
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
    
        app.MapGet("/logins/{identifier}/token", (string identifier, ILiteDatabase db) =>
        {
            var col = db.GetCollection<UserRecord>("users");
            var user = UserLookup.FindByIdentifier(col, identifier);
            if (user is null)
                return Results.NotFound(new { error = $"No user found for '{identifier}'" });
    
            return Results.Ok(new { Token = user.BearerToken, user.Label, user.Email });
        })
            .WithName("GetLoginToken")
            .WithSummary("Get an unmasked login token")
            .WithDescription("Returns the full bearer token for a tracked login. Resolves by exact token, email, or label.")
            .WithTags("Logins");

        return app;
    }
}
