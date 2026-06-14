using System.Text;
using System.Text.Json;

namespace SmoothClaudeProxy.Features.ModelRouting;

/// <summary>Resolves per-family model overrides and rewrites the model field on request bodies.</summary>
public static class ModelOverrideResolver
{
    // Returns the configured override model for a given inbound model, or null if no family
    // prefix matches or the matching override is empty. Family prefixes are matched
    // case-insensitively (claude-fable, claude-opus, claude-sonnet, claude-haiku).
    public static string? ResolveModelOverride(string? model, ModelRouteSettings s)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
    
        if (model.StartsWith("claude-fable", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.FableModel))
            return s.FableModel;
        if (model.StartsWith("claude-opus", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.OpusModel))
            return s.OpusModel;
        if (model.StartsWith("claude-sonnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.SonnetModel))
            return s.SonnetModel;
        if (model.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.HaikuModel))
            return s.HaikuModel;
    
        return null;
    }

    // Rewrites the top-level "model" property of a JSON object body, preserving key order.
    // Non-object bodies (or unparseable JSON) are returned unchanged.
    public static string RewriteModelField(string body, string newModel)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("model"))
                        w.WriteString("model", newModel);
                    else
                        prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public static bool ContainsCacheControlProperty(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("cache_control"))
                        return true;
                    if (ContainsCacheControlProperty(prop.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsCacheControlProperty(item))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }
}
