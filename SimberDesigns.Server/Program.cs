using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.Configure<OnnxOptions>(builder.Configuration.GetSection(OnnxOptions.SectionName));

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

builder.Services.AddSingleton<ICloudflareR2Service, CloudflareR2Service>();
builder.Services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
builder.Services.AddSingleton<ILemonSqueezySignatureVerifier, LemonSqueezySignatureVerifier>();
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
builder.Services.AddControllers();
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

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
