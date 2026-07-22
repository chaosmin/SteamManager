# SteamManager

A self-hosted Steam automation service that idles game hours and unlocks achievements on a realistic schedule — deployable on a NAS or any Docker host, no Steam client required.

[![Build & Publish](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml)
[![Docker Pulls](https://img.shields.io/docker/pulls/chaosmin/steam-manager)](https://hub.docker.com/r/chaosmin/steam-manager)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Features

- **Game Hour Idling** — simulates gameplay to accumulate hours toward a target derived from [SteamHunters](https://steamhunters.com) Median Completion Time (MCT)
- **Achievement Auto-Unlock** — two-step `GetUserStats → StoreUserStats2` protocol; derives unlock timing from a linked reference player's real achievement timestamps; absolute UTC schedule assigned at Refresh; global 60 s scanner fires unlocks
- **Reference Player** — link any SteamHunters player URL to a game; Refresh uses their unlock intervals to build the schedule; burst detection blocks suspicious data (≥ 5 achievements sharing the same timestamp)
- **Game Queue** — dedicated idle queue with drag-and-drop reordering; configurable concurrent slot count (default 1, adjustable in Settings); queue auto-advances when a game reaches MCT
- **Game States** — `Idle → Playing → Scheduled → Completed`; `Scheduled` = MCT reached, game offline, achievements still unlocking; `Completed` = MCT + all achievements done
- **Smart Restart Recovery** — re-anchors scheduled unlock times on server restart using reference-interval math to prevent burst unlocks
- **In-app Achievement Toast** — popup notification (with icon) when an achievement is unlocked, matching Steam overlay style
- **Steam Trading Card Drops** — tracks remaining card drops per game; filter toggle on Games page; synced with library sync
- **Persistent Session** — logs in once with mobile 2FA, encrypts the refresh token (AES-256), restores on every startup
- **Dashboard** — global stat cards (remaining idle time + remaining achievements); live queue panel with drag-and-drop; per-game playing cards with cover art, progress bars, and next-achievement countdown; auto-refreshes every minute with live SignalR updates
- **Multi-language UI** — game and achievement names in English or Simplified Chinese; language selector in top nav
- **Timezone** — display timezone selector in top nav, applied globally
- **Background Sync** — scheduled (cron) or on-demand sync refreshes library, achievement data, and card drops in one click
- **Web UI** — Blazor Server dark-theme with skeleton loading, search/filter, and optional access password

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 8 / ASP.NET Core |
| UI | Blazor Server + MudBlazor (dark theme) |
| Steam | SteamKit2 (no Steam client needed) |
| Database | MySQL 8 + EF Core 8 + Pomelo |
| Logging | Serilog (console + rolling file) |
| Container | Docker / Docker Compose |

## Quick Start

### Prerequisites

- Docker & Docker Compose
- A running MySQL 8 instance (external — not bundled)
- A Steam account with mobile authenticator enabled

### 1. Configure environment

```bash
cp .env.example .env
```

Edit `.env`:

```env
SESSION_ENCRYPTION_KEY=<random string, min 32 chars>
UI_ACCESS_PASSWORD=<your chosen password>

DB_HOST=192.168.1.100
DB_PORT=3306
DB_NAME=steam_manager
DB_USER=steam_mgr
DB_PASSWORD=your-db-password
```

> The app will refuse to start if `SESSION_ENCRYPTION_KEY` is missing.

### 2. Create the database and user

```sql
CREATE DATABASE steam_manager CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'steam_mgr'@'%' IDENTIFIED BY 'your-password';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES
  ON steam_manager.* TO 'steam_mgr'@'%';
```

### 3. Run

```bash
docker compose up -d
```

The app runs on port `8080`. On first startup it applies EF Core migrations automatically.

Open `http://your-host:8080` → go to **Settings** → log in with your Steam credentials + mobile 2FA code.

## Usage

### Adding games

Click **Sync Library** on the Games page to import your entire Steam library. This also syncs remaining trading card drops per game. Requires a Steam Web API key (save it in **Settings → Steam Web API Key**).

### Dashboard

The dashboard shows:
- **Remaining Idle Time** — total hours left across all games, with an overall progress bar
- **Remaining Achievements** — total achievements left across all games, with an overall progress bar
- **Currently Playing** — if a game is running: cover art, remaining idle time, achievement progress, and a countdown to the next scheduled achievement unlock

The dashboard auto-refreshes every minute. Live idle-time updates stream in real time via SignalR.

### Adding games to the queue

On the **Games** page, click **Add to Queue** (requires a reference player configured in the game detail page). Start the queue from the Dashboard. Drag rows in the queue panel to reorder. The queue auto-advances when a game reaches its MCT.

### Trading card drops

The **Card drops remaining** filter on the Games page shows only games with drops left. Card drop counts are refreshed automatically during **Sync Library**.

### Achievement scheduling

Configure a **reference player** (SteamHunters URL) in the game detail page, then click **Refresh**. SteamManager:

1. Fetches the reference player's actual achievement unlock timestamps
2. Assigns each achievement a concrete UTC unlock time preserving the relative intervals
3. A global scanner fires every 60 s and unlocks any achievement whose scheduled time has passed
4. Uses the two-step `CMsgClientGetUserStats → CMsgClientStoreUserStats2` protocol — same approach as [ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager)

### Language & Timezone

Both are accessible in the **top navigation bar** on every page. Changes apply immediately and persist without going to Settings.

### Background sync

Configure a **cron schedule** in Settings for automatic library + achievement sync. Without a cron expression the sync service idles until you click **Sync Now** — no default schedule is applied.

## Configuration Reference

| Variable | Required | Description |
|---|---|---|
| `SESSION_ENCRYPTION_KEY` | ✅ | AES-256 key for encrypting the Steam refresh token (min 32 chars) |
| `UI_ACCESS_PASSWORD` | | Password for the Web UI (leave empty to disable auth) |
| `DB_HOST` | ✅ | MySQL host |
| `DB_PORT` | | MySQL port (default `3306`) |
| `DB_NAME` | ✅ | Database name |
| `DB_USER` | ✅ | Database user |
| `DB_PASSWORD` | ✅ | Database password |

## Project Structure

```
src/
├── SteamManager.Web/          # Blazor Server app, middleware, DI wiring
├── SteamManager.Core/         # Domain models, service interfaces, business logic
│   ├── Models/                # EF entities (Game, Achievement, SteamConfig)
│   ├── Services/              # Session, idle, scheduler, sync, notifier
│   └── Dto/                   # Data transfer objects
└── SteamManager.Infrastructure/
    ├── Steam/                 # SteamClientWrapper, AchievementHandler, UserStatsHandler
    ├── Http/                  # SteamWebApiClient, SteamHuntersClient, SteamCommunityClient
    ├── Crypto/                # AES-256 encryption utility
    └── Persistence/           # AppDbContext, EF Core migrations
tests/
├── SteamManager.Core.Tests/           # Achievement interval calculator tests
└── SteamManager.Infrastructure.Tests/ # AES encryption tests
```

## Building from Source

```bash
dotnet restore
dotnet build
dotnet test

# Run locally (requires .env in project root)
export $(grep -v '^#' .env | xargs)
dotnet run --project src/SteamManager.Web
```

## Docker Image

```bash
docker pull chaosmin/steam-manager:latest
```

Images are published to [Docker Hub](https://hub.docker.com/r/chaosmin/steam-manager) on every push to `master` (`:latest`) and on version tags (`:v0.2.2`).

## Changelog

### v0.4.1

- **Bug fix** — Stop Queue now pauses (preserves queue entry) instead of permanently removing the game
- **Bug fix** — Force Replay now correctly re-queues Completed games after reset
- **Bug fix** — Queue Progress no longer empty after pause+resume (scheduled times preserved through pause)
- **UI** — Games page filter bar redesigned: chip-based status + property filters, result count, Clear All button
- **UI** — GameDetail redesigned: hero banner with cover art + gradient, stats strip, Steam description + tags (Simplified Chinese), SteamHunters button in hero
- **Feature** — Steam Store description and tags integrated via `store.steampowered.com/api/appdetails`

See [docs/changelog/](docs/changelog/) for full version history.

## Acknowledgements

- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam protocol implementation
- [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) — reference for SteamKit2 idling approach
- [ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager) — reference for `ClientStoreUserStats2` achievement unlock approach
- [SteamHunters](https://steamhunters.com) — median completion time data API

## License

[MIT](LICENSE)
