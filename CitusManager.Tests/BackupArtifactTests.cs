using System.Security.Cryptography;
using CitusManager.Security;
using CitusManager.Services.BackupArtifacts;
using CitusManager.Services.BackupStorage;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CitusManager.Tests;

public sealed class BackupArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"citus-artifact-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Encrypted_multi_object_artifact_round_trips()
    {
        Directory.CreateDirectory(_root);
        var sourceBytes = RandomNumberGenerator.GetBytes(13 * 1024 * 1024 + 17);
        var storage = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        var protector = CreateProtector();
        var writer = new BackupArtifactWriter(protector);
        var reader = new BackupArtifactReader(protector);
        await using var source = new MemoryStream(sourceBytes, writable: false);

        var result = await writer.WriteAsync(
            source,
            new BackupArtifactWriteRequest("cluster/run", new BackupArtifactWriteOptions
            {
                FrameSizeBytes = 4 * 1024 * 1024,
                ObjectSizeBytes = 8 * 1024 * 1024,
                StagingDirectory = _root,
                Encrypt = true
            }),
            storage,
            CancellationToken.None);

        Assert.Equal(2, result.Manifest.Objects.Count);
        Assert.All(result.Manifest.Objects, item => Assert.InRange(item.Frames.Max(frame => frame.PlaintextLength), 1, 4 * 1024 * 1024));
        Assert.DoesNotContain(Convert.ToBase64String(sourceBytes.AsSpan(0, 128)), File.ReadAllText(Path.Combine(_root, "cluster/run/manifest.v1.json")));
        await using var restored = new MemoryStream();
        await reader.ReadToAsync(result.ManifestKey, storage, restored, _root, CancellationToken.None);
        Assert.Equal(sourceBytes, restored.ToArray());
    }

    [Fact]
    public async Task Corrupt_object_is_rejected_before_plaintext_is_written()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        var protector = CreateProtector();
        var writer = new BackupArtifactWriter(protector);
        var reader = new BackupArtifactReader(protector);
        await using var source = new MemoryStream(RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        var result = await writer.WriteAsync(
            source,
            new BackupArtifactWriteRequest("cluster/corrupt", new BackupArtifactWriteOptions
            {
                FrameSizeBytes = 4 * 1024 * 1024,
                ObjectSizeBytes = 8 * 1024 * 1024,
                StagingDirectory = _root
            }),
            storage,
            CancellationToken.None);
        var objectPath = Path.Combine(_root, result.Manifest.Objects[0].Key.Replace('/', Path.DirectorySeparatorChar));
        await using (var file = new FileStream(objectPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = file.Length - 1;
            var value = file.ReadByte();
            file.Position = file.Length - 1;
            file.WriteByte((byte)(value ^ 0xff));
        }

        await using var plaintext = new MemoryStream();
        await Assert.ThrowsAsync<BackupArtifactIntegrityException>(() =>
            reader.ReadToAsync(result.ManifestKey, storage, plaintext, _root, CancellationToken.None));
        Assert.Equal(0, plaintext.Length);
    }

    [Fact]
    public async Task Tampered_manifest_is_rejected_before_objects_are_opened()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        var protector = CreateProtector();
        var writer = new BackupArtifactWriter(protector);
        var reader = new BackupArtifactReader(protector);
        await using var source = new MemoryStream(RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
        var result = await writer.WriteAsync(
            source,
            new BackupArtifactWriteRequest("cluster/manifest", new BackupArtifactWriteOptions { StagingDirectory = _root }),
            storage,
            CancellationToken.None);
        var manifestPath = Path.Combine(_root, result.ManifestKey.Replace('/', Path.DirectorySeparatorChar));
        var json = await File.ReadAllTextAsync(manifestPath, CancellationToken.None);
        await File.WriteAllTextAsync(manifestPath, json.Replace("\"applicationConsistent\":false", "\"applicationConsistent\":true"), CancellationToken.None);

        await Assert.ThrowsAsync<BackupArtifactIntegrityException>(() =>
            reader.ReadManifestAsync(result.ManifestKey, storage, CancellationToken.None));
    }

    [Fact]
    public async Task Frame_size_outside_bounded_range_is_rejected()
    {
        Directory.CreateDirectory(_root);
        var writer = new BackupArtifactWriter(CreateProtector());
        var storage = new LocalBackupStorageProvider(new LocalBackupStorageOptions(_root));
        using var source = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(
            source,
            new BackupArtifactWriteRequest("run", new BackupArtifactWriteOptions
            {
                FrameSizeBytes = 9 * 1024 * 1024,
                ObjectSizeBytes = 9 * 1024 * 1024,
                StagingDirectory = _root
            }),
            storage,
            CancellationToken.None));
    }

    private static BackupSecretProtector CreateProtector() =>
        new(DataProtectionProvider.Create($"CitusManager.BackupArtifactTests.{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
