using LiteDB;

namespace SmoothClaudeProxy.Features.Logins;

/// <summary>Resolves a tracked login by exact bearer token, then by email or label.</summary>
public static class UserLookup
{
    public static UserRecord? FindByIdentifier(ILiteCollection<UserRecord> col, string identifier)
    {
        return col.FindById(identifier) ?? col.FindOne(x => x.Email == identifier || x.Label == identifier);
    }
}
