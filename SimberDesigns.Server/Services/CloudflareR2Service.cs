using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

public interface ICloudflareR2Service
{
    TimeSpan UrlLifetime { get; }
    Task<string> GetPresignedDownloadUrlAsync(string objectKey, CancellationToken cancellationToken = default);
    Task<string> GetPresignedUploadUrlAsync(string objectKey, string contentType, CancellationToken cancellationToken = default);
    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default);
}

public sealed class CloudflareR2Service : ICloudflareR2Service, IDisposable
{
    private readonly CloudflareR2Options _options;
    private readonly AmazonS3Client? _s3;
    private readonly ILogger<CloudflareR2Service> _logger;

    public CloudflareR2Service(IOptions<CloudflareR2Options> options, ILogger<CloudflareR2Service> logger)
    {
        _options = options.Value;
        _logger = logger;
        UrlLifetime = TimeSpan.FromMinutes(Math.Max(1, _options.PresignedUrlExpiryMinutes));

        if (!_options.UseFakeClient)
        {
            var config = new AmazonS3Config
            {
                ServiceURL = _options.ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "auto"
            };
            _s3 = new AmazonS3Client(_options.AccessKeyId, _options.SecretAccessKey, config);
        }
    }

    public TimeSpan UrlLifetime { get; }

    public Task<string> GetPresignedDownloadUrlAsync(string objectKey, CancellationToken cancellationToken = default)
        => CreatePresignedUrlAsync(objectKey, HttpVerb.GET, "application/octet-stream", UrlLifetime, cancellationToken);

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, string contentType, CancellationToken cancellationToken = default)
        => CreatePresignedUrlAsync(
            objectKey,
            HttpVerb.PUT,
            contentType,
            TimeSpan.FromMinutes(Math.Max(1, _options.UploadExpiryMinutes)),
            cancellationToken);

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (_options.UseFakeClient || _s3 is null)
        {
            _logger.LogInformation("R2 falso: se omite la subida de {Key}.", objectKey);
            return;
        }

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType
        };

        await _s3.PutObjectAsync(request, cancellationToken);
    }

    private Task<string> CreatePresignedUrlAsync(
        string objectKey,
        HttpVerb verb,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expires = DateTime.UtcNow.Add(lifetime);

        if (_options.UseFakeClient || _s3 is null)
        {
            var url =
                $"https://r2.dev.simber-designs.local/{Uri.EscapeDataString(objectKey)}?verb={verb}&exp={new DateTimeOffset(expires).ToUnixTimeSeconds()}&sig=dev";
            return Task.FromResult(url);
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            Verb = verb,
            Expires = expires
        };
        if (verb == HttpVerb.PUT)
        {
            request.ContentType = contentType;
        }

        return Task.FromResult(_s3.GetPreSignedURL(request));
    }

    public void Dispose() => _s3?.Dispose();
}
