namespace CitusManager.Services.BackupStorage;

public sealed record LocalBackupStorageOptions(string RootPath);

public sealed class LocalBackupStorageProvider : IBackupStorageProvider
{
    private readonly string _rootPath;
    private readonly string _rootPrefix;

    public LocalBackupStorageProvider(LocalBackupStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = Path.GetFullPath(options.RootPath);
        _rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_rootPath);
    }

    public string ProviderType => "Local";

    public async Task WriteAsync(
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        EnsureNoSymbolicLinks(path);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var actualLength = new FileInfo(temporaryPath).Length;
            if (actualLength != contentLength)
            {
                throw new InvalidDataException($"Expected {contentLength} bytes but received {actualLength} bytes.");
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ResolvePath(key),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolvePath(key)));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolvePath(key));
        return Task.CompletedTask;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var key = $".health/{Guid.NewGuid():N}";
        await using var payload = new MemoryStream([0x43, 0x4d]);
        await WriteAsync(key, payload, payload.Length, "application/octet-stream", cancellationToken);
        await using var read = await OpenReadAsync(key, cancellationToken);
        if (read.ReadByte() != 0x43)
        {
            throw new IOException("Local backup storage read verification failed.");
        }

        await DeleteAsync(key, cancellationToken);
    }

    private string ResolvePath(string key)
    {
        var relative = BackupStorageKey.Validate(key).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_rootPath, relative));
        if (!path.StartsWith(_rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key escapes configured root.", nameof(key));
        }

        EnsureNoSymbolicLinks(path);
        return path;
    }

    private void EnsureNoSymbolicLinks(string path)
    {
        var current = _rootPath;
        RejectSymbolicLink(current);
        foreach (var segment in Path.GetRelativePath(_rootPath, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectSymbolicLink(current);
        }
    }

    private static void RejectSymbolicLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Backup storage path contains a symbolic link.");
        }
    }
}
