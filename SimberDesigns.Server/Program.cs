using Microsoft.AspNetCore.HttpOverrides;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Options;
using SimberDesigns.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<CloudflareR2Options>(builder.Configuration.GetSection(CloudflareR2Options.SectionName));
builder.Services.Configure<LemonSqueezyOptions>(builder.Configuration.GetSection(LemonSqueezyOptions.SectionName));
builder.Services.Configure<MercadoPagoOptions>(builder.Configuration.GetSection(MercadoPagoOptions.SectionName));
builder.Services.Configure<OnnxOptions>(builder.Configuration.GetSection(OnnxOptions.SectionName));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.SectionName));
builder.Services.AddHttpClient("anthropic");

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/octet-stream",
        "application/wasm",
        "application/json",
        "image/svg+xml"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:DefaultConnection.");

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
dataSourceBuilder.EnableUnmappedTypes();
var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource, npgsql => npgsql.UseVector())
        .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<LocalCatalogStorage>();
builder.Services.AddSingleton<PluginInstallerStorage>();
builder.Services.AddSingleton<ICloudflareR2Service, CloudflareR2Service>();
builder.Services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
builder.Services.AddSingleton<ILemonSqueezySignatureVerifier, LemonSqueezySignatureVerifier>();
builder.Services.AddHttpClient<IMercadoPagoService, MercadoPagoService>();
builder.Services.AddScoped<IPaymentFulfillmentService, PaymentFulfillmentService>();
builder.Services.AddScoped<IPlanPricingService, PlanPricingService>();
builder.Services.AddScoped<IPluginPeriodKeyService, PluginPeriodKeyService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IDownloadLimitService, DownloadLimitService>();
builder.Services.AddSingleton<PasswordHasher<User>>();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Falta la sección Jwt.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.Name
        };
    });

builder.Services.AddAuthorization();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 110_000_000;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 110_000_000;
    options.AddServerHeader = false; // no revelar Kestrel/versión
});

// —— Rate limiting (anti fuerza bruta y floods) ——
static string ClientIp(HttpContext ctx)
{
    var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(fwd))
    {
        return fwd.Split(',')[0].Trim();
    }
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global SOLO para /api por IP: frena floods a la API sin tocar los archivos de Blazor
    // (index/wasm/dll/css/js quedan sin límite para que la web cargue normal).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("static");
        }
        return RateLimitPartition.GetFixedWindowLimiter("api:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    // Login/registro: muy estricto por IP (evita miles de intentos de contraseña).
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter("auth:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Pagos y canje de serial: por IP.
    options.AddPolicy("sensitive", ctx =>
        RateLimitPartition.GetFixedWindowLimiter("sensitive:" + ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync(
            "Demasiados intentos. Espera un momento e inténtalo de nuevo.", token);
    };
});
builder.Services.AddControllers();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app);

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseCors("dev");
}

app.UseForwardedHeaders();

// Cabeceras de seguridad en TODAS las respuestas (anti-clickjacking, anti-sniff, sin fugas de referer).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Content-Security-Policy"] = "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
    await next();
});

app.UseRateLimiter();
app.UseResponseCompression();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Redirige a https solo cuando el proxy no indica ya https (evita loops raros).
app.Use(async (ctx, next) =>
{
    var forwarded = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
    var isHttps = string.Equals(ctx.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
        || string.Equals(forwarded, "https", StringComparison.OrdinalIgnoreCase);
    if (!app.Environment.IsDevelopment()
        && !isHttps
        && !ctx.Request.Host.Host.Contains("localhost", StringComparison.OrdinalIgnoreCase))
    {
        var url = $"https://{ctx.Request.Host}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}";
        ctx.Response.Redirect(url, permanent: true);
        return;
    }

    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";

        // index / boot: sin cache pegada (si no, el cliente no ve deploys)
        if (path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("blazor.boot.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("blazor.webassembly.js", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
            return;
        }

        // DLL/WASM hasheados: cache largo
        if (path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".br", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=31536000,immutable";
            return;
        }

        // CSS/JS/imagenes sin hash: revalidar siempre
        if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".styles.css", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache, must-revalidate";
            return;
        }

        if (path.StartsWith("/images/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/programas/", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=86400,must-revalidate";
        }
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers[HeaderNames.Pragma] = "no-cache";
    }
});
app.Run();
