using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CitusManager.Services.BackupStorage;

public sealed record GoogleDriveBackupStorageOptions(
    string ClientId,
    string ClientSecret,
    string RefreshToken,
    string FolderId,
    int UploadChunkSizeBytes = 16 * 1024 * 1024);

public sealed class GoogleDriveBackupStorageProvider : IBackupStorageProvider
{
    private static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    private static readonly Uri DriveApi = new("https://www.googleapis.com/drive/v3/");
    private static readonly Uri DriveUploadApi = new("https://www.googleapis.com/upload/drive/v3/");
    private readonly GoogleDriveBackupStorageOptions _options;
    private readonly HttpClient _httpClient;

    public GoogleDriveBackupStorageProvider(GoogleDriveBackupStorageOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(options.FolderId))
        {
            throw new ArgumentException("Google Drive folder ID is required.", nameof(options));
        }

        if (options.UploadChunkSizeBytes <= 0 || options.UploadChunkSizeBytes % (256 * 1024) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.UploadChunkSizeBytes), "Google Drive chunk size must be a positive multiple of 256 KiB.");
        }
    }

    public string ProviderType => "GoogleDrive";

    public async Task WriteAsync(
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeKey = BackupStorageKey.Validate(key);
        var token = await GetAccessTokenAsync(cancellationToken);
        var existingId = await FindFileIdAsync(safeKey, token, cancellationToken);
        var relative = existingId is null ? "files?uploadType=resumable" : $"files/{Uri.EscapeDataString(existingId)}?uploadType=resumable";
        using var request = new HttpRequestMessage(existingId is null ? HttpMethod.Post : HttpMethod.Patch, new Uri(DriveUploadApi, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", contentType);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new
        {
            name = safeKey,
            parents = existingId is null ? new[] { _options.FolderId } : null,
            appProperties = new Dictionary<string, string> { ["citusManagerBackupKey"] = KeyFingerprint(safeKey) }
        });
        using var startResponse = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(startResponse, "start Google Drive resumable upload", cancellationToken);
        var uploadUri = startResponse.Headers.Location
            ?? throw new IOException("Google Drive resumable upload did not return a session URI.");

        var buffer = ArrayPool<byte>.Shared.Rent(_options.UploadChunkSizeBytes);
        try
        {
            long offset = 0;
            while (offset < contentLength)
            {
                var wanted = (int)Math.Min(buffer.Length, contentLength - offset);
                var read = await ReadExactlyUpToAsync(content, buffer.AsMemory(0, wanted), cancellationToken);
                if (read != wanted)
                {
                    throw new EndOfStreamException($"Google Drive upload expected {contentLength} bytes but source ended at {offset + read}.");
                }

                using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                chunk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                chunk.Content = new ByteArrayContent(buffer, 0, read);
                chunk.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, contentLength);
                using var chunkResponse = await _httpClient.SendAsync(chunk, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (chunkResponse.StatusCode != (HttpStatusCode)308 && !chunkResponse.IsSuccessStatusCode)
                {
                    await EnsureSuccessAsync(chunkResponse, "upload Google Drive chunk", cancellationToken);
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var safeKey = BackupStorageKey.Validate(key);
        var token = await GetAccessTokenAsync(cancellationToken);
        var fileId = await FindFileIdAsync(safeKey, token, cancellationToken)
            ?? throw new FileNotFoundException("Google Drive backup object was not found.", safeKey);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(DriveApi, $"files/{Uri.EscapeDataString(fileId)}?alt=media"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await EnsureSuccessAsync(response, "download Google Drive object", cancellationToken);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new OwnedHttpResponseStream(stream, response, request);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        return await FindFileIdAsync(BackupStorageKey.Validate(key), token, cancellationToken) is not null;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var safeKey = BackupStorageKey.Validate(key);
        var token = await GetAccessTokenAsync(cancellationToken);
        var fileId = await FindFileIdAsync(safeKey, token, cancellationToken);
        if (fileId is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(DriveApi, $"files/{Uri.EscapeDataString(fileId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "delete Google Drive object", cancellationToken);
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var key = $".health/{Guid.NewGuid():N}";
        await using var content = new MemoryStream([0x43, 0x4d]);
        await WriteAsync(key, content, content.Length, "application/octet-stream", cancellationToken);
        try
        {
            await using var read = await OpenReadAsync(key, cancellationToken);
            if (read.ReadByte() != 0x43)
            {
                throw new IOException("Google Drive storage read verification failed.");
            }
        }
        finally
        {
            await DeleteAsync(key, cancellationToken);
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["refresh_token"] = _options.RefreshToken,
                ["grant_type"] = "refresh_token"
            })
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "refresh Google Drive access token", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            throw new IOException("Google OAuth response did not contain an access token.");
        }

        return accessToken.GetString()!;
    }

    private async Task<string?> FindFileIdAsync(string key, string token, CancellationToken cancellationToken)
    {
        var escaped = KeyFingerprint(key);
        var query = $"appProperties has {{ key='citusManagerBackupKey' and value='{escaped}' }} and '{_options.FolderId.Replace("'", "\\'")}' in parents and trashed=false";
        var uri = new Uri(DriveApi, $"files?q={Uri.EscapeDataString(query)}&fields=files(id)&pageSize=2&spaces=drive");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "find Google Drive object", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var files = json.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : files[0].GetProperty("id").GetString();
    }

    private static string KeyFingerprint(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new HttpRequestException($"Failed to {operation}: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static async Task<int> ReadExactlyUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private sealed class OwnedHttpResponseStream(Stream inner, IDisposable response, IDisposable request) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
