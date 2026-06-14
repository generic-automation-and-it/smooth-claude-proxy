namespace SmoothClaudeProxy.Features.ModelRouting;

public class ModelRouteSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Upstream API shape: "anthropic" = pure passthrough to a /v1/messages endpoint (no conversion);
    /// "openai" = convert to OpenAI chat format and back (OpenAI-compatible LLM path).</summary>
    public string ApiFormat { get; set; } = "anthropic";
    /// <summary>Gates the entire OpenAI-format request preprocessing. Off (default): the body is
    /// forwarded byte-for-byte — no conversion, rewriting, or filtering of any kind. On: the full
    /// Anthropic→OpenAI conversion + slimming pipeline (field drops, system-reminder/noise filters,
    /// Qwen system prompt replacement) runs as before.</summary>
    public bool StripNonClaudeModels { get; set; } = false;

    /// <summary>Per-family model overrides. When the inbound model starts with the family prefix
    /// (e.g. "claude-fable") and the override is non-empty, the request is routed to the configured
    /// LLM upstream instead of Anthropic, with the model field rewritten to the override value.
    /// Empty/null = no override (claude-* models pass through to Anthropic as normal).</summary>
    public string? FableModel { get; set; }
    public string? OpusModel { get; set; }
    public string? SonnetModel { get; set; }
    public string? HaikuModel { get; set; }
}
