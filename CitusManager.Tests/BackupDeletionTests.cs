using System.Text.Json;
using CitusManager.Services;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Xunit;

namespace CitusManager.Tests;

public sealed class BackupDeletionTests
{
    [Fact]
    public async Task Delete_removes_commit_marker_before_every_artifact_object()
    {
        var storage = new RecordingStorage();
        var manifest = Manifest(
            "clusters/c/backups/b/objects/00000000.cmba",
            "clusters/c/backups/b/objects/00000001.cmba");

        await BackupService.DeleteArtifactAsync(
            storage, manifest, "clusters/c/backups/b", CancellationToken.None);

        Assert.Equal(
        [
            "clusters/c/backups/b/manifest.v1.json",
            "clusters/c/backups/b/objects/00000000.cmba",
            "clusters/c/backups/b/objects/00000001.cmba"
        ], storage.DeletedKeys);
    }

    [Fact]
    public async Task Delete_without_manifest_still_removes_known_commit_marker()
    {
        var storage = new RecordingStorage();

        await BackupService.DeleteArtifactAsync(
            storage, null, "clusters/c/backups/failed", CancellationToken.None);

        Assert.Equal(["clusters/c/backups/failed/manifest.v1.json"], storage.DeletedKeys);
    }

    private static BackupArtifactManifest Manifest(params string[] keys) => new(
        1, DateTimeOffset.UtcNow, true, false, 4 * 1024 * 1024, 256 * 1024 * 1024,
        10, "archive-sha", "protected-key",
        keys.Select((key, index) => new BackupArtifactObjectManifest(
            index, key, 5, 10, "object-sha", [])).ToList(),
        JsonSerializer.SerializeToElement(new { }), "hmac");

    private sealed class RecordingStorage : IBackupStorageProvider
    {
        public string ProviderType => "Recording";
        public List<string> DeletedKeys { get; } = [];
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TestConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
