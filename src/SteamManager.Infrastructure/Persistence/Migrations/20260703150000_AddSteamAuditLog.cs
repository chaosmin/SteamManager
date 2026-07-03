using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "steam_audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Source = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Operation = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    AppId = table.Column<int>(type: "int", nullable: true),
                    RequestSummary = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Success = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ResponseSummary = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_audit_log", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_steam_audit_log_CreatedAt",
                table: "steam_audit_log",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_steam_audit_log_Source_Operation",
                table: "steam_audit_log",
                columns: new[] { "Source", "Operation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "steam_audit_log");
        }
    }
}
