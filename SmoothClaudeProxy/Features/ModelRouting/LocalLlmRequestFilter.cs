using System.Text.Json;

namespace SmoothClaudeProxy.Features.ModelRouting;

/// <summary>
/// Strips Anthropic-specific noise from requests before forwarding to local LLM.
/// Applies only to the local LLM path — Anthropic path is always pass-through.
/// </summary>
public static class LocalLlmRequestFilter
{
    // Tags whose blocks should be dropped entirely from message content
    private static readonly string[] NoiseTags =
        ["system-reminder", "local-command-caveat", "command-name", "local-command-stdout", "available-deferred-tools"];

    /// <summary>Returns true if a system-array text block is Anthropic infrastructure noise.</summary>
    public static bool IsSystemNoise(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("x-anthropic-billing-header:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("You are Claude Code, Anthropic", StringComparison.Ordinal);
    }

    /// <summary>Returns true if a message content text block is purely Anthropic metadata.</summary>
    public static bool IsMessageNoise(string text)
    {
        var t = text.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        foreach (var tag in NoiseTags)
            if (t.StartsWith($"<{tag}>") || t.StartsWith($"<{tag} "))
                return true;
        return false;
    }

    /// <summary>Strips system-reminder XML tags from a text string (inline clean-up).</summary>
    public static string StripInlineNoise(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"<system-reminder>.*?</system-reminder>\n*", "",
            System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

    /// <summary>
    /// Strips mermaid diagrams and the Changelog section from CLAUDE.md-style system content.
    /// Keeps Non-Negotiables, Key Behaviors, API Endpoints, and other task-relevant sections.
    /// </summary>
    public static string TrimSystemContent(string text)
    {
        // Remove mermaid diagrams (visual, no value as text)
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"```mermaid.*?```", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        // Remove Changelog section to end (it's always last and never useful for task execution)
        var changelogIdx = text.IndexOf("\n## Changelog", StringComparison.Ordinal);
        if (changelogIdx > 0) text = text[..changelogIdx];
        return text.Trim();
    }

    /// <summary>
    /// Writes a slim OpenAI-format tool definition from an Anthropic-format tool element.
    /// Strips verbose descriptions and optional parameter details to reduce token overhead.
    /// </summary>
    public static void WriteSlimTool(System.Text.Json.Utf8JsonWriter w, JsonElement anthropicTool)
    {
        w.WriteStartObject();
        w.WriteString("type", "function");
        w.WritePropertyName("function");
        w.WriteStartObject();

        if (anthropicTool.TryGetProperty("name", out var name))
            { w.WritePropertyName("name"); name.WriteTo(w); }

        // Truncate description to first line, max 120 chars
        if (anthropicTool.TryGetProperty("description", out var desc))
        {
            var d = desc.GetString() ?? "";
            var nl = d.IndexOf('\n');
            if (nl > 0) d = d[..nl].Trim();
            if (d.Length > 120) { var dot = d.IndexOf(". "); d = dot > 0 ? d[..(dot + 1)] : d[..120]; }
            w.WriteString("description", d);
        }

        if (anthropicTool.TryGetProperty("input_schema", out var schema))
        {
            w.WritePropertyName("parameters");
            WriteSlimSchema(w, schema);
        }

        w.WriteEndObject(); // function
        w.WriteEndObject(); // tool
    }

    private static void WriteSlimSchema(System.Text.Json.Utf8JsonWriter w, JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        JsonElement reqElem = default;
        if (schema.TryGetProperty("required", out reqElem) && reqElem.ValueKind == JsonValueKind.Array)
            foreach (var r in reqElem.EnumerateArray())
                if (r.GetString() is string s) required.Add(s);

        w.WriteStartObject();
        w.WriteString("type", "object");

        if (schema.TryGetProperty("properties", out var props))
        {
            w.WritePropertyName("properties");
            w.WriteStartObject();
            foreach (var prop in props.EnumerateObject())
            {
                w.WritePropertyName(prop.Name);
                w.WriteStartObject();
                if (prop.Value.TryGetProperty("type", out var t)) { w.WritePropertyName("type"); t.WriteTo(w); }
                if (prop.Value.TryGetProperty("enum", out var e)) { w.WritePropertyName("enum"); e.WriteTo(w); }
                // Description only for required params, truncated to 80 chars
                if (required.Contains(prop.Name) && prop.Value.TryGetProperty("description", out var d))
                {
                    var dt = d.GetString() ?? "";
                    var nl = dt.IndexOf('\n'); if (nl > 0) dt = dt[..nl].Trim();
                    if (dt.Length > 80) dt = dt[..80];
                    w.WriteString("description", dt);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        if (reqElem.ValueKind == JsonValueKind.Array) { w.WritePropertyName("required"); reqElem.WriteTo(w); }
        w.WriteEndObject();
    }
}
