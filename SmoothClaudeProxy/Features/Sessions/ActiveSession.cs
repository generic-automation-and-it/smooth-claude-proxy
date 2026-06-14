namespace SmoothClaudeProxy.Features.Sessions;

public class ActiveSession
{
    public string Email { get; set; } = default!;
    public string BearerToken { get; set; } = default!;
    public string? ApiKey { get; set; }
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public DateTime ActivatedUtc { get; set; }
}
