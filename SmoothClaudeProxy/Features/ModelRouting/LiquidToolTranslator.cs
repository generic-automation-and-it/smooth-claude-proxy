namespace SmoothClaudeProxy.Features.ModelRouting;

// Tool translator: converts Liquid format tool calls to Anthropic format
public static class LiquidToolTranslator
{
    private static readonly Dictionary<string, ToolDefinition> SupportedTools = new()
    {
        { "bash", new ToolDefinition { Name = "bash", Description = "Execute shell command", Params = new[] { "command" } } },
        { "read", new ToolDefinition { Name = "read", Description = "Read file content", Params = new[] { "file_path" } } },
        { "glob", new ToolDefinition { Name = "glob", Description = "Find files by pattern", Params = new[] { "pattern" } } },
        { "grep", new ToolDefinition { Name = "grep", Description = "Search file content", Params = new[] { "pattern" } } },
        { "edit", new ToolDefinition { Name = "edit", Description = "Edit file", Params = new[] { "file_path", "old_string", "new_string" } } },
        { "write", new ToolDefinition { Name = "write", Description = "Write file", Params = new[] { "file_path", "content" } } },
    };

    public class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] Params { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Converts Liquid format: tool_name(param1="value1", param2="value2")
    /// To Anthropic format tool_use block
    /// </summary>
    public static string? TryParseAndConvertToolCall(string liquidFormat)
    {
        // Parse: tool_name(params...)
        var match = System.Text.RegularExpressions.Regex.Match(
            liquidFormat,
            @"^(\w+)\((.*)\)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!match.Success)
            return null;

        var toolName = match.Groups[1].Value.ToLowerInvariant();
        var paramsStr = match.Groups[2].Value;

        if (!SupportedTools.TryGetValue(toolName, out var toolDef))
            return null;

        // Parse parameters: key="value", key2="value2"
        var input = new Dictionary<string, object>();
        var paramPattern = @"(\w+)=""([^""]*)""";
        var paramMatches = System.Text.RegularExpressions.Regex.Matches(paramsStr, paramPattern);

        foreach (System.Text.RegularExpressions.Match m in paramMatches)
        {
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;
            input[key] = value;
        }

        // Build Anthropic tool_use block
        var toolId = $"toolu_{Guid.NewGuid():N}".Substring(0, 24); // Mimic Anthropic ID format
        var toolUse = new
        {
            type = "tool_use",
            id = toolId,
            name = toolName,
            input
        };

        return System.Text.Json.JsonSerializer.Serialize(toolUse);
    }
}
