using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // The model has declared this column (with its default) since before this migration file
    // existed, but no prior migration ever physically created it — it was only ever added via
    // an ad-hoc idempotent ALTER at app startup (see git history of Program.cs). This migration
    // folds that into formal migration history; the guard keeps it a no-op on databases where
    // Program.cs's old startup check already created the column.
    public partial class EnsureMaxConcurrentGamesColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                SET @s = IF(
                    NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = DATABASE()
                        AND TABLE_NAME = 'steam_config'
                        AND COLUMN_NAME = 'max_concurrent_games'),
                    'ALTER TABLE steam_config ADD COLUMN max_concurrent_games INT NOT NULL DEFAULT 1',
                    'SELECT 1');
                PREPARE stmt FROM @s; EXECUTE stmt; DEALLOCATE PREPARE stmt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_concurrent_games",
                table: "steam_config");
        }
    }
}
