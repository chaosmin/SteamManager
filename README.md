# SteamManager

A self-hosted Steam automation service that idles game hours and unlocks achievements on a realistic schedule — deployable on a NAS or any Docker host, no Steam client required.

[![Build & Publish](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml)
[![Docker Pulls](https://img.shields.io/docker/pulls/chaosmin/steam-manager)](https://hub.docker.com/r/chaosmin/steam-manager)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Features

- **Game Hour Idling** — simulates gameplay to accumulate hours toward a configurable target
- **Achievement Auto-Unlock** — derives unlock timing from [SteamHunters](https://steamhunters.com) median completion data; falls back to global unlock percentage ordering when unavailable
- **Catch-up on Restart** — overdue achievements are unlocked in sequence (with 1–100 s random gaps) the moment a game session resumes
- **Checkpoint Resume** — tracks progress per-minute in MySQL; restarts pick up exactly where you left off
- **Persistent Session** — logs in once with mobile 2FA, encrypts the refresh token (AES-256), and restores automatically on every startup
- **Multi-language UI** — game and achievement names displayed in English or Simplified Chinese (Steam's official localizations, English fallback)
- **Background Sync** — scheduled (cron) or on-demand sync refreshes your library and all achievement data in one click
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

Games are added automatically when you click **Sync Now** in Settings — it imports your entire Steam library. You can also add individual games manually via the Games page.

### Refreshing achievement data

Open a game's detail page and click **Refresh**. This fetches:
1. Achievement schema from Steam (names, icons, global unlock rates) in your chosen language
2. Median completion time from SteamHunters to derive realistic unlock offsets
3. Your own current unlock status from Steam

### Achievement scheduling

When a game is started, SteamManager:

1. Distributes achievement unlock times proportionally across the SteamHunters median completion window
2. On start, immediately unlocks any achievements whose offset has already been passed (catch-up), with 1–100 s random gaps between each
3. During play, polls every ~30 s and unlocks newly-due achievements with a 1–100 s random pre-unlock delay
4. Falls back to global unlock percentage ordering if SteamHunters data is unavailable

> A Steam Web API key is required for achievement data. Get one at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey) and save it in **Settings**.

### Background sync

Configure a **cron schedule** in Settings (default: daily at midnight) for automatic library + achievement sync. Use **Sync Now** to trigger immediately. A progress bar shows per-game status in real time.

### Language

Set your preferred display language in Settings → Language. Supports **English** and **简体中文**. Steam's official localizations are used; English is the fallback when no translation exists for a game or achievement.

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
│   ├── Services/              # Session, idle, scheduler, sync, achievement calculator
│   └── Dto/                   # Data transfer objects
└── SteamManager.Infrastructure/
    ├── Steam/                 # SteamClientWrapper (SteamKit2), AchievementHandler
    ├── Http/                  # Steam Web API client, SteamHunters client
    ├── Crypto/                # AES-256 encryption utility
    └── Persistence/           # AppDbContext, EF Core migrations
tests/
├── SteamManager.Core.Tests/           # Achievement interval calculator tests
└── SteamManager.Infrastructure.Tests/ # AES encryption tests
```

## Building from Source

```bash
# Install .NET 8 SDK
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

Images are published to [Docker Hub](https://hub.docker.com/r/chaosmin/steam-manager) on every push to `master` (`:latest`) and on version tags (`:v0.2.0`).

## Acknowledgements

- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam protocol implementation
- [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) — reference for SteamKit2 idling approach
- [ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager) — reference for `ClientStoreUserStats2` achievement unlock approach
- [SteamHunters](https://steamhunters.com) — median completion time data API

## License

[MIT](LICENSE)
