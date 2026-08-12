using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUiSessionToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ui_session_issued_at",
                table: "steam_config",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ui_session_token_hash",
                table: "steam_config",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ui_session_issued_at",
                table: "steam_config");

            migrationBuilder.DropColumn(
                name: "ui_session_token_hash",
                table: "steam_config");
        }
    }
}
