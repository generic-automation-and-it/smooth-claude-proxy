using System.Threading.Channels;
using LiteDB;
using Microsoft.Extensions.Caching.Memory;

namespace SmoothClaudeProxy.Features.Logins;

public class UserUpsertWorker : BackgroundService
{
    private readonly Channel<UserRecord> _channel;
    private readonly ILiteDatabase _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserUpsertWorker> _logger;

    public UserUpsertWorker(
        Channel<UserRecord> channel,
        ILiteDatabase db,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<UserUpsertWorker> logger)
    {
        _channel = channel;
        _db = db;
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var col = _db.GetCollection<UserRecord>("users");
        col.EnsureIndex(x => x.BearerToken, unique: true);
        col.EnsureIndex(x => x.Email);

        await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var existing = col.FindById(record.BearerToken);

                if (existing is not null)
                {
                    var changed = false;
                    var utilizationChanged = false;

                    if (record.Label is not null && record.Label != existing.Label)
                    {
                        existing.Label = record.Label;
                        changed = true;
                    }
                    if (record.Email is not null && record.Email != existing.Email)
                    {
                        existing.Email = record.Email;
                        changed = true;
                    }
                    if (record.Utilization5h is not null
                        && record.Utilization5h != existing.Utilization5h)
                    {
                        existing.Utilization5h = record.Utilization5h;
                        changed = true;
                        utilizationChanged = true;
                    }
                    if (record.Utilization7d is not null
                        && record.Utilization7d != existing.Utilization7d)
                    {
                        existing.Utilization7d = record.Utilization7d;
                        changed = true;
                        utilizationChanged = true;
                    }
                    if (record.Reset5h is not null
                        && record.Reset5h != existing.Reset5h)
                    {
                        existing.Reset5h = record.Reset5h;
                        changed = true;
                    }
                    if (record.Reset7d is not null
                        && record.Reset7d != existing.Reset7d)
                    {
                        existing.Reset7d = record.Reset7d;
                        changed = true;
                    }

                    if (utilizationChanged || existing.LastUsedUtc is null)
                    {
                        existing.LastUsedUtc = DateTime.UtcNow;
                        changed = true;
                    }

                    // Set as active inbound if not already
                    if (!existing.IsActiveInbound)
                    {
                        // Deactivate all others
                        foreach (var other in col.Find(u => u.IsActiveInbound && u.BearerToken != existing.BearerToken))
                        {
                            other.IsActiveInbound = false;
                            col.Update(other);
                        }
                        existing.IsActiveInbound = true;
                        changed = true;

                        // Purge tokens not used in over 1 week
                        var cutoff = DateTime.UtcNow.AddDays(-7);
                        var stale = col.Find(u => u.LastUsedUtc != null && u.LastUsedUtc < cutoff).ToList();
                        foreach (var s in stale)
                        {
                            col.Delete(s.BearerToken);
                            _logger.LogInformation("Purged stale token {Label} (last used {LastUsed})", s.Label, s.LastUsedUtc);
                        }
                    }

                    if (changed)
                        col.Update(existing);
                }
                else
                {
                    // New token — deactivate all others
                    foreach (var other in col.Find(u => u.IsActiveInbound))
                    {
                        other.IsActiveInbound = false;
                        col.Update(other);
                    }

                    if (string.IsNullOrEmpty(record.Label))
                        record.Label = new Bogus.Faker().Name.FullName().ToLowerInvariant().Replace(" ", "-");
                    record.IsActiveInbound = true;
                    record.LastUsedUtc = DateTime.UtcNow;
                    col.Insert(record);
                    _cache.Remove("active_session");
                    _logger.LogInformation("New token tracked as {Label} — active session cleared", record.Label);

                    // Purge tokens not used in over 1 week
                    var cutoff = DateTime.UtcNow.AddDays(-7);
                    var stale = col.Find(u => u.LastUsedUtc != null && u.LastUsedUtc < cutoff).ToList();
                    foreach (var s in stale)
                    {
                        col.Delete(s.BearerToken);
                        _logger.LogInformation("Purged stale token {Label} (last used {LastUsed})", s.Label, s.LastUsedUtc);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Insert failed");
            }
        }
    }
}
