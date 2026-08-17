using CitusManager.Services.BackupStorage;
using Xunit;

namespace CitusManager.Tests;

public sealed class LocalBackupStorageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"citus-local-storage-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/../../secret")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    public async Task Unsafe_key_is_rejected(string key)
    {
        var provider = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        await using var content = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.WriteAsync(key, content, 1, "application/octet-stream", CancellationToken.None));
    }

    [Fact]
    public async Task Failed_write_preserves_committed_object_and_removes_temp_file()
    {
        var provider = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        await using (var original = new MemoryStream([1, 2, 3]))
        {
            await provider.WriteAsync("run/object", original, 3, "application/octet-stream", CancellationToken.None);
        }

        await using var broken = new ThrowingReadStream();
        await Assert.ThrowsAsync<IOException>(() =>
            provider.WriteAsync("run/object", broken, 100, "application/octet-stream", CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(_root, "run/object"), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "run"), "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Health_check_leaves_no_artifacts()
    {
        var provider = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        await provider.TestConnectionAsync(CancellationToken.None);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Symbolic_link_cannot_escape_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"citus-local-storage-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
        try
        {
            var provider = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
            await using var content = new MemoryStream([1]);
            await Assert.ThrowsAsync<IOException>(() =>
                provider.WriteAsync("escape/object", content, 1, "application/octet-stream", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(outside, "object")));
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "escape"));
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated source failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("simulated source failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
