using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class v040_achievement_scheduling_redesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_queue");

            migrationBuilder.DropIndex(
                name: "IX_achievement_game_id_is_unlocked_unlock_offset_minutes",
                table: "achievement");

            migrationBuilder.DropColumn(
                name: "reference_url",
                table: "game");

            migrationBuilder.DropColumn(
                name: "saved_idle_delta_minutes",
                table: "game");

            migrationBuilder.DropColumn(
                name: "unlock_offset_minutes",
                table: "achievement");

            migrationBuilder.RenameColumn(
                name: "total_play_minutes",
                table: "game",
                newName: "steam_playtime_at_refresh");

            migrationBuilder.RenameColumn(
                name: "reference_play_minutes",
                table: "game",
                newName: "target_minutes");

            migrationBuilder.RenameColumn(
                name: "last_session_start",
                table: "game",
                newName: "session_started_at");

            migrationBuilder.AddColumn<DateTime>(
                name: "scheduled_unlock_at",
                table: "achievement",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "game_queue",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    game_id = table.Column<int>(type: "int", nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    added_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_queue", x => x.id);
                    table.ForeignKey(
                        name: "FK_game_queue_game_game_id",
                        column: x => x.game_id,
                        principalTable: "game",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "game_reference_player",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    game_id = table.Column<int>(type: "int", nullable: false),
                    player_url = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    override_burst_check = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ComputedColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_reference_player", x => x.id);
                    table.ForeignKey(
                        name: "FK_game_reference_player_game_game_id",
                        column: x => x.game_id,
                        principalTable: "game",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_achievement_is_unlocked_scheduled_unlock_at",
                table: "achievement",
                columns: new[] { "is_unlocked", "scheduled_unlock_at" });

            migrationBuilder.CreateIndex(
                name: "IX_game_queue_game_id",
                table: "game_queue",
                column: "game_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_queue_position",
                table: "game_queue",
                column: "position");

            migrationBuilder.CreateIndex(
                name: "IX_game_reference_player_game_id",
                table: "game_reference_player",
                column: "game_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_queue");

            migrationBuilder.DropTable(
                name: "game_reference_player");

            migrationBuilder.DropIndex(
                name: "IX_achievement_is_unlocked_scheduled_unlock_at",
                table: "achievement");

            migrationBuilder.DropColumn(
                name: "scheduled_unlock_at",
                table: "achievement");

            migrationBuilder.RenameColumn(
                name: "target_minutes",
                table: "game",
                newName: "reference_play_minutes");

            migrationBuilder.RenameColumn(
                name: "steam_playtime_at_refresh",
                table: "game",
                newName: "total_play_minutes");

            migrationBuilder.RenameColumn(
                name: "session_started_at",
                table: "game",
                newName: "last_session_start");

            migrationBuilder.AddColumn<string>(
                name: "reference_url",
                table: "game",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "saved_idle_delta_minutes",
                table: "game",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "unlock_offset_minutes",
                table: "achievement",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "play_queue",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    game_id = table.Column<int>(type: "int", nullable: false),
                    added_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    saved_session_minutes = table.Column<int>(type: "int", nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_queue", x => x.id);
                    table.ForeignKey(
                        name: "FK_play_queue_game_game_id",
                        column: x => x.game_id,
                        principalTable: "game",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_achievement_game_id_is_unlocked_unlock_offset_minutes",
                table: "achievement",
                columns: new[] { "game_id", "is_unlocked", "unlock_offset_minutes" });

            migrationBuilder.CreateIndex(
                name: "IX_play_queue_game_id",
                table: "play_queue",
                column: "game_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_play_queue_sort_order",
                table: "play_queue",
                column: "sort_order");
        }
    }
}
