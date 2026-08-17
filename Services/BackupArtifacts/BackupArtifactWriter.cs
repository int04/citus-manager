using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CitusManager.Security;
using CitusManager.Services.BackupStorage;

namespace CitusManager.Services.BackupArtifacts;

public sealed class BackupArtifactWriter(IBackupSecretProtector secretProtector) : IBackupArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BackupArtifactWriteResult> WriteAsync(
        Stream pgDumpArchive,
        BackupArtifactWriteRequest request,
        IBackupStorageProvider destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pgDumpArchive);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);

        var prefix = BackupStorageKey.Validate(request.ArtifactPrefix);
        var options = request.Options ?? new BackupArtifactWriteOptions();
        ValidateOptions(options);
        Directory.CreateDirectory(options.StagingDirectory);

        var dataKey = RandomNumberGenerator.GetBytes(32);
        var protectedDataKey = secretProtector.ProtectBytes(dataKey);
        var objects = new List<BackupArtifactObjectManifest>();
        long archiveLength = 0;
        using var archiveHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(dataKey, 16);
        var buffer = ArrayPool<byte>.Shared.Rent(options.FrameSizeBytes);

        try
        {
            var reachedEnd = false;
            for (var objectIndex = 0; !reachedEnd; objectIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryPath = Path.Combine(options.StagingDirectory, $"citus-backup-{Guid.NewGuid():N}.part");
                try
                {
                    var frameManifests = new List<BackupArtifactFrameManifest>();
                    long objectPlaintextLength = 0;
                    await using (var objectFile = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        using var objectHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await WriteAndHashAsync(objectFile, BackupArtifactFormatHeader(), objectHash, cancellationToken);

                        for (var frameIndex = 0; objectPlaintextLength < options.ObjectSizeBytes; frameIndex++)
                        {
                            var remaining = options.ObjectSizeBytes - objectPlaintextLength;
                            var wanted = (int)Math.Min(options.FrameSizeBytes, remaining);
                            var read = await ReadAtMostAsync(pgDumpArchive, buffer.AsMemory(0, wanted), cancellationToken);
                            if (read == 0)
                            {
                                reachedEnd = true;
                                break;
                            }

                            var plaintext = buffer.AsSpan(0, read);
                            archiveHash.AppendData(plaintext);
                            archiveLength += read;
                            objectPlaintextLength += read;

                            var nonce = new byte[12];
                            var tag = new byte[16];
                            var payload = new byte[read];
                            if (options.Encrypt)
                            {
                                RandomNumberGenerator.Fill(nonce);
                                aes.Encrypt(nonce, plaintext, payload, tag);
                            }
                            else
                            {
                                plaintext.CopyTo(payload);
                            }

                            var frameHeader = new byte[BackupArtifactFormat.FrameHeaderLength];
                            BinaryPrimitives.WriteInt32LittleEndian(frameHeader, read);
                            nonce.CopyTo(frameHeader, 4);
                            tag.CopyTo(frameHeader, 16);
                            using var frameHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            frameHash.AppendData(frameHeader);
                            frameHash.AppendData(payload);
                            await WriteAndHashAsync(objectFile, frameHeader, objectHash, cancellationToken);
                            await WriteAndHashAsync(objectFile, payload, objectHash, cancellationToken);
                            frameManifests.Add(new BackupArtifactFrameManifest(
                                frameIndex,
                                read,
                                frameHeader.Length + payload.Length,
                                BackupArtifactFormat.Hex(frameHash.GetHashAndReset())));
                        }

                        if (objectPlaintextLength == 0)
                        {
                            break;
                        }

                        await objectFile.FlushAsync(cancellationToken);
                        var storedLength = objectFile.Length;
                        var objectKey = $"{prefix}/objects/{objectIndex:D8}.cmba";
                        var objectSha = BackupArtifactFormat.Hex(objectHash.GetHashAndReset());
                        objectFile.Position = 0;
                        await destination.WriteAsync(
                            objectKey,
                            objectFile,
                            storedLength,
                            "application/vnd.citus-manager.backup-object",
                            cancellationToken);
                        objects.Add(new BackupArtifactObjectManifest(
                            objectIndex,
                            objectKey,
                            objectPlaintextLength,
                            storedLength,
                            objectSha,
                            frameManifests));
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            var manifest = new BackupArtifactManifest(
                BackupArtifactFormat.Version,
                DateTimeOffset.UtcNow,
                options.Encrypt,
                request.ApplicationConsistent,
                options.FrameSizeBytes,
                options.ObjectSizeBytes,
                archiveLength,
                BackupArtifactFormat.Hex(archiveHash.GetHashAndReset()),
                protectedDataKey,
                objects,
                request.CitusMetadata,
                null);
            var unsignedJson = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var hmac = HMACSHA256.HashData(dataKey, unsignedJson);
            manifest = manifest with { HmacSha256 = Convert.ToBase64String(hmac) };
            var manifestJson = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            var manifestKey = $"{prefix}/manifest.v1.json";
            await using var manifestStream = new MemoryStream(manifestJson, writable: false);
            await destination.WriteAsync(
                manifestKey,
                manifestStream,
                manifestJson.Length,
                "application/vnd.citus-manager.backup-manifest+json",
                cancellationToken);
            return new BackupArtifactWriteResult(manifestKey, manifest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateOptions(BackupArtifactWriteOptions options)
    {
        if (options.FrameSizeBytes is < 4 * 1024 * 1024 or > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options.FrameSizeBytes), "Frame size must be between 4 MiB and 8 MiB.");
        }

        if (options.ObjectSizeBytes < options.FrameSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ObjectSizeBytes), "Object size must be at least one frame.");
        }
    }

    private static byte[] BackupArtifactFormatHeader()
    {
        var header = new byte[BackupArtifactFormat.HeaderLength];
        BackupArtifactFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), BackupArtifactFormat.Version);
        return header;
    }

    private static async Task WriteAndHashAsync(
        Stream stream,
        ReadOnlyMemory<byte> value,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        hash.AppendData(value.Span);
        await stream.WriteAsync(value, cancellationToken);
    }

    private static async Task<int> ReadAtMostAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await source.ReadAsync(destination[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
