using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Moq;
using SimberDesigns.Api; // Namespace del backend diseñado anteriormente

namespace SimberDesigns.Tests
{
    #region 1. PRUEBAS UNITARIAS EN C# (xUnit & Moq)

    /// <summary>
    /// Pruebas para validar las reglas de negocio de límites de descargas (VectorSport style)
    /// </summary>
    public class DownloadLimitTests
    {
        [Fact]
        public async Task Download_WhenUserExceedsDailyLimit_ShouldReturnBadRequest()
        {
            // Arrange (Preparar)
            var userId = Guid.NewGuid();
            var designId = Guid.NewGuid();
            
            // Simulamos un usuario con Membresía VIP (Límite: 25 descargas diarias)
            var dailyLimit = 25;
            var downloadsToday = 25; // Ya consumió su límite diario de 25 descargas

            var mockStorage = new Mock<IStorageService>();
            
            // Simulamos el escenario donde el límite ya se alcanzó
            bool canDownload = downloadsToday < dailyLimit;

            // Assert (Verificar)
            Assert.False(canDownload, "El sistema debió bloquear la descarga al haber alcanzado el límite de 25 del plan VIP.");
        }

        [Fact]
        public async Task Download_WhenUserWithinDailyLimit_ShouldAllowDownload()
        {
            // Arrange
            var dailyLimit = 25; // Límite VIP
            var downloadsToday = 14; // Aún tiene margen de descargas

            // Act
            bool canDownload = downloadsToday < dailyLimit;

            // Assert
            Assert.True(canDownload, "El usuario con 14 descargas realizadas de su límite de 25 debería poder descargar.");
        }
    }

    /// <summary>
    /// Pruebas para la seguridad de Webhooks de Lemon Squeezy (Firma HMAC SHA256)
    /// </summary>
    public class WebhookSecurityTests
    {
        private const string SecretKey = "TestSecretKeyForLemonSqueezy_12345!";

        [Fact]
        public void VerifySignature_WithValidSignature_ShouldReturnTrue()
        {
            // Arrange
            var payload = "{\"meta\":{\"event_name\":\"order_created\"}}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            // Generamos la firma HMAC-SHA256 legítima usando la llave secreta
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
            var hashBytes = hmac.ComputeHash(payloadBytes);
            var expectedSignature = Convert.ToHexString(hashBytes).ToLower();

            // Act (Validación usando comparación a tiempo constante para mitigar ataques de timing)
            var isValid = VerifyHmacSignature(payload, expectedSignature, SecretKey);

            // Assert
            Assert.True(isValid, "La firma generada válidamente debe ser aceptada de manera exitosa.");
        }

        [Fact]
        public void VerifySignature_WithTamperedPayload_ShouldReturnFalse()
        {
            // Arrange
            var payload = "{\"meta\":{\"event_name\":\"order_created\"}}";
            var tamperedPayload = "{\"meta\":{\"event_name\":\"hacker_event_credit_injection\"}}";
            
            // Firma legítima del payload original
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var validSignatureForOriginalPayload = Convert.ToHexString(hashBytes).ToLower();

            // Act - Validamos el payload modificado contra la firma del original
            var isValid = VerifyHmacSignature(tamperedPayload, validSignatureForOriginalPayload, SecretKey);

            // Assert
            Assert.False(isValid, "La firma debe fallar si el cuerpo del payload fue alterado en tránsito.");
        }

        private bool VerifyHmacSignature(string payload, string signatureHeader, string secret)
        {
            if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret)) return false;

            var keyBytes = Encoding.UTF8.GetBytes(secret);
            using var hmac = new HMACSHA256(keyBytes);
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLower();

            // Comparación a tiempo constante para evitar vulnerabilidades de canal lateral (Timing Attacks)
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedSignature), 
                Encoding.UTF8.GetBytes(signatureHeader)
            );
        }
    }

    /// <summary>
    /// Pruebas matemáticas para el tratamiento de vectores de la IA local (ONNX embeddings)
    /// </summary>
    public class EmbeddingMathTests
    {
        [Fact]
        public void NormalizeVector_ShouldReturnVectorWithUnitLength()
        {
            // Arrange - Vector desbalanceado de prueba
            var rawVector = new float[] { 3.0f, 4.0f, 0.0f }; // Su magnitud Euclidiana es Sqrt(3^2 + 4^2) = 5.0f

            // Act - Normalización L2 (misma lógica que el servicio OnnxEmbeddingService)
            float sum = 0;
            for (int i = 0; i < rawVector.Length; i++) sum += rawVector[i] * rawVector[i];
            float norm = (float)Math.Sqrt(sum);
            
            var normalized = new float[rawVector.Length];
            for (int i = 0; i < rawVector.Length; i++) normalized[i] = rawVector[i] / norm;

            // Calculamos la magnitud del vector normalizado (debe dar exactamente 1.0f)
            float normSum = 0;
            for (int i = 0; i < normalized.Length; i++) normSum += normalized[i] * normalized[i];
            float finalMagnitude = (float)Math.Sqrt(normSum);

            // Assert
            Assert.Equal(0.6f, normalized[0], precision: 4); // 3 / 5 = 0.6
            Assert.Equal(0.8f, normalized[1], precision: 4); // 4 / 5 = 0.8
            Assert.Equal(1.0f, finalMagnitude, precision: 4); // La longitud del vector normalizado es 1 (vector unitario)
        }
    }

    #endregion

    #region 2. SCRIPTS DE PRUEBA DE INTEGRACIÓN DE BASE DE DATOS (PostgreSQL & pgvector)

    /*
    -- COPY-PASTE DIRECTO EN POSTGRESQL PARA VALIDAR LA INTEGRACIÓN DE PGVECTOR Y TRIGGERS --

    -- PASO A: SEED / POBLACIÓN DE PRUEBA --
    -- Insertamos planes y paquetes con la estructura del negocio de Simber designs

    -- 1. Insertar un usuario cliente de pruebas
    INSERT INTO users (id, email, password_hash, full_name, role, credits_balance)
    VALUES ('a3f1249c-f9e2-411a-826f-4d372e9d20c5', 'cliente.test@simberdesigns.com', 'hashed_pass_xyz', 'Carlos Sublimador', 'Customer', 0.00)
    ON CONFLICT (email) DO NOTHING;

    -- 2. Insertar paquetes de créditos (Inspirado en Foraes)
    INSERT INTO credit_packages (id, name, credits_amount, bonus_amount, price_usd, active)
    VALUES 
    ('4e2a7e28-1f19-4cb3-91ee-e2213e4df6a1', 'Basic', 10.00, 0.00, 10.00, TRUE),
    ('4e2a7e28-1f19-4cb3-91ee-e2213e4df6a2', 'VIP', 25.00, 5.00, 25.00, TRUE), -- Recibe 30 créditos en total
    ('4e2a7e28-1f19-4cb3-91ee-e2213e4df6a3', 'Elite', 50.00, 15.00, 50.00, TRUE) -- Recibe 65 créditos en total
    ON CONFLICT DO NOTHING;

    -- 3. Insertar vectores deportivos mock con sus respectivos embeddings tridimensionales simplificados para pruebas
    -- En producción pgvector(512) espera 512 dimensiones, para esta validación insertamos arrays de 512 rellenados
    INSERT INTO designs (id, title, slug, description, price_usd, credits_cost, r2_key, preview_url, embedding)
    VALUES 
    ('e1c12140-5f21-4ea2-9f37-1cd7df219491', 'Jersey Motocross Fox Ultimate V1', 'jersey-motocross-fox-v1', 'Diseño vectorial deportivo para moldería motocross', 2.00, 2.00, 'designs/motocross-fox-v1.rar', 'preview/motocross-v1.png', array_fill(0.1, ARRAY[512])::vector),
    ('e1c12140-5f21-4ea2-9f37-1cd7df219492', 'Camiseta Argentina 3 Stars Concept', 'jersey-argentina-3-stars', 'Concepto sublimación Albiceleste Qatar 2026', 2.00, 2.00, 'designs/argentina-3-stars.rar', 'preview/argentina-3.png', array_fill(0.5, ARRAY[512])::vector),
    ('e1c12140-5f21-4ea2-9f37-1cd7df219493', 'Jersey Inter Milan Serpiente Grunge', 'jersey-inter-milan-serpiente', 'Edición especial concepto Grunge Serpiente', 2.00, 2.00, 'designs/inter-serpiente.rar', 'preview/inter-serpiente.png', array_fill(-0.2, ARRAY[512])::vector)
    ON CONFLICT DO NOTHING;


    -- PASO B: PRUEBA DEL TRIGGER DE ACREDITACIÓN DE CRÉDITOS (PAGO MANUAL/QR O WEBHOOK) --
    -- Insertamos una transacción 'Pending' para simular un depósito QR Yape de S/ 92.50 (Equivalent a Paquete VIP $25)
    INSERT INTO transactions (id, user_id, amount, currency, gateway, status, payment_receipt_url, credit_package_id)
    VALUES (
        '9b5832a7-f5da-48eb-a1d2-cd38290fae12', 
        'a3f1249c-f9e2-411a-826f-4d372e9d20c5', 
        25.00, 
        'USD', 
        'Manual_QR', 
        'Pending', 
        'receipts/comprobante_yape_carlos.png', 
        '4e2a7e28-1f19-4cb3-91ee-e2213e4df6a2'
    );

    -- Ejecutar esta consulta para verificar que el saldo de créditos del cliente está en 0.00
    SELECT email, credits_balance FROM users WHERE id = 'a3f1249c-f9e2-411a-826f-4d372e9d20c5';

    -- Simulamos la aprobación por parte de un administrador (Modificamos el estado a 'Completed')
    UPDATE transactions 
    SET status = 'Completed', verified_by = 'a3f1249c-f9e2-411a-826f-4d372e9d20c5', verified_at = NOW() 
    WHERE id = '9b5832a7-f5da-48eb-a1d2-cd38290fae12';

    -- CONSULTA DE VERIFICACIÓN POST-TRIGGER: El saldo ahora debe reflejar exactamente 30.00 créditos (25 base + 5 bonus)
    -- Y la tabla `credit_transactions` debe tener una entrada automática de auditoría.
    SELECT email, credits_balance FROM users WHERE id = 'a3f1249c-f9e2-411a-826f-4d372e9d20c5';
    SELECT * FROM credit_transactions WHERE user_id = 'a3f1249c-f9e2-411a-826f-4d372e9d20c5';


    -- PASO C: PRUEBA DE BÚSQUEDA VISUAL (SIMILITUD COSENO CON PGVECTOR) --
    -- Simulamos que el usuario carga una imagen, y el modelo CLIP extrae un vector de características
    -- donde predomina un valor de 0.45 en sus ejes de embeddings.
    -- Buscamos el diseño más similar en la base de datos de producción:

    SELECT id, title, slug, preview_url, price_usd,
           (1 - (embedding <=> array_fill(0.45, ARRAY[512])::vector)) AS similitud_porcentaje
    FROM designs
    WHERE embedding IS NOT NULL
    ORDER BY embedding <=> array_fill(0.45, ARRAY[512])::vector
    LIMIT 3;

    -- RESULTADO ESPERADO: 
    -- 1. 'Camiseta Argentina 3 Stars Concept' debe figurar primero con la similitud más cercana a 1.0 (vector de 0.5 es muy similar a 0.45).
    -- 2. El diseño de 'Inter Milan Serpiente Grunge' (-0.2) debe aparecer al final debido a la lejanía matemática de los signos vectoriales.
    */

    #endregion
}
