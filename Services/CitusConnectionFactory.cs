using CitusManager.Domain;
using CitusManager.Security;
using Npgsql;

namespace CitusManager.Services;

public interface ICitusConnectionFactory
{
    NpgsqlConnection Create(ClusterProfile profile);
}

public sealed class CitusConnectionFactory(IClusterSecretProtector secrets) : ICitusConnectionFactory
{
    public NpgsqlConnection Create(ClusterProfile profile)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = profile.Host,
            Port = profile.Port,
            Database = profile.Database,
            ApplicationName = "CitusManager",
            Timeout = 10,
            CommandTimeout = 30,
            Pooling = true,
            MaxPoolSize = 10,
            SslMode = profile.SslMode switch
            {
                ClusterSslMode.Disable => SslMode.Disable,
                ClusterSslMode.Prefer => SslMode.Prefer,
                ClusterSslMode.Require => SslMode.Require,
                ClusterSslMode.VerifyCa => SslMode.VerifyCA,
                ClusterSslMode.VerifyFull => SslMode.VerifyFull,
                _ => SslMode.Prefer
            }
        };

        if (!string.IsNullOrWhiteSpace(profile.Username))
            builder.Username = profile.Username;
        if (!string.IsNullOrWhiteSpace(profile.ProtectedPassword))
            builder.Password = secrets.Unprotect(profile.ProtectedPassword);

        return new NpgsqlConnection(builder.ConnectionString);
    }
}
