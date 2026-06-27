# SteamManager

A self-hosted Steam automation service that idles game hours and unlocks achievements on a realistic schedule — deployable on a NAS or any Docker host, no Steam client required.

[![Build & Publish](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/chaosmin/SteamManager/actions/workflows/docker-publish.yml)
[![Docker Pulls](https://img.shields.io/docker/pulls/chaosmin/steam-manager)](https://hub.docker.com/r/chaosmin/steam-manager)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Features

- **Game Hour Idling** — simulates gameplay to accumulate hours toward a configurable target
- **Achievement Auto-Unlock** — derives unlock intervals from real 100%-completion players via [SteamHunters](https://steamhunters.com), falls back to global unlock percentage ordering
- **Checkpoint Resume** — tracks progress per-minute in MySQL; picking up where you left off after a restart takes seconds
- **Persistent Session** — logs in once with mobile 2FA, encrypts the refresh token (AES-256), and restores the session automatically on every startup
- **Web UI** — Blazor Server dashboard with real-time progress, game management, settings, and an optional access password
- **Timezone-aware** — stores all timestamps in UTC, displays them in your chosen IANA timezone

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 8 / ASP.NET Core |
| UI | Blazor Server + SignalR |
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

### Adding a game

1. Go to **Games**
2. Enter the App ID (find it on the Steam store URL, e.g. `730` for CS2), game name, and target hours
3. Toggle **Achievements** if you want auto-unlock enabled
4. Click **Add**, then **Start**

### Achievement scheduling

When a game starts with achievements enabled, SteamManager:

1. Fetches the achievement list from the Steam Web API
2. Queries SteamHunters for up to 20 players who reached 100% completion
3. Calculates the **median** unlock interval between achievements across those players
4. Falls back to global unlock percentage ordering if no player data is available
5. Persists the schedule to MySQL and unlocks each achievement once the accumulated playtime reaches its offset

> A Steam Web API key is required for achievement data. Get one at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey) and save it in **Settings**.

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
│   ├── Models/                # EF entities
│   ├── Services/              # Session, idle, scheduler, achievement calculator
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

Images are published to [Docker Hub](https://hub.docker.com/r/chaosmin/steam-manager) on every push to `master` (`:latest`) and on version tags (`:v0.1.0`).

## Acknowledgements

- [SteamKit2](https://github.com/SteamRE/SteamKit) — Steam protocol implementation
- [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) — reference for `ClientStoreUserStats2` achievement unlock approach
- [SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager) — reference implementation
- [SteamHunters](https://steamhunters.com) — perfect-game player data API

## License

[MIT](LICENSE)
