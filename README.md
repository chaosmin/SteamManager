# SteamManager

A self-hosted Steam automation service that idles game hours and unlocks achievements on a realistic schedule — deployable on a NAS or any Docker host, no Steam client required.

[![Build & Publish](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml)
[![Docker Pulls](https://img.shields.io/docker/pulls/chaosmin/steam-manager)](https://hub.docker.com/r/chaosmin/steam-manager)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Features

- **Game Hour Idling** — simulates gameplay to accumulate hours toward a configurable target
- **Achievement Auto-Unlock** — two-step `GetUserStats → StoreUserStats2` protocol; derives unlock timing from [SteamHunters](https://steamhunters.com) median completion data; falls back to global unlock percentage ordering
- **In-app Achievement Toast** — popup notification (with icon) when an achievement is unlocked, matching Steam overlay style
- **Catch-up on Restart** — overdue achievements unlock in sequence (1–100 s random gaps) the moment a game session resumes
- **Checkpoint Resume** — tracks progress per-minute in MySQL; restarts pick up exactly where you left off
- **Steam Trading Card Drops** — tracks remaining card drops per game via Steam Community badge page; filter toggle on Games page; synced automatically with library sync
- **Persistent Session** — logs in once with mobile 2FA, encrypts the refresh token (AES-256), and restores on every startup
- **Dashboard** — two live summary panels (total remaining idle time + remaining achievements across all games); currently playing section with cover art, idle progress, achievement progress, and next-achievement countdown; auto-refreshes every minute
- **Single-game Enforcement** — starting a new game automatically stops the previously running game
- **Multi-language UI** — game and achievement names in English or Simplified Chinese (Steam's official localizations, English fallback); language selector in the top nav bar
- **Timezone** — display timezone selector in the top nav bar, applied globally
- **Background Sync** — scheduled (cron) or on-demand sync refreshes your library, achievement data, and card drops in one click
- **Web UI** — Blazor Server dark-theme dashboard with real-time progress, game management, search/filter, and an optional access password

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

### Starting / stopping games

Go to **Games** and click the play button on any game card. Only one game can run at a time — starting a new game automatically stops the current one.

### Trading card drops

The **Card drops remaining** filter on the Games page shows only games with drops left. Card drop counts are refreshed automatically during **Sync Library**.

### Achievement scheduling

When a game is started, SteamManager:

1. Refreshes achievement data from Steam (ensuring schema is current)
2. Immediately unlocks any overdue achievements (catch-up), with 1–100 s random gaps
3. During play, polls every ~30 s and unlocks newly-due achievements with a 1–100 s pre-unlock delay
4. Uses the correct two-step `CMsgClientGetUserStats → CMsgClientStoreUserStats2` protocol with binary KeyValue schema parsing — same approach as [ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager)

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

### v0.2.2
- Auto-reconnect on unexpected Steam disconnect: session is restored automatically from the saved token (5 s delay, exponential backoff); game idling resumes immediately after reconnect
- Fix duplicate callback loop: `Connect()` now cancels the previous loop before starting a new one
- Sync on demand only: background sync no longer falls back to a default daily schedule — it waits for a manual trigger unless a cron expression is explicitly configured in Settings

### v0.2.1
- Fix achievement unlock: proper two-step `GetUserStats → StoreUserStats2` with binary KeyValue schema parsing
- Add in-app achievement unlock toast (icon + game name)
- Add Steam trading card drops tracking and filter toggle on Games page
- Redesign Dashboard: global summary panels + currently playing with next-achievement countdown + 1-min auto-refresh
- Enforce single-game: starting a game auto-stops the current one
- Move Language and Timezone selectors to top nav bar
- Reorganize Settings: Web API Key moved under Steam Account
- Add achievement description display in GameDetail

### v0.2.0
- Multi-language support (English / 简体中文)
- Achievement unlock scheduling with SteamHunters data
- Global background sync with cron schedule
- MudBlazor Material Design UI

## Acknowledgements

- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam protocol implementation
- [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) — reference for SteamKit2 idling approach
- [ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager) — reference for `ClientStoreUserStats2` achievement unlock approach
- [SteamHunters](https://steamhunters.com) — median completion time data API

## License

[MIT](LICENSE)
