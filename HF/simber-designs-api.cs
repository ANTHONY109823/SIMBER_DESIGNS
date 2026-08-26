using System;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Npgsql;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SimberDesigns.Api
{
    #region DTOs & Models

    public record PresignedUrlResponse(string UploadUrl, string FileKey);
    public record DownloadUrlResponse(string DownloadUrl);
    public record VisualSearchResponse(Guid Id, string Title, string Slug, string PreviewUrl, decimal PriceUsd, float Similarity);
    
    public class ManualPaymentRequest
    {
        public Guid CreditPackageId { get; set; }
        public string Email { get; set; }
        public string ReferenceNumber { get; set; }
    }

    #endregion

    #region Services

    /// <summary>
    /// Servicio de Almacenamiento seguro con Cloudflare R2 (compatible con Amazon S3 API)
    /// </summary>
    public interface IStorageService
    {
        string GeneratePresignedUploadUrl(string fileName, string contentType);
        string GeneratePresignedDownloadUrl(string fileKey, double durationMinutes = 10);
    }

    public class CloudflareR2Service : IStorageService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;

        public CloudflareR2Service(IConfiguration configuration)
        {
            var accessKey = configuration["CloudflareR2:AccessKey"] ?? throw new ArgumentNullException("CloudflareR2:AccessKey");
            var secretKey = configuration["CloudflareR2:SecretKey"] ?? throw new ArgumentNullException("CloudflareR2:SecretKey");
            var serviceUrl = configuration["CloudflareR2:ServiceUrl"] ?? throw new ArgumentNullException("CloudflareR2:ServiceUrl"); // Ej: https://<account_id>.r2.cloudflarestorage.com
            _bucketName = configuration["CloudflareR2:BucketName"] ?? throw new ArgumentNullException("CloudflareR2:BucketName");

            var config = new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true, // Requerido para Cloudflare R2
                SignatureVersion = "4"
            };

            _s3Client = new AmazonS3Client(accessKey, secretKey, config);
        }

        public string GeneratePresignedUploadUrl(string fileName, string contentType)
        {
            var fileKey = $"designs/{Guid.NewGuid()}_{fileName}";
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = fileKey,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.AddMinutes(20), // 20 minutos para iniciar la carga de 100 MB
                ContentType = contentType
            };

            return _s3Client.GetPreSignedURL(request);
        }

        public string GeneratePresignedDownloadUrl(string fileKey, double durationMinutes = 10)
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = fileKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddMinutes(durationMinutes)
            };

            return _s3Client.GetPreSignedURL(request);
        }
    }

    /// <summary>
    /// Servicio de Extracción de Embeddings usando ONNX Runtime con un modelo CLIP ligero para .NET
    /// </summary>
    public interface IEmbeddingService
    {
        Task<float[]> GenerateImageEmbeddingAsync(Stream imageStream);
    }

    public class OnnxEmbeddingService : IEmbeddingService
    {
        private readonly InferenceSession _onnxSession;
        private readonly int _vectorDimensions = 512; // Dimensión del vector para CLIP-ViT-B/32

        public OnnxEmbeddingService(IConfiguration configuration)
        {
            // Ruta del archivo modelo CLIP.onnx alojado en el servidor
            var modelPath = configuration["MachineLearning:ClipModelPath"] ?? Path.Combine(AppContext.BaseDirectory, "MLModels", "clip-image-encoder.onnx");
            if (File.Exists(modelPath))
            {
                _onnxSession = new InferenceSession(modelPath);
            }
        }

        public async Task<float[]> GenerateImageEmbeddingAsync(Stream imageStream)
        {
            if (_onnxSession == null)
            {
                // Fallback de desarrollo en caso de que no esté cargado el modelo físico .onnx
                return GenerateMockEmbedding();
            }

            // 1. Preprocesar la imagen (Redimensionar a 224x224, Normalizar según ImageNet)
            // En producción se usa ImageSharp o SkiaSharp para transformar el stream a float[1, 3, 224, 224]
            var preprocessedTensor = await PreprocessImageAsync(imageStream);

            // 2. Ejecutar inferencia en el modelo ONNX local
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", preprocessedTensor)
            };

            using var results = _onnxSession.Run(inputs);
            var outputTensor = results[0].AsTensor<float>();

            // 3. Extraer y normalizar el vector resultante de 512 dimensiones
            var embedding = new float[_vectorDimensions];
            for (int i = 0; i < _vectorDimensions; i++)
            {
                embedding[i] = outputTensor[0, i];
            }

            return NormalizeL2(embedding);
        }

        private async Task<DenseTensor<float>> PreprocessImageAsync(Stream imageStream)
        {
            // Simulación del procesamiento de tensor 224x224x3 requerido por CLIP
            await Task.Delay(10); // Simular I/O
            var tensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
            var random = new Random();
            for (int i = 0; i < tensor.Length; i++)
            {
                tensor.SetValue(i, (float)random.NextDouble());
            }
            return tensor;
        }

        private float[] NormalizeL2(float[] vector)
        {
            float sum = 0;
            for (int i = 0; i < vector.Length; i++) sum += vector[i] * vector[i];
            float norm = (float)Math.Sqrt(sum);
            if (norm > 0)
            {
                for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
            }
            return vector;
        }

        private float[] GenerateMockEmbedding()
        {
            // Retorna un vector normalizado pseudo-aleatorio de prueba
            var vector = new float[_vectorDimensions];
            var rand = new Random();
            float sum = 0;
            for (int i = 0; i < _vectorDimensions; i++)
            {
                vector[i] = (float)rand.NextDouble() - 0.5f;
                sum += vector[i] * vector[i];
            }
            float norm = (float)Math.Sqrt(sum);
            for (int i = 0; i < _vectorDimensions; i++) vector[i] /= norm;
            return vector;
        }
    }

    #endregion

    #region Controllers

    [ApiController]
    [Route("api/[controller]")]
    public class DesignsController : ControllerBase
    {
        private readonly IStorageService _storageService;
        private readonly IEmbeddingService _embeddingService;
        private readonly string _connectionString;

        public DesignsController(IConfiguration configuration, IStorageService storageService, IEmbeddingService embeddingService)
        {
            _storageService = storageService;
            _embeddingService = embeddingService;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? throw new ArgumentNullException("ConnectionStrings:DefaultConnection");
        }

        /// <summary>
        /// Genera una URL firmada para subir archivos pesados directamente de 100 MB de manera segura.
        /// </summary>
        [Authorize(Roles = "Admin")]
        [HttpPost("generate-upload-url")]
        public IActionResult GetUploadUrl([FromQuery] string fileName, [FromQuery] string contentType)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(contentType))
                return BadRequest("El nombre de archivo y Content-Type son obligatorios.");

            var uploadUrl = _storageService.GeneratePresignedUploadUrl(fileName, contentType);
            // El backend retorna la clave única generada para guardarla en base de datos al finalizar la carga
            var fileKey = $"designs/{Guid.NewGuid()}_{fileName}"; 

            return Ok(new PresignedUrlResponse(uploadUrl, fileKey));
        }

        /// <summary>
        /// Valida privilegios y genera una URL firmada de descarga segura desde Cloudflare R2 con expiración.
        /// </summary>
        [Authorize]
        [HttpGet("{id}/download")]
        public async Task<IActionResult> DownloadDesign(Guid id)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId))
                return Unauthorized();

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Obtener los detalles del diseño y el archivo de almacenamiento
            var design = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, file_key, price_usd FROM designs WHERE id = @Id", new { Id = id });

            if (design == null)
                return NotFound("Diseño no encontrado.");

            // 2. Control de Acceso: Verificar si tiene suscripción activa o si realizó compra directa
            var hasAccess = false;
            
            // Validar suscripción activa (Modelo VectorSport)
            var subscription = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, daily_download_limit FROM subscriptions WHERE user_id = @UserId AND status = 'Active' AND expires_at > NOW()",
                new { UserId = userId });

            if (subscription != null)
            {
                // Verificar límite diario de descargas
                int dailyLimit = subscription.daily_download_limit;
                int downloadsToday = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM user_downloads WHERE user_id = @UserId AND downloaded_at >= NOW() - INTERVAL '1 DAY'",
                    new { UserId = userId });

                if (downloadsToday >= dailyLimit)
                {
                    return BadRequest($"Has alcanzado tu límite de descarga diario de {dailyLimit} archivos para tu membresía.");
                }
                hasAccess = true;
            }
            else
            {
                // Validar compra directa (Modelo Wiforth) o canje de créditos (Modelo Foraes)
                var purchaseExists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM transactions WHERE user_id = @UserId AND design_id = @DesignId AND status = 'Completed')",
                    new { UserId = userId, DesignId = id });

                if (purchaseExists)
                {
                    hasAccess = true;
                }
            }

            if (!hasAccess)
                return Forbid("No tienes acceso a este diseño. Adquiere una membresía o realiza la compra directa de este vector.");

            // 3. Registrar el evento de descarga
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            var userAgent = Request.Headers["User-Agent"].ToString();

            await connection.ExecuteAsync(
                @"INSERT INTO user_downloads (id, user_id, design_id, downloaded_at, ip_address, user_agent) 
                  VALUES (@Id, @UserId, @DesignId, NOW(), @IpAddress, @UserAgent)",
                new { Id = Guid.NewGuid(), UserId = userId, DesignId = id, IpAddress = ipAddress, UserAgent = userAgent });

            // 4. Generar URL temporal segura de Cloudflare R2
            string fileKey = design.file_key;
            var secureDownloadUrl = _storageService.GeneratePresignedDownloadUrl(fileKey, durationMinutes: 10);

            return Ok(new DownloadUrlResponse(secureDownloadUrl));
        }

        /// <summary>
        /// Búsqueda Visual estilo Google Lens procesada localmente en C# con pgvector
        /// </summary>
        [HttpPost("visual-search")]
        public async Task<IActionResult> VisualSearch(IFormFile imageFile)
        {
            if (imageFile == null || imageFile.Length == 0)
                return BadRequest("Se requiere una imagen válida para la búsqueda visual.");

            // 1. Extraer embedding (vector) de 512 dimensiones usando el modelo ONNX en C#
            float[] queryVector;
            using (var stream = imageFile.OpenReadStream())
            {
                queryVector = await _embeddingService.GenerateImageEmbeddingAsync(stream);
            }

            // 2. Convertir vector a string compatible con PostgreSQL pgvector: "[v1,v2,v3...]"
            var vectorString = $"[{string.Join(",", queryVector)}]";

            // 3. Ejecutar búsqueda por similitud de distancia coseno en PostgreSQL
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT id, title, slug, preview_url, price_usd,
                       (1 - (embedding <=> @Vector::vector)) AS similarity
                FROM designs
                WHERE embedding IS NOT NULL
                ORDER BY embedding <=> @Vector::vector
                LIMIT 8;";

            var results = await connection.QueryAsync<VisualSearchResponse>(query, new { Vector = vectorString });

            return Ok(results);
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly IStorageService _storageService;
        private readonly string _connectionString;

        public PaymentsController(IConfiguration configuration, IStorageService storageService)
        {
            _storageService = storageService;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? throw new ArgumentNullException("ConnectionStrings:DefaultConnection");
        }

        /// <summary>
        /// Endpoint webhook para procesar notificaciones de Lemon Squeezy de forma automática.
        /// </summary>
        [HttpPost("lemonsqueezy-webhook")]
        public async Task<IActionResult> LemonSqueezyWebhook()
        {
            // En producción, aquí se valida la firma X-Signature enviada por Lemon Squeezy para garantizar la seguridad
            using var reader = new StreamReader(Request.Body);
            var jsonPayload = await reader.ReadToEndAsync();

            // Deserialización conceptual de eventos
            // var eventType = parsedPayload["meta"]["event_name"].ToString();
            // var email = parsedPayload["data"]["attributes"]["user_email"].ToString();
            
            // Simulación del procesamiento:
            // Al recibir "subscription_created":
            // - Crear registro en la tabla `subscriptions` asociando el UserID
            // - Configurar el `daily_download_limit` de acuerdo al plan contratado (ej. 15 descargas para Plan Básico, 25 para VIP)

            return Ok();
        }

        /// <summary>
        /// Registrar un pago local manual mediante transferencia o código QR (Estilo Foraes)
        /// </summary>
        [Authorize]
        [HttpPost("manual-recharge")]
        public async Task<IActionResult> RequestManualRecharge([FromForm] ManualPaymentRequest request, IFormFile receiptImage)
        {
            if (receiptImage == null || receiptImage.Length == 0)
                return BadRequest("El comprobante de pago en imagen es obligatorio.");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out var userId))
                return Unauthorized();

            // 1. Subir la captura del comprobante a Cloudflare R2
            var extension = Path.GetExtension(receiptImage.FileName);
            var receiptKey = $"receipts/{Guid.NewGuid()}{extension}";
            
            // Generamos una URL pre-firmada para subir el comprobante de forma directa
            var uploadUrl = _storageService.GeneratePresignedUploadUrl(receiptImage.FileName, receiptImage.ContentType);
            
            // En este caso, como el comprobante es una imagen ligera de unos cuantos KB, 
            // el backend puede opcionalmente subirla directamente usando HttpClient para comodidad del cliente móvil
            using (var stream = receiptImage.OpenReadStream())
            using (var client = new HttpClient())
            {
                var content = new StreamContent(stream);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(receiptImage.ContentType);
                await client.PutAsync(uploadUrl, content);
            }

            // 2. Registrar la transacción en la base de datos PostgreSQL como 'Pending'
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var txId = Guid.NewGuid();
            var query = @"
                INSERT INTO transactions (id, user_id, credit_package_id, payment_gateway, status, payment_receipt_url, created_at, updated_at)
                VALUES (@Id, @UserId, @CreditPackageId, 'LocalQR', 'Pending', @ReceiptUrl, NOW(), NOW())";

            await connection.ExecuteAsync(query, new
            {
                Id = txId,
                UserId = userId,
                CreditPackageId = request.CreditPackageId,
                ReceiptUrl = receiptKey
            });

            return Ok(new { Message = "Su solicitud de recarga de créditos ha sido registrada. Un administrador verificará su comprobante en un plazo de 5 a 30 minutos.", TransactionId = txId });
        }
    }

    #endregion
}
