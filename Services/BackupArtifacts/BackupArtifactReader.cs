using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CitusManager.Security;
using CitusManager.Services.BackupStorage;

namespace CitusManager.Services.BackupArtifacts;

public sealed class BackupArtifactReader(IBackupSecretProtector secretProtector) : IBackupArtifactReader
{
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BackupArtifactManifest> ReadManifestAsync(
        string manifestKey,
        IBackupStorageProvider source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var stream = await source.OpenReadAsync(BackupStorageKey.Validate(manifestKey), cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxManifestBytes)
            {
                throw new BackupArtifactIntegrityException("Backup manifest exceeds size limit.");
            }

            memory.Write(buffer, 0, read);
        }

        BackupArtifactManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupArtifactManifest>(memory.ToArray(), JsonOptions)
                ?? throw new BackupArtifactIntegrityException("Backup manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new BackupArtifactIntegrityException("Backup manifest JSON is invalid.", exception);
        }

        ValidateManifestShape(manifest);
        var dataKey = UnprotectDataKey(manifest);
        try
        {
            var unsigned = JsonSerializer.SerializeToUtf8Bytes(manifest with { HmacSha256 = null }, JsonOptions);
            var actualHmac = HMACSHA256.HashData(dataKey, unsigned);
            byte[] expectedHmac;
            try
            {
                expectedHmac = Convert.FromBase64String(manifest.HmacSha256!);
            }
            catch (FormatException exception)
            {
                throw new BackupArtifactIntegrityException("Backup manifest signature is invalid.", exception);
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedHmac, actualHmac))
            {
                throw new BackupArtifactIntegrityException("Backup manifest authentication failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        return manifest;
    }

    public async Task<BackupArtifactManifest> ReadToAsync(
        string manifestKey,
        IBackupStorageProvider source,
        Stream plaintextDestination,
        string? stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintextDestination);
        var manifest = await ReadManifestAsync(manifestKey, source, cancellationToken);
        var dataKey = UnprotectDataKey(manifest);
        var stageRoot = stagingDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(stageRoot);
        long archiveLength = 0;
        using var archiveHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            // Validate every committed object before releasing any plaintext to pg_restore.
            // Second pass below also revalidates staged bytes, protecting against replacement races.
            foreach (var objectManifest in manifest.Objects.OrderBy(item => item.Index))
            {
                await ValidateRemoteObjectAsync(objectManifest, source, cancellationToken);
            }

            foreach (var objectManifest in manifest.Objects.OrderBy(item => item.Index))
            {
                var temporaryPath = Path.Combine(stageRoot, $"citus-restore-{Guid.NewGuid():N}.part");
                try
                {
                    await StageAndValidateObjectAsync(objectManifest, source, temporaryPath, cancellationToken);
                    await using var objectFile = new FileStream(
                        temporaryPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    archiveLength += await DecryptObjectAsync(
                        objectFile,
                        objectManifest,
                        manifest.Encrypted,
                        dataKey,
                        plaintextDestination,
                        archiveHash,
                        cancellationToken);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            var archiveDigest = archiveHash.GetHashAndReset();
            if (archiveLength != manifest.ArchivePlaintextLength ||
                !BackupArtifactFormat.FixedHexEquals(manifest.ArchiveSha256, archiveDigest))
            {
                throw new BackupArtifactIntegrityException("Restored archive length or checksum does not match manifest.");
            }

            return manifest;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static async Task ValidateRemoteObjectAsync(
        BackupArtifactObjectManifest manifest,
        IBackupStorageProvider source,
        CancellationToken cancellationToken)
    {
        await using var remote = await source.OpenReadAsync(BackupStorageKey.Validate(manifest.Key), cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (length != manifest.StoredLength ||
            !BackupArtifactFormat.FixedHexEquals(manifest.Sha256, hash.GetHashAndReset()))
        {
            throw new BackupArtifactIntegrityException($"Backup object {manifest.Index} length or checksum mismatch.");
        }
    }

    private static async Task StageAndValidateObjectAsync(
        BackupArtifactObjectManifest manifest,
        IBackupStorageProvider source,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using var remote = await source.OpenReadAsync(BackupStorageKey.Validate(manifest.Key), cancellationToken);
        await using var local = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
                hash.AppendData(buffer, 0, read);
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        await local.FlushAsync(cancellationToken);
        var digest = hash.GetHashAndReset();
        if (length != manifest.StoredLength || !BackupArtifactFormat.FixedHexEquals(manifest.Sha256, digest))
        {
            throw new BackupArtifactIntegrityException($"Backup object {manifest.Index} length or checksum mismatch.");
        }
    }

    private static async Task<long> DecryptObjectAsync(
        Stream input,
        BackupArtifactObjectManifest objectManifest,
        bool encrypted,
        byte[] dataKey,
        Stream output,
        IncrementalHash archiveHash,
        CancellationToken cancellationToken)
    {
        await BackupArtifactFormat.ReadAndValidateHeaderAsync(input, cancellationToken);
        long plaintextLength = 0;
        using var aes = new AesGcm(dataKey, 16);
        foreach (var frameManifest in objectManifest.Frames.OrderBy(item => item.Index))
        {
            var header = new byte[BackupArtifactFormat.FrameHeaderLength];
            await input.ReadExactlyAsync(header, cancellationToken);
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (frameLength != frameManifest.PlaintextLength ||
                frameManifest.StoredLength != header.Length + frameLength ||
                frameLength is < 0 or > 8 * 1024 * 1024)
            {
                throw new BackupArtifactIntegrityException($"Backup frame {objectManifest.Index}:{frameManifest.Index} metadata mismatch.");
            }

            var payload = new byte[frameLength];
            await input.ReadExactlyAsync(payload, cancellationToken);
            using var frameHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            frameHash.AppendData(header);
            frameHash.AppendData(payload);
            if (!BackupArtifactFormat.FixedHexEquals(frameManifest.Sha256, frameHash.GetHashAndReset()))
            {
                throw new BackupArtifactIntegrityException($"Backup frame {objectManifest.Index}:{frameManifest.Index} checksum mismatch.");
            }

            byte[] plaintext;
            if (encrypted)
            {
                plaintext = new byte[frameLength];
                try
                {
                    aes.Decrypt(header.AsSpan(4, 12), payload, header.AsSpan(16, 16), plaintext);
                }
                catch (AuthenticationTagMismatchException exception)
                {
                    throw new BackupArtifactIntegrityException(
                        $"Backup frame {objectManifest.Index}:{frameManifest.Index} authentication failed.",
                        exception);
                }
            }
            else
            {
                plaintext = payload;
            }

            archiveHash.AppendData(plaintext);
            await output.WriteAsync(plaintext, cancellationToken);
            plaintextLength += plaintext.Length;
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (input.Position != input.Length || plaintextLength != objectManifest.PlaintextLength)
        {
            throw new BackupArtifactIntegrityException($"Backup object {objectManifest.Index} frame count or length mismatch.");
        }

        return plaintextLength;
    }

    private byte[] UnprotectDataKey(BackupArtifactManifest manifest)
    {
        try
        {
            var key = secretProtector.UnprotectBytes(manifest.ProtectedDataKey);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new BackupArtifactIntegrityException("Backup data key has invalid length.");
            }

            return key;
        }
        catch (BackupArtifactIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new BackupArtifactIntegrityException("Backup data key cannot be unwrapped.", exception);
        }
    }

    private static void ValidateManifestShape(BackupArtifactManifest manifest)
    {
        if (manifest.FormatVersion != BackupArtifactFormat.Version ||
            manifest.FrameSizeBytes is < 4 * 1024 * 1024 or > 8 * 1024 * 1024 ||
            manifest.ObjectSizeBytes < manifest.FrameSizeBytes ||
            manifest.ArchivePlaintextLength < 0 ||
            string.IsNullOrWhiteSpace(manifest.ProtectedDataKey) ||
            string.IsNullOrWhiteSpace(manifest.HmacSha256) ||
            manifest.Objects is null ||
            manifest.Objects.Count > 1_000_000)
        {
            throw new BackupArtifactIntegrityException("Backup manifest shape is invalid or unsupported.");
        }

        for (var index = 0; index < manifest.Objects.Count; index++)
        {
            var item = manifest.Objects[index];
            if (item.Index != index || item.PlaintextLength < 0 || item.StoredLength < BackupArtifactFormat.HeaderLength ||
                item.Frames is null || item.Frames.Select(frame => frame.Index).Where((value, frameIndex) => value != frameIndex).Any())
            {
                throw new BackupArtifactIntegrityException("Backup object or frame ordering is invalid.");
            }

            BackupStorageKey.Validate(item.Key);
        }
    }
}
