using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmoothClaudeProxy.Features.Proxy;

/// <summary>Decodes the JWT payload (no signature validation) to extract identity claims.</summary>
public static class JwtIdentity
{
    public static (string? email, string? name) TryDecodeJwt(string token, ILogger logger)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return (null, null);
    
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
    
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
    
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var name = root.TryGetProperty("name", out var n) ? n.GetString()
                     : root.TryGetProperty("sub", out var s) ? s.GetString()
                     : null;
    
            if (email is null)
                logger.LogInformation("JWT claims (no email found): {Claims}", json);
    
            return (email, name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JWT decode failed");
            return (null, null);
        }
    }
}
