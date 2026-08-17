using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class QueryEndpointRoutingTests
{
    [Fact]
    public void Select_RotatesAcrossHealthyEndpoints()
    {
        var health = new QueryEndpointHealthRegistry();
        var selector = new RoundRobinQueryEndpointSelector(health, TimeProvider.System);
        var clusterId = Guid.NewGuid();
        var endpoints = new[]
        {
            new QueryEndpointCandidate(Guid.NewGuid(), "query-a", 5432),
            new QueryEndpointCandidate(Guid.NewGuid(), "query-b", 5432)
        };

        Assert.Equal("query-a", selector.Select(clusterId, endpoints)?.Host);
        Assert.Equal("query-b", selector.Select(clusterId, endpoints)?.Host);
        Assert.Equal("query-a", selector.Select(clusterId, endpoints)?.Host);
    }

    [Fact]
    public void Select_SkipsEjectedEndpoint_ThenReturnsNullForControlFallback()
    {
        var health = new QueryEndpointHealthRegistry();
        var selector = new RoundRobinQueryEndpointSelector(health, TimeProvider.System);
        var clusterId = Guid.NewGuid();
        var now = TimeProvider.System.GetUtcNow();
        var first = new QueryEndpointCandidate(Guid.NewGuid(), "query-a", 5432);
        var second = new QueryEndpointCandidate(Guid.NewGuid(), "query-b", 5432);

        health.MarkFailure(first.Id, now);
        Assert.Equal(second.Id, selector.Select(clusterId, [first, second])?.Id);

        health.MarkFailure(second.Id, now);
        Assert.Null(selector.Select(clusterId, [first, second]));
    }

    [Fact]
    public void MarkSuccess_ReintroducesEndpointImmediately()
    {
        var health = new QueryEndpointHealthRegistry();
        var selector = new RoundRobinQueryEndpointSelector(health, TimeProvider.System);
        var endpoint = new QueryEndpointCandidate(Guid.NewGuid(), "query-a", 5432);

        health.MarkFailure(endpoint.Id, TimeProvider.System.GetUtcNow());
        Assert.Null(selector.Select(Guid.NewGuid(), [endpoint]));

        health.MarkSuccess(endpoint.Id);
        Assert.Equal(endpoint.Id, selector.Select(Guid.NewGuid(), [endpoint])?.Id);
    }
}
