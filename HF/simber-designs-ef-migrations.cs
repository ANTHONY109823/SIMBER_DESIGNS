using System;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SimberDesigns.Infrastructure.Data
{
    /// <summary>
    /// Contexto de Base de Datos para Simber designs.
    /// Incorpora soporte nativo para pgvector (Búsqueda Visual) y mapeos de monetización dual.
    /// </summary>
    public class SimberDesignsDbContext : DbContext
    {
        public SimberDesignsDbContext(DbContextOptions<SimberDesignsDbContext> options)
            : base(options)
        {
        }

        // Tablas principales del sistema
        public DbSet<User> Users { get; set; }
        public DbSet<Subscription> Subscriptions { get; set; }
        public DbSet<CreditPackage> CreditPackages { get; set; }
        public DbSet<Design> Designs { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<UserDownload> UserDownloads { get; set; }
        public DbSet<CreditTransaction> CreditTransactions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. REGISTRAR LA EXTENSIÓN PGVECTOR NATIVAMENTE EN POSTGRESQL
            // Esto le indica a EF Core que agregue "CREATE EXTENSION IF NOT EXISTS vector;" en la migración.
            modelBuilder.HasPostgresExtension("vector");

            // 2. CONFIGURACIÓN DE ENTIDAD: USER
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
                entity.HasIndex(e => e.Email).IsUnique();
                entity.Property(e => e.CreditsBalance).HasColumnType("decimal(10,2)").HasDefaultValue(0.00m);
            });

            // 3. CONFIGURACIÓN DE ENTIDAD: SUBSCRIPTION (Membresías estilo VectorSport)
            modelBuilder.Entity<Subscription>(entity =>
            {
                entity.ToTable("subscriptions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Status).HasMaxLength(50).HasDefaultValue("Active");
                
                // Relación 1-to-Many: Un usuario puede tener múltiples suscripciones (historial), pero solo una activa.
                entity.HasOne(d => d.User)
                    .WithMany(p => p.Subscriptions)
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // 4. CONFIGURACIÓN DE ENTIDAD: CREDIT PACKAGES (Recargas estilo Foraes)
            modelBuilder.Entity<CreditPackage>(entity =>
            {
                entity.ToTable("credit_packages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                entity.Property(e => e.PriceUsd).HasColumnType("decimal(10,2)");
                entity.Property(e => e.CreditsAmount).HasColumnType("decimal(10,2)");
                entity.Property(e => e.BonusAmount).HasColumnType("decimal(10,2)").HasDefaultValue(0.00m);
            });

            // 5. CONFIGURACIÓN DE ENTIDAD: DESIGN (Catálogo de vectores)
            modelBuilder.Entity<Design>(entity =>
            {
                entity.ToTable("designs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(255);
                entity.Property(e => e.Slug).IsRequired().HasMaxLength(255);
                entity.HasIndex(e => e.Slug).IsUnique();
                entity.Property(e => e.PriceUsd).HasColumnType("decimal(10,2)").HasDefaultValue(2.00m);
                entity.Property(e => e.CreditsCost).HasColumnType("decimal(10,2)").HasDefaultValue(1.00m);

                // --- MAPEO CLAVE: pgvector para Búsqueda Visual ---
                // Mapeamos el vector de características de 512 dimensiones (CLIP) como una columna vectorial en Postgres.
                // En EF Core con Npgsql, un float[] anotado con el tipo de columna "vector" es la forma estándar de representarlo.
                entity.Property(e => e.Embedding)
                    .HasColumnType("vector(512)"); 

                // Creamos un índice HNSW para búsquedas de alta velocidad usando Distancia Coseno
                entity.HasIndex(e => e.Embedding)
                    .HasMethod("hnsw")
                    .HasOperators("vector_cosine_ops");
            });

            // 6. CONFIGURACIÓN DE ENTIDAD: TRANSACTIONS (Pagos Híbridos)
            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.ToTable("transactions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Amount).HasColumnType("decimal(10,2)");
                entity.Property(e => e.Status).HasMaxLength(50).HasDefaultValue("Pending");
                entity.Property(e => e.PaymentMethod).HasMaxLength(50);

                entity.HasOne(d => d.User)
                    .WithMany()
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(d => d.CreditPackage)
                    .WithMany()
                    .HasForeignKey(d => d.CreditPackageId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            // 7. CONFIGURACIÓN DE ENTIDAD: USER DOWNLOADS (Auditoría de Descargas de 100 MB)
            modelBuilder.Entity<UserDownload>(entity =>
            {
                entity.ToTable("user_downloads");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DownloadedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(d => d.User)
                    .WithMany()
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(d => d.Design)
                    .WithMany()
                    .HasForeignKey(d => d.DesignId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // 8. CONFIGURACIÓN DE ENTIDAD: CREDIT TRANSACTIONS (Auditoría contable interna)
            modelBuilder.Entity<CreditTransaction>(entity =>
            {
                entity.ToTable("credit_transactions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CreditsChanged).HasColumnType("decimal(10,2)");
                entity.Property(e => e.TxType).HasMaxLength(50);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(d => d.User)
                    .WithMany()
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }

    #region Clases de Modelos (Entidades Dominio)

    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = "Customer"; // Admin, Customer
        public decimal CreditsBalance { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Propiedad de navegación
        public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
    }

    public class Subscription
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public User? User { get; set; }
        public string PlanName { get; set; } = "Basic"; // Basic, VIP, Semestral
        public int DailyDownloadLimit { get; set; } // 15, 25, 30
        public DateTime ExpiresAt { get; set; }
        public string Status { get; set; } = "Active";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CreditPackage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty; // Basic, VIP, Elite
        public decimal PriceUsd { get; set; }
        public decimal CreditsAmount { get; set; }
        public decimal BonusAmount { get; set; }
    }

    public class Design
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string PreviewUrl { get; set; } = string.Empty;
        public string FileKey { get; set; } = string.Empty; // Ruta del archivo en Cloudflare R2
        public decimal PriceUsd { get; set; }
        public decimal CreditsCost { get; set; }
        public float[]? Embedding { get; set; } // Array de 512 floats para pgvector
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Transaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public User? User { get; set; }
        public Guid? CreditPackageId { get; set; }
        public CreditPackage? CreditPackage { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string PaymentMethod { get; set; } = "LemonSqueezy"; // LemonSqueezy, QR_Yape, BankTransfer
        public string Status { get; set; } = "Pending"; // Pending, Completed, Rejected
        public string? ReferenceId { get; set; } // ID de la pasarela de pagos
        public string? PaymentReceiptUrl { get; set; } // Comprobante para pagos QR manuales
        public string? RejectionReason { get; set; }
        public Guid? ApprovedByAdminId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }

    public class UserDownload
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public User? User { get; set; }
        public Guid DesignId { get; set; }
        public Design? Design { get; set; }
        public DateTime DownloadedAt { get; set; }
        public string IpAddress { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
    }

    public class CreditTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public User? User { get; set; }
        public Guid? TransactionId { get; set; }
        public decimal CreditsChanged { get; set; } // Puede ser positivo (recompensa/recarga) o negativo (canje)
        public string TxType { get; set; } = "Recharge"; // Recharge, Purchase, Bonus, Penalty
        public DateTime CreatedAt { get; set; }
    }

    #endregion
}
