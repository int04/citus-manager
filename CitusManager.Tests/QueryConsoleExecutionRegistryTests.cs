using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class QueryConsoleExecutionRegistryTests
{
    [Fact]
    public void Queued_statement_can_be_skipped_and_will_not_start()
    {
        var registry = new QueryConsoleExecutionRegistry();
        var executionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        registry.Register(executionId, actorId, clusterId, [0, 1]);

        Assert.Equal(SkipConsoleStatementResult.Skipped,
            registry.Skip(executionId, actorId, clusterId, 1));
        Assert.True(registry.TryStart(executionId, 0));
        Assert.False(registry.TryStart(executionId, 1));
    }

    [Fact]
    public void Running_statement_cannot_be_skipped()
    {
        var registry = new QueryConsoleExecutionRegistry();
        var executionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        registry.Register(executionId, actorId, clusterId, [0]);

        Assert.True(registry.TryStart(executionId, 0));
        Assert.Equal(SkipConsoleStatementResult.AlreadyStarted,
            registry.Skip(executionId, actorId, clusterId, 0));
    }

    [Fact]
    public void Execution_is_scoped_to_actor_and_cluster()
    {
        var registry = new QueryConsoleExecutionRegistry();
        var executionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        registry.Register(executionId, actorId, clusterId, [0]);

        Assert.Equal(SkipConsoleStatementResult.ExecutionNotFound,
            registry.Skip(executionId, Guid.NewGuid(), clusterId, 0));
        Assert.Equal(SkipConsoleStatementResult.ExecutionNotFound,
            registry.Skip(executionId, actorId, Guid.NewGuid(), 0));
    }
}
