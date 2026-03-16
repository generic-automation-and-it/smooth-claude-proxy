using System.Text.Json;

/// <summary>
/// Handles converting local LLM responses to Anthropic SSE format.
/// Implement this interface for each model type to handle model-specific response processing.
/// </summary>
public interface ILocalLLMResponseHandler
{
    /// <summary>
    /// Process a response from the local LLM and stream it as Anthropic-compatible SSE to the client.
    /// </summary>
    Task HandleResponseAsync(
        HttpContext context,
        HttpResponseMessage lmResp,
        string targetModel,
        ILogger logger);
}

// Liquid model support removed — it didn't work reliably with tool calls.
// Only Qwen2_5ResponseHandler remains.
