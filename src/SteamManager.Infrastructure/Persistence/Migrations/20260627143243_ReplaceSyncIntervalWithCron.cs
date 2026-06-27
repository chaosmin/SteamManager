using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSyncIntervalWithCron : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncIntervalMinutes",
                table: "steam_config");

            migrationBuilder.AddColumn<string>(
                name: "SyncCron",
                table: "steam_config",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncCron",
                table: "steam_config");

            migrationBuilder.AddColumn<int>(
                name: "SyncIntervalMinutes",
                table: "steam_config",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
