using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        try
        {
            await ApplySchemaAsync(db, app.Environment, logger);
            var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await dataSource.ReloadTypesAsync();
            await SeedAsync(db, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "No se pudo inicializar PostgreSQL. Levanta el contenedor con `docker compose up -d` y vuelve a ejecutar la API.");
        }
    }

    private static async Task ApplySchemaAsync(AppDbContext db, IWebHostEnvironment env, ILogger logger)
    {
        var sqlPath = FindSchemaPath(env);
        if (sqlPath is null)
        {
            logger.LogWarning("No se encontró simber-designs-db-schema.sql.");
            return;
        }

        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        var hasPackages = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'credit_packages'
            )
            """);
        var hasLegacyPlan = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'plan'
            )
            """);

        if (hasPackages && !hasLegacyPlan)
        {
            logger.LogInformation("Esquema de créditos/membresías ya aplicado.");
            await RelaxEnumColumnsAsync(connection, logger);
            await EnsureCommerceSchemaAsync(connection, logger);
            return;
        }

        var sql = await File.ReadAllTextAsync(sqlPath);
        await connection.ExecuteAsync(sql);
        await connection.ReloadTypesAsync();
        await RelaxEnumColumnsAsync(connection, logger);
        await EnsureCommerceSchemaAsync(connection, logger);
        logger.LogInformation("Esquema canónico aplicado desde {Path}.", sqlPath);
    }

    private static async Task RelaxEnumColumnsAsync(NpgsqlConnection connection, ILogger logger)
    {
        await connection.ExecuteAsync(
            """
            DROP INDEX IF EXISTS idx_transactions_status_pending;
            ALTER TABLE users ALTER COLUMN role TYPE varchar(50) USING role::text;
            ALTER TABLE subscriptions ALTER COLUMN tier TYPE varchar(50) USING tier::text;
            ALTER TABLE subscriptions ALTER COLUMN status TYPE varchar(50) USING status::text;
            ALTER TABLE transactions ALTER COLUMN gateway TYPE varchar(50) USING gateway::text;
            ALTER TABLE transactions ALTER COLUMN status TYPE varchar(50) USING status::text;
            ALTER TABLE credit_transactions ALTER COLUMN tx_type TYPE varchar(50) USING tx_type::text;
            CREATE INDEX IF NOT EXISTS idx_transactions_status_pending ON transactions(status) WHERE status = 'Pending';
            """);
        logger.LogInformation("Columnas enum relajadas a varchar para desarrollo local.");
    }

    private static async Task EnsureCommerceSchemaAsync(NpgsqlConnection connection, ILogger logger)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS plugin_licenses (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
                hardware_id VARCHAR(200),
                plan VARCHAR(50) NOT NULL,
                status VARCHAR(50) NOT NULL,
                activation_code VARCHAR(40) NOT NULL UNIQUE,
                expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
                created_at TIMESTAMP WITH TIME ZONE NOT NULL,
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_plugin_licenses_user ON plugin_licenses(user_id);
            ALTER TABLE plugin_licenses ADD COLUMN IF NOT EXISTS edition VARCHAR(40) NOT NULL DEFAULT '';
            ALTER TABLE plugin_licenses ADD COLUMN IF NOT EXISTS activation_code VARCHAR(40) NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS idx_plugin_licenses_user_edition ON plugin_licenses(user_id, edition);

            CREATE TABLE IF NOT EXISTS site_content (
                key VARCHAR(120) PRIMARY KEY,
                value TEXT NOT NULL DEFAULT '',
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS site_assets (
                key VARCHAR(120) PRIMARY KEY,
                content_type VARCHAR(100) NOT NULL DEFAULT 'image/jpeg',
                data BYTEA NOT NULL,
                updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);
        logger.LogInformation("Tablas plugin_licenses + CMS (site_content/site_assets) listas.");
    }

    private static string? FindSchemaPath(IWebHostEnvironment env)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "simber-designs-db-schema.sql"),
            Path.Combine(env.ContentRootPath, "simber-designs-db-schema.sql"),
            Path.Combine(env.ContentRootPath, "..", "simber-designs-db-schema.sql")
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static async Task SeedAsync(AppDbContext db, ILogger logger)
    {
        var hasher = new PasswordHasher<User>();

        if (!await db.Users.AnyAsync(u => u.Email == "admin@simber.designs"))
        {
            var admin = new User
            {
                Id = Guid.NewGuid(),
                Email = "admin@simber.designs",
                FullName = "Administrador Simber",
                Role = Roles.Admin,
                CreditsBalance = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            admin.PasswordHash = hasher.HashPassword(admin, "Admin123!");
            db.Users.Add(admin);
            logger.LogInformation("Usuario administrador sembrado (credenciales solo por configuración / entrega privada).");
        }

        var demo = await db.Users.FirstOrDefaultAsync(u => u.Email == "demo@simber.designs");
        if (demo is null)
        {
            demo = new User
            {
                Id = Guid.NewGuid(),
                Email = "demo@simber.designs",
                FullName = "Cliente Demo",
                Role = Roles.Customer,
                CreditsBalance = 30,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            demo.PasswordHash = hasher.HashPassword(demo, "Demo123!");
            db.Users.Add(demo);
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = demo.Id,
                Tier = MembershipTiers.Vip,
                Status = SubscriptionStatuses.Active,
                DailyDownloadLimit = MembershipLimits.ForTier(MembershipTiers.Vip),
                StartsAt = DateTime.UtcNow.AddDays(-10),
                ExpiresAt = DateTime.UtcNow.AddMonths(1),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            logger.LogInformation("Usuario demo cliente sembrado (credenciales solo por entrega privada).");
        }

        if (!await db.Designs.AnyAsync())
        {
            db.Designs.AddRange(
                CreateDesign("Jersey Argentina World Cup 2026 Special Edition", "jersey-argentina-3", "Jersey", "Concepto Albiceleste para sublimación."),
                CreateDesign("Inter Milan Concept Snake Black & Gold", "inter-milan-snake", "Concept Kits", "Serpiente grunge sobre negro y oro."),
                CreateDesign("Motocross Fox Racing Team Concept", "motocross-fox-3", "Motocross", "Moldería motocross lista para corte."),
                CreateDesign("Basketball Wolves Dark Grunge Pattern", "basketball-wolves-grunge", "Basketball", "Patrón grunge para paneles laterales."),
                CreateDesign("Voleibol Power Strike Neon Orange", "voleibol-power-strike", "Voleibol", "Acentos naranja neón para dorsal y costados."),
                CreateDesign("Panama Home Kit World Cup 26 Aero", "panama-homekit-2026", "Jersey", "Kit local aero con degradado rojo.")
            );
        }

        if (!await db.CreditPackages.AnyAsync(p => p.Name == "160"))
        {
            foreach (var old in await db.CreditPackages.Where(p => p.Name == "Basic" || p.Name == "VIP" || p.Name == "Elite").ToListAsync())
            {
                old.Active = false;
            }

            db.CreditPackages.AddRange(
                new CreditPackage { Id = Guid.NewGuid(), Name = "160", CreditsAmount = 160, BonusAmount = 0, PriceUsd = 20, Active = true },
                new CreditPackage { Id = Guid.NewGuid(), Name = "400", CreditsAmount = 400, BonusAmount = 0, PriceUsd = 30, Active = true },
                new CreditPackage { Id = Guid.NewGuid(), Name = "800", CreditsAmount = 800, BonusAmount = 50, PriceUsd = 50, Active = true });
            logger.LogInformation("Paquetes de créditos en soles listos (160 / 400 / 800).");
        }

        await SeedDefaultContentAsync(db);
        await db.SaveChangesAsync();
    }

    private static async Task SeedDefaultContentAsync(AppDbContext db)
    {
        var defaults = new Dictionary<string, string>
        {
            ["home.hero.eyebrow"] = "Vectores · Sublimación · Wireframe",
            ["home.hero.title"] = "Del patrón a la camiseta en menos de tres clics.",
            ["home.hero.desc"] = "Catálogo CDR y programas para CorelDRAW e Illustrator.",
            ["home.steps.title"] = "De la lista al corte, en cuatro pasos",
            ["catalog.eyebrow"] = "Catálogo CDR",
            ["catalog.title"] = "Fútbol|Vóley",
            ["catalog.desc"] = "",
            ["programas.badge"] = "Herramienta · CorelDRAW e Illustrator",
            ["programas.desc"] = "Arma mockups, nombres y dorsales en CorelDRAW e Illustrator.",
            ["encarganos.eyebrow"] = "Cotización",
            ["encarganos.title"] = "Encárganos a nosotros",
            ["encarganos.gallery.title"] = "Así queda en cancha"
        };

        var existing = await db.SiteContents.Select(c => c.Key).ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var (key, value) in defaults)
        {
            if (existing.Contains(key)) continue;
            db.SiteContents.Add(new SiteContent { Key = key, Value = value, UpdatedAt = now });
        }
    }

    private static Design CreateDesign(string title, string slug, string category, string description)
    {
        return new Design
        {
            Id = Guid.NewGuid(),
            Title = title,
            Slug = slug,
            Description = description,
            Category = category,
            PriceUsd = 2m,
            CreditsCost = 2m,
            R2Key = $"designs/{slug}.rar",
            PreviewUrl = $"https://images.unsplash.com/photo-1579546929518-9e396f3cc809?w=800&auto=format&fit=crop&q=60&sig={slug}",
            IsFreeDaily = true,
            Embedding = new Vector(RandomUnitVector(512, slug.GetHashCode())),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static float[] RandomUnitVector(int dimensions, int? seed = null)
    {
        var random = seed is null ? Random.Shared : new Random(seed.Value);
        var values = Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
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
}
