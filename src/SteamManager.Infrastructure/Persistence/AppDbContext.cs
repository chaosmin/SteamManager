using Microsoft.EntityFrameworkCore;
using SteamManager.Core.Models;

namespace SteamManager.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SteamConfig> SteamConfigs => Set<SteamConfig>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<SteamAuditLog> SteamAuditLogs => Set<SteamAuditLog>();
    public DbSet<GameQueueEntry> GameQueue => Set<GameQueueEntry>();
    public DbSet<GameReferencePlayer> GameReferencePlayers => Set<GameReferencePlayer>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SteamConfig>(e =>
        {
            e.ToTable("steam_config");
            e.Property(x => x.DisplayTimezone).HasDefaultValue("UTC");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<Game>(e =>
        {
            e.ToTable("game");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<Achievement>(e =>
        {
            e.ToTable("achievement");
            e.HasIndex(x => new { x.GameId, x.ApiName }).IsUnique();
            e.HasIndex(x => new { x.IsUnlocked, x.ScheduledUnlockAt });  // for global scanner
            e.HasOne(x => x.Game).WithMany(g => g.Achievements)
             .HasForeignKey(x => x.GameId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<SteamAuditLog>(e =>
        {
            e.ToTable("steam_audit_log");
            e.Property(x => x.Source).HasMaxLength(50);
            e.Property(x => x.Operation).HasMaxLength(100);
            e.Property(x => x.RequestSummary).HasMaxLength(500);
            e.Property(x => x.ResponseSummary).HasMaxLength(1000);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Source, x.Operation });
        });

        mb.Entity<GameQueueEntry>(e =>
        {
            e.ToTable("game_queue");
            e.HasOne(x => x.Game).WithMany()
             .HasForeignKey(x => x.GameId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.GameId).IsUnique();
            e.HasIndex(x => x.Position);
        });

        mb.Entity<GameReferencePlayer>(e =>
        {
            e.ToTable("game_reference_player");
            e.HasOne(x => x.Game).WithOne(g => g.ReferencePlayer)
             .HasForeignKey<GameReferencePlayer>(x => x.GameId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.GameId).IsUnique();
            e.Property(x => x.PlayerUrl).HasMaxLength(512);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        foreach (var entity in mb.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }
}
