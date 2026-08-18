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
        Assert.Contains("--exclude-extension=citus", arguments);
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

    [Fact]
    public void Same_target_restore_list_excludes_only_citus_extension_entry()
    {
        string[] lines =
        [
            "; Archive created at 2026-08-18",
            "1; 3079 16385 EXTENSION - citus ",
            "4378; 0 0 COMMENT - EXTENSION citus ",
            "9; 2615 17799 SCHEMA - app postgres"
        ];

        var filtered = PostgresToolRunner.FilterRestoreList(lines, preserveCitusExtension: true);

        Assert.Equal(";1; 3079 16385 EXTENSION - citus ", filtered[1]);
        Assert.Equal(lines[2], filtered[2]);
        Assert.Equal(lines[3], filtered[3]);
    }

    [Fact]
    public void Same_target_restore_uses_filtered_toc_for_clean_pre_data()
    {
        var target = new ClusterProfile
        {
            Name = "target", Host = "coordinator", Port = 5432, Database = "app", Username = "restore"
        };

        var arguments = PostgresToolRunner.BuildRestoreArguments(
            target, "pre-data", clean: true, jobs: 2, restoreListPath: "restore.list");

        Assert.Contains("--clean", arguments);
        Assert.Contains("--if-exists", arguments);
        var listIndex = arguments.IndexOf("--use-list");
        Assert.True(listIndex >= 0);
        Assert.Equal("restore.list", arguments[listIndex + 1]);
    }

    private sealed class BrokenPipeStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Broken pipe"));
    }
}
