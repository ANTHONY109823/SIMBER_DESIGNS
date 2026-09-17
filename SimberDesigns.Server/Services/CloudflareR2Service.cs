using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

public interface ICloudflareR2Service
{
    bool IsEnabled { get; }
    TimeSpan UrlLifetime { get; }
    TimeSpan PreviewUrlLifetime { get; }
    string? PublicBaseUrl { get; }
    Task<string> GetPresignedDownloadUrlAsync(
        string objectKey,
        CancellationToken cancellationToken = default,
        string? downloadFileName = null,
        TimeSpan? lifetime = null);
    Task<string> GetPresignedUploadUrlAsync(string objectKey, string contentType, CancellationToken cancellationToken = default);
    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default);
    Task<(long Length, DateTime? LastModified)?> GetObjectInfoAsync(string objectKey, CancellationToken cancellationToken = default);
    string? TryBuildPublicUrl(string objectKey);
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
        PreviewUrlLifetime = TimeSpan.FromMinutes(Math.Max(5, _options.PreviewUrlExpiryMinutes));
        PublicBaseUrl = string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? null
            : _options.PublicBaseUrl.Trim().TrimEnd('/');

        if (_options.UseFakeClient)
        {
            _logger.LogWarning("Cloudflare R2 en modo falso (UseFakeClient=true). Los archivos irán al disco local.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.AccountId)
            || string.IsNullOrWhiteSpace(_options.AccessKeyId)
            || string.IsNullOrWhiteSpace(_options.SecretAccessKey)
            || string.IsNullOrWhiteSpace(_options.BucketName)
            || string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            throw new InvalidOperationException(
                "CloudflareR2: UseFakeClient=false pero faltan AccountId, AccessKeyId, SecretAccessKey, BucketName o ServiceUrl.");
        }

        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl.Trim(),
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        };
        _s3 = new AmazonS3Client(_options.AccessKeyId.Trim(), _options.SecretAccessKey.Trim(), config);
        _logger.LogInformation("Cloudflare R2 activo. Bucket={Bucket}", _options.BucketName);
    }

    public bool IsEnabled => !_options.UseFakeClient && _s3 is not null;
    public TimeSpan UrlLifetime { get; }
    public TimeSpan PreviewUrlLifetime { get; }
    public string? PublicBaseUrl { get; }

    public Task<string> GetPresignedDownloadUrlAsync(
        string objectKey,
        CancellationToken cancellationToken = default,
        string? downloadFileName = null,
        TimeSpan? lifetime = null)
        => CreatePresignedUrlAsync(
            objectKey,
            HttpVerb.GET,
            "application/octet-stream",
            lifetime ?? UrlLifetime,
            cancellationToken,
            downloadFileName);

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, string contentType, CancellationToken cancellationToken = default)
        => CreatePresignedUrlAsync(
            objectKey,
            HttpVerb.PUT,
            contentType,
            TimeSpan.FromMinutes(Math.Max(1, _options.UploadExpiryMinutes)),
            cancellationToken);

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _s3 is null)
        {
            _logger.LogInformation("R2 falso: se omite la subida de {Key}.", objectKey);
            return;
        }

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };

        await _s3.PutObjectAsync(request, cancellationToken);
        _logger.LogInformation("R2 subido: {Key}", objectKey);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !IsEnabled || _s3 is null)
        {
            return;
        }

        try
        {
            await _s3.DeleteObjectAsync(_options.BucketName, objectKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo borrar {Key} en R2.", objectKey);
        }
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var info = await GetObjectInfoAsync(objectKey, cancellationToken);
        return info is not null;
    }

    public async Task<(long Length, DateTime? LastModified)?> GetObjectInfoAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !IsEnabled || _s3 is null)
        {
            return null;
        }

        try
        {
            var meta = await _s3.GetObjectMetadataAsync(_options.BucketName, objectKey, cancellationToken);
            return (meta.ContentLength, meta.LastModified);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string? TryBuildPublicUrl(string objectKey)
    {
        if (PublicBaseUrl is null || string.IsNullOrWhiteSpace(objectKey))
        {
            return null;
        }

        return $"{PublicBaseUrl}/{objectKey.TrimStart('/')}";
    }

    private Task<string> CreatePresignedUrlAsync(
        string objectKey,
        HttpVerb verb,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken,
        string? downloadFileName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("objectKey vacío.", nameof(objectKey));
        }

        var expires = DateTime.UtcNow.Add(lifetime);

        if (!IsEnabled || _s3 is null)
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

        if (verb == HttpVerb.GET && !string.IsNullOrWhiteSpace(downloadFileName))
        {
            var safe = downloadFileName.Replace("\"", "", StringComparison.Ordinal);
            request.ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = $"attachment; filename=\"{safe}\""
            };
        }

        return Task.FromResult(_s3.GetPreSignedURL(request));
    }

    public void Dispose() => _s3?.Dispose();
}
