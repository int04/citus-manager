using System.Collections.Concurrent;

namespace CitusManager.Services;

public enum SkipConsoleStatementResult
{
    Skipped,
    AlreadySkipped,
    AlreadyStarted,
    ExecutionNotFound
}

public interface IQueryConsoleExecutionRegistry
{
    void Register(Guid executionId, Guid actorId, Guid clusterId, IEnumerable<int> statementIndexes);
    bool TryStart(Guid executionId, int statementIndex);
    SkipConsoleStatementResult Skip(Guid executionId, Guid actorId, Guid clusterId, int statementIndex);
    void Complete(Guid executionId);
}

public sealed class QueryConsoleExecutionRegistry : IQueryConsoleExecutionRegistry
{
    private readonly ConcurrentDictionary<Guid, ExecutionState> executions = new();

    public void Register(Guid executionId, Guid actorId, Guid clusterId, IEnumerable<int> statementIndexes)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("Invalid execution ID.");
        var state = new ExecutionState(actorId, clusterId, statementIndexes);
        if (!executions.TryAdd(executionId, state))
            throw new ArgumentException("Execution ID is already in use.");
    }

    public bool TryStart(Guid executionId, int statementIndex) =>
        executions.TryGetValue(executionId, out var state) && state.TryStart(statementIndex);

    public SkipConsoleStatementResult Skip(
        Guid executionId, Guid actorId, Guid clusterId, int statementIndex)
    {
        if (!executions.TryGetValue(executionId, out var state) ||
            state.ActorId != actorId || state.ClusterId != clusterId)
            return SkipConsoleStatementResult.ExecutionNotFound;
        return state.Skip(statementIndex);
    }

    public void Complete(Guid executionId) => executions.TryRemove(executionId, out _);

    private sealed class ExecutionState(Guid actorId, Guid clusterId, IEnumerable<int> statementIndexes)
    {
        private readonly Lock sync = new();
        private readonly Dictionary<int, StatementState> statements = statementIndexes
            .Distinct().ToDictionary(index => index, _ => StatementState.Queued);

        public Guid ActorId { get; } = actorId;
        public Guid ClusterId { get; } = clusterId;

        public bool TryStart(int statementIndex)
        {
            lock (sync)
            {
                if (!statements.TryGetValue(statementIndex, out var state) || state != StatementState.Queued)
                    return false;
                statements[statementIndex] = StatementState.Running;
                return true;
            }
        }

        public SkipConsoleStatementResult Skip(int statementIndex)
        {
            lock (sync)
            {
                if (!statements.TryGetValue(statementIndex, out var state))
                    return SkipConsoleStatementResult.ExecutionNotFound;
                if (state == StatementState.Skipped) return SkipConsoleStatementResult.AlreadySkipped;
                if (state != StatementState.Queued) return SkipConsoleStatementResult.AlreadyStarted;
                statements[statementIndex] = StatementState.Skipped;
                return SkipConsoleStatementResult.Skipped;
            }
        }
    }

    private enum StatementState
    {
        Queued,
        Running,
        Skipped
    }
}
