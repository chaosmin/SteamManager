using Microsoft.EntityFrameworkCore;
using SteamManager.Core.Models;

namespace SteamManager.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SteamConfig> SteamConfigs => Set<SteamConfig>();
    public DbSet<GameConfig> GameConfigs => Set<GameConfig>();
    public DbSet<GameProgress> GameProgresses => Set<GameProgress>();
    public DbSet<AchievementScheduleItem> AchievementSchedules => Set<AchievementScheduleItem>();
    public DbSet<AchievementCache> AchievementCaches => Set<AchievementCache>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SteamConfig>(e =>
        {
            e.ToTable("steam_config");
            e.Property(x => x.DisplayTimezone).HasDefaultValue("UTC");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<GameConfig>(e =>
        {
            e.ToTable("game_config");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<GameProgress>(e =>
        {
            e.ToTable("game_progress");
            e.HasIndex(x => x.AppId).IsUnique();
            e.HasOne(x => x.Game).WithOne(g => g.Progress)
             .HasForeignKey<GameProgress>(x => x.AppId)
             .HasPrincipalKey<GameConfig>(g => g.AppId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<AchievementScheduleItem>(e =>
        {
            e.ToTable("achievement_schedule");
            e.HasIndex(x => new { x.AppId, x.AchievementId }).IsUnique();
            e.HasIndex(x => new { x.AppId, x.Done, x.OffsetMinutes });
            e.HasOne(x => x.Game).WithMany(g => g.AchievementSchedule)
             .HasForeignKey(x => x.AppId)
             .HasPrincipalKey(g => g.AppId);
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });

        mb.Entity<AchievementCache>(e =>
        {
            e.ToTable("achievement_cache");
            e.HasIndex(x => x.AppId).IsUnique();
            e.Property(x => x.Data).HasColumnType("json");
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        });
    }
}
