using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Flowstate.Api.Features.Attachments;

/// <summary>
/// S3-compatible object storage: OVHcloud Object Storage in production (ADR-004), versitygw in dev and tests.
/// Attachments are disabled while <see cref="ServiceUrl"/> is unset.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>S3 endpoint, reachable by both the API and browsers (they upload straight to it).</summary>
    public string? ServiceUrl { get; set; }

    public string Bucket { get; set; } = "flowstate";
    public string Region { get; set; } = "us-east-1";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Create the bucket on first use; for dev and tests, where storage starts empty.</summary>
    public bool CreateBucket { get; set; }
}

/// <summary>Thin wrapper over the S3 client: presigned URLs, existence check, delete. Singleton.</summary>
public sealed class ObjectStorage : IDisposable
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client? _client;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketReady;

    public ObjectStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        if (_options.ServiceUrl is { Length: > 0 } url)
        {
            _client = new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = true,
                AuthenticationRegion = _options.Region,
                // Checksum headers are AWS-specific; S3-compatible stores may reject presigned uploads that carry them.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        }
    }

    public bool Enabled => _client is not null;

    public async Task<string> PresignUploadAsync(string key, string contentType, DateTimeOffset expires)
    {
        await EnsureBucketAsync();
        return await Client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expires.UtcDateTime,
            Protocol = Protocol,
        });
    }

    /// <summary>Images display inline; everything else (including SVG, which can carry script) downloads.</summary>
    public Task<string> PresignDownloadAsync(string key, string fileName, string contentType, DateTimeOffset expires)
    {
        var inline = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("svg", StringComparison.OrdinalIgnoreCase);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
            Protocol = Protocol,
        };
        request.ResponseHeaderOverrides.ContentType = inline ? contentType : "application/octet-stream";
        request.ResponseHeaderOverrides.ContentDisposition =
            $"{(inline ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        return Client.GetPreSignedURLAsync(request);
    }

    /// <summary>Stored object's size in bytes, or null when nothing was uploaded.</summary>
    public async Task<long?> SizeAsync(string key, CancellationToken ct)
    {
        try
        {
            var head = await Client.GetObjectMetadataAsync(_options.Bucket, key, ct);
            return head.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct) => Client.DeleteObjectAsync(_options.Bucket, key, ct);

    public void Dispose()
    {
        _client?.Dispose();
        _bucketLock.Dispose();
    }

    private AmazonS3Client Client => _client ?? throw new InvalidOperationException("Object storage is not configured.");

    private Protocol Protocol => _options.ServiceUrl!.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP;

    private async Task EnsureBucketAsync()
    {
        if (_bucketReady || !_options.CreateBucket)
        {
            return;
        }

        await _bucketLock.WaitAsync();
        try
        {
            if (!_bucketReady)
            {
                try
                {
                    await Client.PutBucketAsync(_options.Bucket);
                }
                catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
                {
                }

                _bucketReady = true;
            }
        }
        finally
        {
            _bucketLock.Release();
        }
    }
}
