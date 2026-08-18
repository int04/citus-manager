namespace CitusManager.Services;

public sealed class OperationWorker(
    IServiceScopeFactory scopes,
    IApplicationUpdateGate updateGate,
    ILogger<OperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (updateGate.IsClosed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                using var scope = scopes.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<IOperationExecutor>();
                var worked = await executor.ExecuteOneAsync(stoppingToken);
                if (!worked) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError("Operation runner loop failed ({ErrorType}).", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
