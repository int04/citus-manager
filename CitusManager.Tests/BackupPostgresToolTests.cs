using CitusManager.Domain;
using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class BackupPostgresToolTests
{
    [Fact]
    public void Dump_uses_portable_gzip_compression_by_default()
    {
        var source = new ClusterProfile
        {
            Name = "source",
            Host = "coordinator",
            Port = 5432,
            Database = "app",
            Username = "backup"
        };

        var arguments = PostgresToolRunner.BuildDumpArguments(source, new PostgresToolOptions().Compression);

        Assert.Contains("--compress=gzip:5", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("zstd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_compression_setting_falls_back_to_gzip()
    {
        var source = new ClusterProfile { Name = "source", Host = "coordinator" };

        var arguments = PostgresToolRunner.BuildDumpArguments(source, "  ");

        Assert.Contains("--compress=gzip:5", arguments);
    }

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 16.9", 16)]
    [InlineData("pg_restore (PostgreSQL) 18.4 (Homebrew)", 18)]
    public void Tool_version_parser_returns_postgresql_major(string version, int expected)
    {
        Assert.Equal(expected, PostgresToolRunner.ParseMajor(version));
    }

    [Fact]
    public async Task List_validation_drains_archive_when_pg_restore_closes_stdin_early()
    {
        var bytes = new byte[3 * 1024 * 1024 + 17];
        await using var source = new MemoryStream(bytes, writable: false);
        await using var destination = new BrokenPipeStream();
        var activityCount = 0;

        await PostgresToolRunner.CopyInputAsync(
            source, destination, () => activityCount++, drainAfterDestinationCloses: true, CancellationToken.None);

        Assert.Equal(source.Length, source.Position);
        Assert.True(activityCount >= 3);
    }

    private sealed class BrokenPipeStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Broken pipe"));
    }
}
