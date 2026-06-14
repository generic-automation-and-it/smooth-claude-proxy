using System.Text;
using System.Text.Json;

namespace SmoothClaudeProxy.Features.ModelRouting;

/// <summary>
/// When Claude Code truncates a large tool result and saves it to disk, the tool result
/// content contains a &lt;persisted-output&gt; block with the file path. This helper reads
/// the file and replaces the block with the actual content so Qwen sees the full result.
/// </summary>
public static class PersistedOutputResolver
{
    public static async Task<string> ResolveAsync(string text)
    {
        const string openTag = "<persisted-output>";
        const string closeTag = "</persisted-output>";

        if (!text.Contains(openTag)) return text;

        var start = text.IndexOf(openTag);
        var end = text.IndexOf(closeTag);
        if (end < 0) return text;

        var inner = text[(start + openTag.Length)..end];

        const string marker = "Full output saved to: ";
        var markerIdx = inner.IndexOf(marker);
        if (markerIdx < 0) return text;

        var pathStart = markerIdx + marker.Length;
        var pathEnd = inner.IndexOf('\n', pathStart);
        var filePath = (pathEnd >= 0 ? inner[pathStart..pathEnd] : inner[pathStart..]).Trim();

        if (!File.Exists(filePath)) return text;

        try
        {
            var fileContent = await File.ReadAllTextAsync(filePath);
            // File is a JSON array of content blocks — extract text values
            using var doc = JsonDocument.Parse(fileContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in doc.RootElement.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        block.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                }
                return text[..start] + sb.ToString() + text[(end + closeTag.Length)..];
            }
            return text[..start] + fileContent + text[(end + closeTag.Length)..];
        }
        catch
        {
            return text;
        }
    }
}
