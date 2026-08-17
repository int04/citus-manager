using CitusManager.Contracts;
using CitusManager.Domain;
using CitusManager.Models;
using Xunit;

namespace CitusManager.Tests;

public sealed class ClusterTopologyPresentationTests
{
    [Fact]
    public void Drained_worker_is_not_misclassified_as_query_endpoint()
    {
        var drained = Node(2, "worker-1", shouldHaveShards: false);

        var groups = ClusterTopologyPresentation.Classify([drained], []);

        Assert.Empty(groups.QueryNodes);
        Assert.Same(drained, Assert.Single(groups.Workers));
    }

    [Fact]
    public void Registered_query_endpoint_is_classified_by_control_database_identity()
    {
        var queryNode = Node(3, "query-1", shouldHaveShards: false);
        var endpoint = new ClusterQueryEndpointResponse(
            Guid.NewGuid(), "QUERY-1", 5432, true, QueryEndpointHealth.Healthy,
            true, DateTimeOffset.UtcNow, null);

        var groups = ClusterTopologyPresentation.Classify([queryNode], [endpoint]);

        Assert.Same(queryNode, Assert.Single(groups.QueryNodes));
        Assert.Empty(groups.Workers);
    }

    private static CitusNodeResponse Node(int id, string host, bool shouldHaveShards) =>
        new(id, id, host, 5432, "primary", true, true, true,
            shouldHaveShards, 0, 0);
}
