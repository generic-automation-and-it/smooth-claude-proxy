using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace SmoothClaudeProxy.Features.ModelRouting;

public static class ModelRoutingEndpoints
{
    public static IEndpointRouteBuilder MapModelRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/override-model", (IMemoryCache cache, IOptions<LlmServiceOptions> llm) =>
        {
            var settings = cache.Get<ModelRouteSettings>("model_route_settings") ?? new ModelRouteSettings();
            return Results.Ok(new
            {
                settings.Enabled,
                settings.ApiFormat,
                settings.StripNonClaudeModels,
                settings.FableModel,
                settings.OpusModel,
                settings.SonnetModel,
                settings.HaikuModel,
                Target = llm.Value.BaseUrl
            });
        })
            .WithName("GetModelRoute")
            .WithSummary("Get model routing settings")
            .WithDescription("Returns the current model routing configuration. When enabled, models that do not start with 'claude-' are forwarded to the configured alternate upstream.")
            .WithTags("Model Routing");
    
        app.MapPost("/override-model", (ModelRouteRequest body, IMemoryCache cache, IOptions<LlmServiceOptions> llm) =>
        {
            var settings = cache.Get<ModelRouteSettings>("model_route_settings") ?? new ModelRouteSettings();
            if (body.Enabled.HasValue) settings.Enabled = body.Enabled.Value;
            if (body.ApiFormat is not null) settings.ApiFormat = body.ApiFormat;
            if (body.StripNonClaudeModels.HasValue) settings.StripNonClaudeModels = body.StripNonClaudeModels.Value;
            cache.Set("model_route_settings", settings);
            return Results.Ok(new
            {
                settings.Enabled,
                settings.ApiFormat,
                settings.StripNonClaudeModels,
                Target = llm.Value.BaseUrl
            });
        })
            .WithName("SetModelRoute")
            .WithSummary("Update model routing settings")
            .WithDescription("Updates the model routing config. Set ApiFormat to 'anthropic' for passthrough to a /v1/messages-compatible upstream or 'openai' for Anthropic-to-OpenAI conversion. Set Enabled to false to disable routing.")
            .WithTags("Model Routing");
    
        app.MapDelete("/override-model", (IMemoryCache cache) =>
        {
            var defaults = new ModelRouteSettings();
            cache.Set("model_route_settings", defaults);
            return Results.Ok(new
            {
                status = "model routing reset to defaults",
                defaults.Enabled,
                defaults.ApiFormat,
                defaults.StripNonClaudeModels
            });
        })
            .WithName("ResetModelRoute")
            .WithSummary("Reset model routing to defaults")
            .WithDescription("Resets model routing settings to defaults: enabled=true, apiFormat=anthropic.")
            .WithTags("Model Routing");

        return app;
    }
}
