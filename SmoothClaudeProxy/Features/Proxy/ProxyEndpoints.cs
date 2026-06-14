namespace SmoothClaudeProxy.Features.Proxy;

public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/", [Microsoft.AspNetCore.Http.HttpMethods.Head, Microsoft.AspNetCore.Http.HttpMethods.Get],
            () => Results.Ok(new { status = "ok" }))
            .ExcludeFromDescription();
    
        app.MapGet("/health", () => Results.Content("{\"status\":\"ok\",\"target\":\"https://api.anthropic.com\"}", "application/json"))
            .WithName("Health")
            .WithSummary("Health check")
            .WithDescription("Returns proxy status and upstream target.")
            .WithTags("Proxy");

        return app;
    }
}
