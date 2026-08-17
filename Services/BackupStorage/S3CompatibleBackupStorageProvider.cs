using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CitusManager.Services.BackupStorage;

public sealed record S3CompatibleBackupStorageOptions(
    string Endpoint,
    string Bucket,
    string Region,
    string AccessKey,
    string SecretKey,
    string Prefix = "",
    int MultipartPartSizeBytes = 16 * 1024 * 1024);

public sealed class S3CompatibleBackupStorageProvider : IBackupStorageProvider, IDisposable
{
    private const int MinimumMultipartPartSize = 5 * 1024 * 1024;
    private readonly S3CompatibleBackupStorageOptions _options;
    private readonly IAmazonS3 _client;
    private readonly bool _ownsClient;

    public S3CompatibleBackupStorageProvider(S3CompatibleBackupStorageOptions options)
        : this(options, CreateClient(options), ownsClient: true)
    {
    }

    public S3CompatibleBackupStorageProvider(
        S3CompatibleBackupStorageOptions options,
        IAmazonS3 client,
        bool ownsClient = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            throw new ArgumentException("S3 bucket is required.", nameof(options));
        }

        if (options.MultipartPartSizeBytes < MinimumMultipartPartSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MultipartPartSizeBytes), "S3 multipart parts must be at least 5 MiB.");
        }
    }

    public string ProviderType => "S3Compatible";

    public async Task WriteAsync(
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var objectKey = ResolveKey(key);
        if (contentLength <= _options.MultipartPartSizeBytes)
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
                UseChunkEncoding = false
            }, cancellationToken);
            return;
        }

        var initiated = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            ContentType = contentType
        }, cancellationToken);
        var parts = new List<PartETag>();
        try
        {
            long remaining = contentLength;
            for (var partNumber = 1; remaining > 0; partNumber++)
            {
                var partLength = Math.Min(remaining, _options.MultipartPartSizeBytes);
                var response = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _options.Bucket,
                    Key = objectKey,
                    UploadId = initiated.UploadId,
                    PartNumber = partNumber,
                    PartSize = partLength,
                    InputStream = new LengthLimitedReadStream(content, partLength)
                }, cancellationToken);
                parts.Add(new PartETag(partNumber, response.ETag));
                remaining -= partLength;
            }

            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _options.Bucket,
                Key = objectKey,
                UploadId = initiated.UploadId,
                PartETags = parts
            }, cancellationToken);
        }
        catch
        {
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _options.Bucket,
                    Key = objectKey,
                    UploadId = initiated.UploadId
                }, CancellationToken.None);
            }
            catch
            {
                // Preserve original upload failure. Provider reconciliation can clean orphaned parts.
            }

            throw;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var response = await _client.GetObjectAsync(_options.Bucket, ResolveKey(key), cancellationToken);
        return new OwnedResponseStream(response.ResponseStream, response);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_options.Bucket, ResolveKey(key), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when ((int)exception.StatusCode == 404)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_options.Bucket, ResolveKey(key), cancellationToken);

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
                throw new IOException("S3-compatible storage read verification failed.");
            }
        }
        finally
        {
            await DeleteAsync(key, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private string ResolveKey(string key)
    {
        var safeKey = BackupStorageKey.Validate(key);
        var prefix = _options.Prefix.Trim('/');
        return prefix.Length == 0 ? safeKey : $"{prefix}/{safeKey}";
    }

    private static IAmazonS3 CreateClient(S3CompatibleBackupStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configuration = new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            AuthenticationRegion = options.Region,
            ForcePathStyle = true
        };
        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), configuration);
    }

    private sealed class LengthLimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public LengthLimitedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }

    private sealed class OwnedResponseStream(Stream inner, IDisposable owner) : Stream
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
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
