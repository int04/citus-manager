using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class RestoreExecutionTests
{
    [Fact]
    public async Task Cached_archive_is_closed_before_pg_restore_validation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "citus-manager-tests", Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(directory, "restore.dump");
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4 };

            var length = await RestoreRunExecutor.CacheAndValidateArchiveAsync(
                archive,
                output => output.WriteAsync(bytes).AsTask(),
                async path =>
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var restored = new byte[bytes.Length];
                    await input.ReadExactlyAsync(restored);
                    Assert.Equal(bytes, restored);
                });

            Assert.Equal(bytes.Length, length);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void Nested_pg_restore_failure_is_retained_for_diagnostics()
    {
        var toolFailure = new PostgresToolException("pg_restore", 1, "could not open input file");
        var wrapped = new InvalidDataException(
            "Every committed destination failed full archive validation.", toolFailure);

        Assert.Same(toolFailure, RestoreRunExecutor.FindPostgresToolFailure(wrapped));
    }
}
