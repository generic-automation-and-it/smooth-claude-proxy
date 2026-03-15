using System.Text.Json;

/// <summary>
/// Maps Liquid model tool calls to Claude Code tool format using a JSON translation table.
/// Loads app_data/liquid-tool-translator.json at startup.
/// </summary>
public class LiquidToolMapper
{
    private readonly Dictionary<string, ToolMapping> _toolMappings = new();
    private readonly ILogger<LiquidToolMapper> _logger;

    public class ToolMapping
    {
        public string Target { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }

    public LiquidToolMapper(ILogger<LiquidToolMapper> logger)
    {
        _logger = logger;
        LoadTranslationTable();
    }

    private void LoadTranslationTable()
    {
        try
        {
            // Try multiple paths: AppContext.BaseDirectory, current directory, and /app
            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "app_data", "liquid-tool-translator.json"),
                Path.Combine(Environment.CurrentDirectory, "app_data", "liquid-tool-translator.json"),
                "/app/app_data/liquid-tool-translator.json",
                "app_data/liquid-tool-translator.json"
            };

            string? tablePath = null;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    tablePath = path;
                    break;
                }
            }

            if (string.IsNullOrEmpty(tablePath))
            {
                _logger.LogWarning("Tool translator table not found in any location. Tried: {Paths}",
                    string.Join(", ", possiblePaths));
                return;
            }

            _logger.LogInformation("Loading tool translator table from {Path}", tablePath);

            var json = File.ReadAllText(tablePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Object)
            {
                foreach (var toolProp in tools.EnumerateObject())
                {
                    var liquidName = toolProp.Name;
                    var toolDef = toolProp.Value;

                    if (toolDef.TryGetProperty("target", out var targetElem)
                        && toolDef.TryGetProperty("enabled", out var enabledElem))
                    {
                        var mapping = new ToolMapping
                        {
                            Target = targetElem.GetString() ?? "",
                            Enabled = enabledElem.GetBoolean()
                        };

                        // Load parameter mappings
                        if (toolDef.TryGetProperty("parameters", out var paramsDef) && paramsDef.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var paramProp in paramsDef.EnumerateObject())
                            {
                                mapping.Parameters[paramProp.Name] = paramProp.Value.GetString() ?? paramProp.Name;
                            }
                        }

                        _toolMappings[liquidName.ToLowerInvariant()] = mapping;
                    }
                }
            }

            _logger.LogInformation("Loaded {Count} tool mappings from {Path}", _toolMappings.Count, tablePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load tool translation table");
        }
    }

    /// <summary>
    /// Try to translate a Liquid tool call to Claude Code format.
    /// Returns the translated tool use JSON, or null if the tool isn't recognized/enabled.
    /// </summary>
    public (bool Success, JsonElement? TranslatedTool) TryTranslateTool(JsonElement liquidToolCall)
    {
        try
        {
            // Extract tool name from Liquid's tool call format
            if (!liquidToolCall.TryGetProperty("name", out var nameElem))
            {
                return (false, null);
            }

            var liquidToolName = nameElem.GetString()?.ToLowerInvariant() ?? "";
            if (!_toolMappings.TryGetValue(liquidToolName, out var mapping))
            {
                // Tool not in translation table
                return (false, null);
            }

            if (!mapping.Enabled)
            {
                // Tool is disabled
                return (false, null);
            }

            // Extract and translate the tool call input
            if (!liquidToolCall.TryGetProperty("arguments", out var inputElem))
            {
                return (false, null);
            }

            // Build the translated tool use object in Anthropic format
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", $"toolu_{Guid.NewGuid():N}");
                writer.WriteString("name", mapping.Target);

                // Translate input parameters
                writer.WritePropertyName("input");
                writer.WriteStartObject();

                if (inputElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inputProp in inputElem.EnumerateObject())
                    {
                        var ccParamName = mapping.Parameters.TryGetValue(inputProp.Name, out var mapped)
                            ? mapped
                            : inputProp.Name;

                        writer.WritePropertyName(ccParamName);
                        inputProp.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var translated = JsonDocument.Parse(ms.ToArray()).RootElement;
            return (true, translated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error translating Liquid tool call");
            return (false, null);
        }
    }

    /// <summary>
    /// Check if a tool is supported and enabled.
    /// </summary>
    public bool IsToolSupported(string liquidToolName)
    {
        return _toolMappings.TryGetValue(liquidToolName.ToLowerInvariant(), out var mapping) && mapping.Enabled;
    }

    /// <summary>
    /// Get all supported tool names.
    /// </summary>
    public IEnumerable<string> GetSupportedTools()
    {
        return _toolMappings
            .Where(kv => kv.Value.Enabled)
            .Select(kv => kv.Value.Target);
    }
}
