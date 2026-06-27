using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddI18nFieldsDropEnableAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAchievements",
                table: "game");

            migrationBuilder.AddColumn<string>(
                name: "NameI18n",
                table: "game",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "DisplayNameI18n",
                table: "achievement",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NameI18n",
                table: "game");

            migrationBuilder.DropColumn(
                name: "DisplayNameI18n",
                table: "achievement");

            migrationBuilder.AddColumn<bool>(
                name: "EnableAchievements",
                table: "game",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }
    }
}
