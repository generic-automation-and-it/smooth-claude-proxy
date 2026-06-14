using LiteDB;

namespace SmoothClaudeProxy.Features.Logins;

public class UserRecord
{
    [BsonId]
    public string BearerToken { get; set; } = default!;
    public string? Email { get; set; }
    public string? Label { get; set; }
    public string? ApiKey { get; set; }
    public string? AnthropicVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool IsActiveInbound { get; set; }
    public double? Utilization5h { get; set; }
    public double? Utilization7d { get; set; }
    public long? Reset5h { get; set; }
    public long? Reset7d { get; set; }
}
