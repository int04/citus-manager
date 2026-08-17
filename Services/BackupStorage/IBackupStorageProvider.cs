namespace CitusManager.Services.BackupStorage;

public interface IBackupStorageProvider
{
    string ProviderType { get; }
    Task WriteAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

public static class BackupStorageKey
{
    public static string Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Storage key is required.", nameof(key));
        }

        var normalized = key.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(key) || normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Storage key must be a safe relative path.", nameof(key));
        }

        return normalized;
    }
}
