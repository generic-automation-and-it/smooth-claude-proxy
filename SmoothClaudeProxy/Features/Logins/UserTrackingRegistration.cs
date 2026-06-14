using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothClaudeProxy.Features.Logins;

/// <summary>Composition root wiring for the user-tracking (logins) feature.</summary>
public static class UserTrackingRegistration
{
    /// <summary>
    /// Registers the unbounded <see cref="Channel{T}"/> that decouples DB writes from the
    /// request pipeline and the single-reader <see cref="UserUpsertWorker"/> that drains it.
    /// </summary>
    public static IServiceCollection AddUserTracking(this IServiceCollection services)
    {
        var channel = Channel.CreateUnbounded<UserRecord>(
            new UnboundedChannelOptions { SingleReader = true });
        services.AddSingleton(channel);
        services.AddHostedService<UserUpsertWorker>();
        return services;
    }
}
