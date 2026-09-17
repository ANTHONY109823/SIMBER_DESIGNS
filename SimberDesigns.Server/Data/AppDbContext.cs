using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Models;

namespace SimberDesigns.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CreditPackage> CreditPackages => Set<CreditPackage>();
    public DbSet<Design> Designs => Set<Design>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<CreditTransaction> CreditTransactions => Set<CreditTransaction>();
    public DbSet<UserDownload> UserDownloads => Set<UserDownload>();
    public DbSet<PluginLicense> PluginLicenses => Set<PluginLicense>();
    public DbSet<SiteContent> SiteContents => Set<SiteContent>();
    public DbSet<SiteAsset> SiteAssets => Set<SiteAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).IsRequired().HasMaxLength(255);
            entity.Property(x => x.FullName).HasMaxLength(150);
            entity.Property(x => x.Role).HasMaxLength(50);
            entity.Property(x => x.CreditsBalance).HasPrecision(10, 2);
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.Property(x => x.Tier).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(50);
            entity.HasOne(x => x.User).WithMany(x => x.Subscriptions).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<CreditPackage>(entity =>
        {
            entity.Property(x => x.Name).IsRequired().HasMaxLength(50);
            entity.Property(x => x.CreditsAmount).HasPrecision(10, 2);
            entity.Property(x => x.BonusAmount).HasPrecision(10, 2);
            entity.Property(x => x.PriceUsd).HasPrecision(10, 2);
        });

        modelBuilder.Entity<Design>(entity =>
        {
            entity.Property(x => x.Title).IsRequired().HasMaxLength(255);
            entity.Property(x => x.Slug).IsRequired().HasMaxLength(255);
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.Category).HasMaxLength(100);
            entity.Property(x => x.PriceUsd).HasPrecision(10, 2);
            entity.Property(x => x.CreditsCost).HasPrecision(10, 2);
            entity.Property(x => x.R2Key).HasMaxLength(512).HasColumnName("r2_key");
            entity.Property(x => x.PreviewR2Key).HasMaxLength(512).HasColumnName("preview_r2_key");
            entity.Property(x => x.PreviewUrl).HasMaxLength(512);
            entity.Property(x => x.Embedding).HasColumnType("vector(512)");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(x => x.Amount).HasPrecision(10, 2);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Gateway).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(50);
            entity.HasOne(x => x.User).WithMany(x => x.Transactions).HasForeignKey(x => x.UserId);
            entity.HasOne(x => x.CreditPackage).WithMany().HasForeignKey(x => x.CreditPackageId);
            entity.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId);
            entity.HasOne(x => x.Design).WithMany().HasForeignKey(x => x.DesignId);
        });

        modelBuilder.Entity<CreditTransaction>(entity =>
        {
            entity.Property(x => x.CreditsChanged).HasPrecision(10, 2);
            entity.Property(x => x.TxType).HasMaxLength(50);
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne(x => x.Design).WithMany().HasForeignKey(x => x.DesignId);
        });

        modelBuilder.Entity<UserDownload>(entity =>
        {
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne(x => x.Design).WithMany().HasForeignKey(x => x.DesignId);
        });

        modelBuilder.Entity<PluginLicense>(entity =>
        {
            entity.ToTable("plugin_licenses");
            entity.Property(x => x.HardwareId).HasMaxLength(200);
            entity.Property(x => x.Plan).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(50);
            entity.Property(x => x.Edition).HasMaxLength(40);
            entity.Property(x => x.ActivationCode).HasMaxLength(40);
            entity.HasIndex(x => x.ActivationCode).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.Edition });
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<SiteContent>(entity =>
        {
            entity.ToTable("site_content");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(120);
        });

        modelBuilder.Entity<SiteAsset>(entity =>
        {
            entity.ToTable("site_assets");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(120);
            entity.Property(x => x.ContentType).HasMaxLength(100);
            entity.Property(x => x.R2Key).HasMaxLength(512).HasColumnName("r2_key");
        });
    }
}
