using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SteamManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseColumnNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── game ──────────────────────────────────────────────────────────
            // Step 1: rename to snake_case
            migrationBuilder.Sql(@"ALTER TABLE `game`
                CHANGE COLUMN `Id` `id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `AppId` `app_id` int NOT NULL,
                CHANGE COLUMN `Name` `name` longtext NOT NULL,
                CHANGE COLUMN `NameI18n` `name_i18n` longtext NULL,
                CHANGE COLUMN `Status` `status` longtext NOT NULL,
                CHANGE COLUMN `TotalPlayMinutes` `total_play_minutes` int NOT NULL,
                CHANGE COLUMN `LastSessionStart` `last_session_start` datetime(6) NULL,
                CHANGE COLUMN `ReferencePlayMinutes` `reference_play_minutes` int NULL,
                CHANGE COLUMN `AchievementsCachedAt` `achievements_cached_at` datetime(6) NULL,
                CHANGE COLUMN `DropsRemaining` `drops_remaining` int NULL,
                CHANGE COLUMN `SavedIdleDeltaMinutes` `saved_idle_delta_minutes` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `CreatedAt` `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `UpdatedAt` `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            // Step 2: reorder so created_at/updated_at are last
            migrationBuilder.Sql(@"ALTER TABLE `game`
                MODIFY COLUMN `id` int NOT NULL AUTO_INCREMENT FIRST,
                MODIFY COLUMN `app_id` int NOT NULL AFTER `id`,
                MODIFY COLUMN `name` longtext NOT NULL AFTER `app_id`,
                MODIFY COLUMN `name_i18n` longtext NULL AFTER `name`,
                MODIFY COLUMN `status` longtext NOT NULL AFTER `name_i18n`,
                MODIFY COLUMN `total_play_minutes` int NOT NULL AFTER `status`,
                MODIFY COLUMN `last_session_start` datetime(6) NULL AFTER `total_play_minutes`,
                MODIFY COLUMN `reference_play_minutes` int NULL AFTER `last_session_start`,
                MODIFY COLUMN `achievements_cached_at` datetime(6) NULL AFTER `reference_play_minutes`,
                MODIFY COLUMN `drops_remaining` int NULL AFTER `achievements_cached_at`,
                MODIFY COLUMN `saved_idle_delta_minutes` int NOT NULL DEFAULT 0 AFTER `drops_remaining`,
                MODIFY COLUMN `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) AFTER `saved_idle_delta_minutes`,
                MODIFY COLUMN `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) AFTER `created_at`");

            // ── achievement ───────────────────────────────────────────────────
            migrationBuilder.Sql(@"ALTER TABLE `achievement`
                CHANGE COLUMN `Id` `id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `GameId` `game_id` int NOT NULL,
                CHANGE COLUMN `AppId` `app_id` int NOT NULL,
                CHANGE COLUMN `ApiName` `api_name` varchar(255) NOT NULL,
                CHANGE COLUMN `DisplayName` `display_name` longtext NOT NULL,
                CHANGE COLUMN `DisplayNameI18n` `display_name_i18n` longtext NULL,
                CHANGE COLUMN `Description` `description` longtext NULL,
                CHANGE COLUMN `DescriptionI18n` `description_i18n` longtext NULL,
                CHANGE COLUMN `IconUrl` `icon_url` longtext NULL,
                CHANGE COLUMN `IconGrayUrl` `icon_gray_url` longtext NULL,
                CHANGE COLUMN `GlobalPercent` `global_percent` double NOT NULL,
                CHANGE COLUMN `UnlockOffsetMinutes` `unlock_offset_minutes` int NOT NULL,
                CHANGE COLUMN `IsUnlocked` `is_unlocked` tinyint(1) NOT NULL,
                CHANGE COLUMN `UnlockedAt` `unlocked_at` datetime(6) NULL,
                CHANGE COLUMN `CreatedAt` `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `UpdatedAt` `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql(@"ALTER TABLE `achievement`
                MODIFY COLUMN `id` int NOT NULL AUTO_INCREMENT FIRST,
                MODIFY COLUMN `game_id` int NOT NULL AFTER `id`,
                MODIFY COLUMN `app_id` int NOT NULL AFTER `game_id`,
                MODIFY COLUMN `api_name` varchar(255) NOT NULL AFTER `app_id`,
                MODIFY COLUMN `display_name` longtext NOT NULL AFTER `api_name`,
                MODIFY COLUMN `display_name_i18n` longtext NULL AFTER `display_name`,
                MODIFY COLUMN `description` longtext NULL AFTER `display_name_i18n`,
                MODIFY COLUMN `description_i18n` longtext NULL AFTER `description`,
                MODIFY COLUMN `icon_url` longtext NULL AFTER `description_i18n`,
                MODIFY COLUMN `icon_gray_url` longtext NULL AFTER `icon_url`,
                MODIFY COLUMN `global_percent` double NOT NULL AFTER `icon_gray_url`,
                MODIFY COLUMN `unlock_offset_minutes` int NOT NULL AFTER `global_percent`,
                MODIFY COLUMN `is_unlocked` tinyint(1) NOT NULL AFTER `unlock_offset_minutes`,
                MODIFY COLUMN `unlocked_at` datetime(6) NULL AFTER `is_unlocked`,
                MODIFY COLUMN `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) AFTER `unlocked_at`,
                MODIFY COLUMN `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) AFTER `created_at`");

            // ── steam_config ──────────────────────────────────────────────────
            migrationBuilder.Sql(@"ALTER TABLE `steam_config`
                CHANGE COLUMN `Id` `id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `Username` `username` longtext NOT NULL,
                CHANGE COLUMN `PasswordEnc` `password_enc` longtext NULL,
                CHANGE COLUMN `SessionToken` `session_token` longtext NULL,
                CHANGE COLUMN `SessionUpdatedAt` `session_updated_at` datetime(6) NULL,
                CHANGE COLUMN `WebApiKey` `web_api_key` longtext NULL,
                CHANGE COLUMN `SyncCron` `sync_cron` longtext NOT NULL,
                CHANGE COLUMN `Language` `language` longtext NOT NULL,
                CHANGE COLUMN `DisplayTimezone` `display_timezone` longtext NOT NULL DEFAULT ('UTC'),
                CHANGE COLUMN `CreatedAt` `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `UpdatedAt` `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql(@"ALTER TABLE `steam_config`
                MODIFY COLUMN `id` int NOT NULL AUTO_INCREMENT FIRST,
                MODIFY COLUMN `username` longtext NOT NULL AFTER `id`,
                MODIFY COLUMN `password_enc` longtext NULL AFTER `username`,
                MODIFY COLUMN `session_token` longtext NULL AFTER `password_enc`,
                MODIFY COLUMN `session_updated_at` datetime(6) NULL AFTER `session_token`,
                MODIFY COLUMN `web_api_key` longtext NULL AFTER `session_updated_at`,
                MODIFY COLUMN `sync_cron` longtext NOT NULL AFTER `web_api_key`,
                MODIFY COLUMN `language` longtext NOT NULL AFTER `sync_cron`,
                MODIFY COLUMN `display_timezone` longtext NOT NULL DEFAULT ('UTC') AFTER `language`,
                MODIFY COLUMN `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) AFTER `display_timezone`,
                MODIFY COLUMN `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6) AFTER `created_at`");

            // ── play_queue ────────────────────────────────────────────────────
            // Already in correct order (id, game_id, sort_order, saved_session_minutes, is_active, added_at)
            migrationBuilder.Sql(@"ALTER TABLE `play_queue`
                CHANGE COLUMN `Id` `id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `GameId` `game_id` int NOT NULL,
                CHANGE COLUMN `SortOrder` `sort_order` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `SavedSessionMinutes` `saved_session_minutes` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `IsActive` `is_active` tinyint(1) NOT NULL DEFAULT 0,
                CHANGE COLUMN `AddedAt` `added_at` datetime(6) NOT NULL");

            // ── steam_audit_log ───────────────────────────────────────────────
            // Already in correct order (id, source, operation, app_id, ..., created_at)
            migrationBuilder.Sql(@"ALTER TABLE `steam_audit_log`
                CHANGE COLUMN `Id` `id` bigint NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `Source` `source` varchar(50) NOT NULL,
                CHANGE COLUMN `Operation` `operation` varchar(100) NOT NULL,
                CHANGE COLUMN `AppId` `app_id` int NULL,
                CHANGE COLUMN `RequestSummary` `request_summary` varchar(500) NULL,
                CHANGE COLUMN `Success` `success` tinyint(1) NOT NULL,
                CHANGE COLUMN `ResponseSummary` `response_summary` varchar(1000) NULL,
                CHANGE COLUMN `DurationMs` `duration_ms` int NOT NULL,
                CHANGE COLUMN `CreatedAt` `created_at` datetime(6) NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE `game`
                CHANGE COLUMN `id` `Id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `app_id` `AppId` int NOT NULL,
                CHANGE COLUMN `name` `Name` longtext NOT NULL,
                CHANGE COLUMN `name_i18n` `NameI18n` longtext NULL,
                CHANGE COLUMN `status` `Status` longtext NOT NULL,
                CHANGE COLUMN `total_play_minutes` `TotalPlayMinutes` int NOT NULL,
                CHANGE COLUMN `last_session_start` `LastSessionStart` datetime(6) NULL,
                CHANGE COLUMN `reference_play_minutes` `ReferencePlayMinutes` int NULL,
                CHANGE COLUMN `achievements_cached_at` `AchievementsCachedAt` datetime(6) NULL,
                CHANGE COLUMN `drops_remaining` `DropsRemaining` int NULL,
                CHANGE COLUMN `saved_idle_delta_minutes` `SavedIdleDeltaMinutes` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `created_at` `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `updated_at` `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql(@"ALTER TABLE `achievement`
                CHANGE COLUMN `id` `Id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `game_id` `GameId` int NOT NULL,
                CHANGE COLUMN `app_id` `AppId` int NOT NULL,
                CHANGE COLUMN `api_name` `ApiName` varchar(255) NOT NULL,
                CHANGE COLUMN `display_name` `DisplayName` longtext NOT NULL,
                CHANGE COLUMN `display_name_i18n` `DisplayNameI18n` longtext NULL,
                CHANGE COLUMN `description` `Description` longtext NULL,
                CHANGE COLUMN `description_i18n` `DescriptionI18n` longtext NULL,
                CHANGE COLUMN `icon_url` `IconUrl` longtext NULL,
                CHANGE COLUMN `icon_gray_url` `IconGrayUrl` longtext NULL,
                CHANGE COLUMN `global_percent` `GlobalPercent` double NOT NULL,
                CHANGE COLUMN `unlock_offset_minutes` `UnlockOffsetMinutes` int NOT NULL,
                CHANGE COLUMN `is_unlocked` `IsUnlocked` tinyint(1) NOT NULL,
                CHANGE COLUMN `unlocked_at` `UnlockedAt` datetime(6) NULL,
                CHANGE COLUMN `created_at` `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `updated_at` `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql(@"ALTER TABLE `steam_config`
                CHANGE COLUMN `id` `Id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `username` `Username` longtext NOT NULL,
                CHANGE COLUMN `password_enc` `PasswordEnc` longtext NULL,
                CHANGE COLUMN `session_token` `SessionToken` longtext NULL,
                CHANGE COLUMN `session_updated_at` `SessionUpdatedAt` datetime(6) NULL,
                CHANGE COLUMN `web_api_key` `WebApiKey` longtext NULL,
                CHANGE COLUMN `sync_cron` `SyncCron` longtext NOT NULL,
                CHANGE COLUMN `language` `Language` longtext NOT NULL,
                CHANGE COLUMN `display_timezone` `DisplayTimezone` longtext NOT NULL DEFAULT ('UTC'),
                CHANGE COLUMN `created_at` `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                CHANGE COLUMN `updated_at` `UpdatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql(@"ALTER TABLE `play_queue`
                CHANGE COLUMN `id` `Id` int NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `game_id` `GameId` int NOT NULL,
                CHANGE COLUMN `sort_order` `SortOrder` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `saved_session_minutes` `SavedSessionMinutes` int NOT NULL DEFAULT 0,
                CHANGE COLUMN `is_active` `IsActive` tinyint(1) NOT NULL DEFAULT 0,
                CHANGE COLUMN `added_at` `AddedAt` datetime(6) NOT NULL");

            migrationBuilder.Sql(@"ALTER TABLE `steam_audit_log`
                CHANGE COLUMN `id` `Id` bigint NOT NULL AUTO_INCREMENT,
                CHANGE COLUMN `source` `Source` varchar(50) NOT NULL,
                CHANGE COLUMN `operation` `Operation` varchar(100) NOT NULL,
                CHANGE COLUMN `app_id` `AppId` int NULL,
                CHANGE COLUMN `request_summary` `RequestSummary` varchar(500) NULL,
                CHANGE COLUMN `success` `Success` tinyint(1) NOT NULL,
                CHANGE COLUMN `response_summary` `ResponseSummary` varchar(1000) NULL,
                CHANGE COLUMN `duration_ms` `DurationMs` int NOT NULL,
                CHANGE COLUMN `created_at` `CreatedAt` datetime(6) NOT NULL");
        }
    }
}
