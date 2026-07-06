using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameReferenceUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reference_url",
                table: "game",
                type: "longtext",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reference_url",
                table: "game");
        }
    }
}
