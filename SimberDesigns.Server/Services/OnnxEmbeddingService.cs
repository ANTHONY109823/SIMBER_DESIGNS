using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using SimberDesigns.Server.Options;

namespace SimberDesigns.Server.Services;

public interface IEmbeddingService
{
    int Dimensions { get; }
    Task<float[]> EmbedImageAsync(Stream image, CancellationToken cancellationToken = default);
}

public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    public const int ClipDimensions = 512;

    private readonly ILogger<OnnxEmbeddingService> _logger;
    private readonly InferenceSession? _session;
    private readonly string _inputName;

    public OnnxEmbeddingService(IOptions<OnnxOptions> options, ILogger<OnnxEmbeddingService> logger)
    {
        _logger = logger;
        _inputName = options.Value.InputName;
        Dimensions = ClipDimensions;

        var path = options.Value.ModelPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _session = new InferenceSession(path);
            _logger.LogInformation("Modelo ONNX CLIP cargado desde {Path}.", path);
        }
        else
        {
            _logger.LogWarning(
                "No hay modelo ONNX en {Path}. Se usará un embedding de desarrollo (hash normalizado de 512 dimensiones).",
                path);
        }
    }

    public int Dimensions { get; }

    public Task<float[]> EmbedImageAsync(Stream image, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_session is not null)
        {
            throw new InvalidOperationException(
                "El modelo CLIP real está presente, pero el pipeline de preprocesamiento 224x224 debe configurarse con los nombres de entrada/salida del .onnx.");
        }

        return Task.FromResult(HashEmbedding(image));
    }

    public static float[] HashEmbedding(Stream image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        int read;
        while ((read = image.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, read));
        }

        var hash = sha.GetHashAndReset();
        var values = new float[ClipDimensions];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ((hash[i % hash.Length] / 255f) * 2f) - 1f;
        }

        var norm = MathF.Sqrt(values.Sum(v => v * v));
        if (norm == 0)
        {
            values[0] = 1;
            return values;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }

        return values;
    }

    public void Dispose() => _session?.Dispose();
}
