using System.Collections.Concurrent;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CitusManager.Services;

public sealed record QueryEndpointCandidate(Guid Id, string Host, int Port);

public interface IQueryEndpointHealthRegistry
{
    bool IsEjected(Guid endpointId, DateTimeOffset now);
    void MarkFailure(Guid endpointId, DateTimeOffset now);
    void MarkSuccess(Guid endpointId);
}

public sealed class QueryEndpointHealthRegistry : IQueryEndpointHealthRegistry
{
    private static readonly TimeSpan EjectionDuration = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> ejectedUntil = new();

    public bool IsEjected(Guid endpointId, DateTimeOffset now)
    {
        if (!ejectedUntil.TryGetValue(endpointId, out var until)) return false;
        if (until > now) return true;
        ejectedUntil.TryRemove(endpointId, out _);
        return false;
    }

    public void MarkFailure(Guid endpointId, DateTimeOffset now) =>
        ejectedUntil[endpointId] = now + EjectionDuration;

    public void MarkSuccess(Guid endpointId) => ejectedUntil.TryRemove(endpointId, out _);
}

public interface IQueryEndpointSelector
{
    QueryEndpointCandidate? Select(Guid clusterId, IReadOnlyList<QueryEndpointCandidate> endpoints);
}

public sealed class RoundRobinQueryEndpointSelector(
    IQueryEndpointHealthRegistry health,
    TimeProvider timeProvider) : IQueryEndpointSelector
{
    private readonly ConcurrentDictionary<Guid, long> cursors = new();

    public QueryEndpointCandidate? Select(Guid clusterId, IReadOnlyList<QueryEndpointCandidate> endpoints)
    {
        if (endpoints.Count == 0) return null;
        var now = timeProvider.GetUtcNow();
        var available = endpoints.Where(x => !health.IsEjected(x.Id, now)).ToArray();
        if (available.Length == 0) return null;
        var cursor = cursors.AddOrUpdate(clusterId, 0, static (_, current) => unchecked(current + 1));
        return available[(int)(unchecked((ulong)cursor) % (ulong)available.Length)];
    }
}

public sealed record RoutedQueryConnection(
    NpgsqlConnection Connection, bool IsControlCoordinator, Guid? EndpointId, string Host, int Port)
    : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public interface IQueryEndpointRouter
{
    Task<RoutedQueryConnection> OpenAsync(
        ClusterProfile cluster, bool provenReadOnly, CancellationToken cancellationToken);
}

public sealed class QueryEndpointRouter(
    ControlDbContext db,
    ICitusConnectionFactory connections,
    IQueryEndpointSelector selector,
    IQueryEndpointHealthRegistry health,
    TimeProvider timeProvider) : IQueryEndpointRouter
{
    public async Task<RoutedQueryConnection> OpenAsync(
        ClusterProfile cluster, bool provenReadOnly, CancellationToken cancellationToken)
    {
        if (provenReadOnly)
        {
            var endpoints = await db.ClusterQueryEndpoints.AsNoTracking()
                .Where(x => x.ClusterId == cluster.Id && x.IsEnabled &&
                            x.Health == QueryEndpointHealth.Healthy && x.MetadataSynced)
                .OrderBy(x => x.Id)
                .Select(x => new QueryEndpointCandidate(x.Id, x.Host, x.Port))
                .ToListAsync(cancellationToken);
            for (var attempt = 0; attempt < endpoints.Count; attempt++)
            {
                var endpoint = selector.Select(cluster.Id, endpoints);
                if (endpoint is null) break;
                var routed = connections.Create(cluster, endpoint.Host, endpoint.Port);
                try
                {
                    await routed.OpenAsync(cancellationToken);
                    health.MarkSuccess(endpoint.Id);
                    return new(routed, false, endpoint.Id, endpoint.Host, endpoint.Port);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    await routed.DisposeAsync();
                    health.MarkFailure(endpoint.Id, timeProvider.GetUtcNow());
                }
            }
        }

        var control = connections.Create(cluster);
        await control.OpenAsync(cancellationToken);
        return new(control, true, null, cluster.Host, cluster.Port);
    }
}
