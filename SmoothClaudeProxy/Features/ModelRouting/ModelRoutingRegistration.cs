using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SmoothClaudeProxy.Features.ModelRouting;

/// <summary>Composition root wiring for the model-routing feature.</summary>
public static class ModelRoutingRegistration
{
    /// <summary>
    /// Binds <see cref="LlmServiceOptions"/> from the standard configuration pipeline and
    /// registers the keyed LLM response handlers. The well-known flat env vars are bridged
    /// onto their canonical <c>LlmService</c> keys (added last → highest precedence; only set
    /// vars are included, so appsettings.json stays the fallback).
    /// </summary>
    public static WebApplicationBuilder AddModelRouting(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmService:BaseUrl"] = Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL"),
            ["LlmService:AuthToken"] = GetFirstNonEmptyEnvironmentVariable("OPENCODE_API_KEY", "LLMSERVICE_API_KEY"),
            ["LlmService:claude_fable_default_model"] = Environment.GetEnvironmentVariable("CLAUDE_FABLE_DEFAULT_MODEL"),
            ["LlmService:claude_opus_default_model"] = Environment.GetEnvironmentVariable("CLAUDE_OPUS_DEFAULT_MODEL"),
            ["LlmService:claude_sonnet_default_model"] = Environment.GetEnvironmentVariable("CLAUDE_SONNET_DEFAULT_MODEL"),
            ["LlmService:claude_haiku_default_model"] = Environment.GetEnvironmentVariable("CLAUDE_HAIKU_DEFAULT_MODEL"),
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));

        // LocalLLMService is bound first as a legacy fallback, then LlmService overrides any
        // keys it defines. BindNonPublicProperties lets the binder write the internal setters.
        builder.Services.AddOptions<LlmServiceOptions>()
            .Bind(builder.Configuration.GetSection("LocalLLMService"), o => o.BindNonPublicProperties = true)
            .Bind(builder.Configuration.GetSection(LlmServiceOptions.SectionName), o => o.BindNonPublicProperties = true);

        // Keyed response handlers (open-closed: add a model → register its handler).
        builder.Services.AddKeyedScoped<ILocalLLMResponseHandler>(
            "qwen/qwen2.5-coder-14b", (sp, key) => new Qwen2_5ResponseHandler());

        return builder;
    }

    private static string? GetFirstNonEmptyEnvironmentVariable(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Seeds the mutable runtime routing settings (held in <c>IMemoryCache</c>, tweakable at
    /// runtime via <c>/override-model</c>) from the bound startup configuration.
    /// </summary>
    public static WebApplication SeedModelRouteSettings(this WebApplication app)
    {
        var cache = app.Services.GetRequiredService<IMemoryCache>();
        var llm = app.Services.GetRequiredService<IOptions<LlmServiceOptions>>().Value;
        cache.Set("model_route_settings", new ModelRouteSettings
        {
            Enabled = llm.Enabled,
            ApiFormat = llm.ApiFormat,
            StripNonClaudeModels = llm.StripNonClaudeModels,
            FableModel = llm.FableDefaultModel,
            OpusModel = llm.OpusDefaultModel,
            SonnetModel = llm.SonnetDefaultModel,
            HaikuModel = llm.HaikuDefaultModel,
        });
        Serilog.Log.Information("LLM: {LlmUrl} (apiFormat={ApiFormat})", llm.BaseUrl, llm.ApiFormat);
        return app;
    }
}
