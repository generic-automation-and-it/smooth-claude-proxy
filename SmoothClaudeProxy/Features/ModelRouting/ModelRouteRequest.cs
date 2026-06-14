namespace SmoothClaudeProxy.Features.ModelRouting;

public class ModelRouteRequest
{
    public bool? Enabled { get; set; }
    public string? ApiFormat { get; set; }
    public bool? StripNonClaudeModels { get; set; }
}
