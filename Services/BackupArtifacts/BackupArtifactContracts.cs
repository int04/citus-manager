using System.Text.Json;
using CitusManager.Services.BackupStorage;

namespace CitusManager.Services.BackupArtifacts;

public sealed record BackupArtifactWriteOptions
{
    public const long DefaultObjectSizeBytes = 256L * 1024 * 1024;
    public const int DefaultFrameSizeBytes = 4 * 1024 * 1024;

    public long ObjectSizeBytes { get; init; } = DefaultObjectSizeBytes;
    public int FrameSizeBytes { get; init; } = DefaultFrameSizeBytes;
    public bool Encrypt { get; init; } = true;
    public string StagingDirectory { get; init; } = Path.GetTempPath();
}

public sealed record BackupArtifactWriteRequest(
    string ArtifactPrefix,
    BackupArtifactWriteOptions? Options = null,
    JsonElement? CitusMetadata = null,
    bool ApplicationConsistent = false);

public sealed record BackupArtifactFrameManifest(
    int Index,
    int PlaintextLength,
    int StoredLength,
    string Sha256);

public sealed record BackupArtifactObjectManifest(
    int Index,
    string Key,
    long PlaintextLength,
    long StoredLength,
    string Sha256,
    IReadOnlyList<BackupArtifactFrameManifest> Frames);

public sealed record BackupArtifactManifest(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    bool Encrypted,
    bool ApplicationConsistent,
    int FrameSizeBytes,
    long ObjectSizeBytes,
    long ArchivePlaintextLength,
    string ArchiveSha256,
    string ProtectedDataKey,
    IReadOnlyList<BackupArtifactObjectManifest> Objects,
    JsonElement? CitusMetadata,
    string? HmacSha256);

public sealed record BackupArtifactWriteResult(string ManifestKey, BackupArtifactManifest Manifest);

public interface IBackupArtifactWriter
{
    Task<BackupArtifactWriteResult> WriteAsync(
        Stream pgDumpArchive,
        BackupArtifactWriteRequest request,
        IBackupStorageProvider destination,
        CancellationToken cancellationToken);
}

public interface IBackupArtifactReader
{
    Task<BackupArtifactManifest> ReadManifestAsync(
        string manifestKey,
        IBackupStorageProvider source,
        CancellationToken cancellationToken);

    Task<BackupArtifactManifest> ReadToAsync(
        string manifestKey,
        IBackupStorageProvider source,
        Stream plaintextDestination,
        string? stagingDirectory,
        CancellationToken cancellationToken);
}

public sealed class BackupArtifactIntegrityException(string message, Exception? innerException = null)
    : IOException(message, innerException);
