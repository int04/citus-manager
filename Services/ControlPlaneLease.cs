using System.Buffers.Binary;
using System.Security.Cryptography;
using Npgsql;

namespace CitusManager.Services;

public interface IControlPlaneLeaseProvider
{
    Task<IAsyncDisposable?> TryAcquireClusterAsync(Guid clusterId, CancellationToken cancellationToken);
}

public sealed class ControlPlaneLeaseProvider(IConfiguration configuration) : IControlPlaneLeaseProvider
{
    public async Task<IAsyncDisposable?> TryAcquireClusterAsync(
        Guid clusterId, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("ControlDatabase")
            ?? throw new InvalidOperationException("Control database connection is missing.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var digest = SHA256.HashData(clusterId.ToByteArray());
        var key = BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(key);
        var acquired = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
        if (!acquired)
        {
            await connection.DisposeAsync();
            return null;
        }
        return new PostgreSqlLease(connection, key);
    }

    private sealed class PostgreSqlLease(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
                command.Parameters.AddWithValue(key);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
