using Microsoft.Extensions.Configuration;

namespace SmoothClaudeProxy.Features.ModelRouting;

/// <summary>
/// Startup configuration for the alternate LLM upstream, bound from the <c>LlmService</c>
/// section through the standard configuration pipeline (appsettings.json + environment
/// variables). Setters are <c>internal</c> so values flow in only through configuration
/// binding (which uses <c>BindNonPublicProperties</c>); consumers read the friendly-named
/// getters. This object holds the immutable startup defaults — runtime changes live in the
/// mutable <see cref="ModelRouteSettings"/> kept in <c>IMemoryCache</c>.
/// </summary>
public sealed class LlmServiceOptions
{
    public const string SectionName = "LlmService";

    /// <summary>Base URL of the alternate upstream (env: <c>LMSTUDIO_BASE_URL</c>).</summary>
    public string BaseUrl { get; internal set; } = "http://host.docker.internal:1234";

    /// <summary>Auth token for the alternate upstream (env: <c>OPENCODE_API_KEY</c> or <c>LLMSERVICE_API_KEY</c>).</summary>
    public string AuthToken { get; internal set; } = "";

    /// <summary>Whether non-claude model routing is enabled.</summary>
    public bool Enabled { get; internal set; } = true;

    /// <summary>Upstream API shape: "anthropic" passthrough or "openai" conversion.</summary>
    public string ApiFormat { get; internal set; } = "anthropic";

    /// <summary>Gates the OpenAI-format request preprocessing pipeline.</summary>
    public bool StripNonClaudeModels { get; internal set; }

    /// <summary>Default-model override for claude-fable* (env: <c>CLAUDE_FABLE_DEFAULT_MODEL</c>).</summary>
    [ConfigurationKeyName("claude_fable_default_model")]
    public string? FableDefaultModel { get; internal set; }

    /// <summary>Default-model override for claude-opus* (env: <c>CLAUDE_OPUS_DEFAULT_MODEL</c>).</summary>
    [ConfigurationKeyName("claude_opus_default_model")]
    public string? OpusDefaultModel { get; internal set; }

    /// <summary>Default-model override for claude-sonnet* (env: <c>CLAUDE_SONNET_DEFAULT_MODEL</c>).</summary>
    [ConfigurationKeyName("claude_sonnet_default_model")]
    public string? SonnetDefaultModel { get; internal set; }

    /// <summary>Default-model override for claude-haiku* (env: <c>CLAUDE_HAIKU_DEFAULT_MODEL</c>).</summary>
    [ConfigurationKeyName("claude_haiku_default_model")]
    public string? HaikuDefaultModel { get; internal set; }
}
