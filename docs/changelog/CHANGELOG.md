# Changelog

All notable changes to SteamManager are documented here.

---

## v0.2.2 — 2026-06-29

### Fixed
- **Auto-reconnect on unexpected disconnect**: `SteamSessionService` now watches for non-user-initiated disconnects and automatically restores the session from the saved token after a 5-second delay. Game idling resumes immediately after reconnect via a new `OnLoggedOn` hook in `GameIdleService`.
- **Duplicate callback loop**: `SteamClientWrapper.Connect()` now cancels the previous callback loop before starting a new one, preventing silent loop leaks on reconnect.
- **`OnDisconnected` event signature**: Changed from `Action?` to `Action<bool>?` to expose the `userInitiated` flag, allowing reconnect logic to correctly skip user-initiated logouts.

### Changed
- **Background sync no longer has a default schedule**: If `SyncCron` is not configured in Settings, the sync service blocks and waits for a manual trigger only. Previously it fell back to `0 0 * * *` (daily midnight) regardless of configuration.

---

## v0.2.1 — 2026-06-28

### Fixed
- **Achievement unlock protocol**: Rewrote `AchievementHandler` and `UserStatsHandler` to implement the correct two-step `CMsgClientGetUserStats → CMsgClientStoreUserStats2` flow with binary KeyValue schema parsing, matching the approach used by ASFAchievementManager. Previous implementation failed silently.
- **Achievement data loading on game start**: Made the initial achievement refresh fire-and-forget so that game idling starts immediately without blocking on the HTTP fetch.

### Added
- **In-app achievement unlock toast**: A popup (icon + achievement name + game name) appears in the AppBar area whenever an achievement is unlocked, routed via `AchievementUnlockNotifier` singleton event bus.
- **Steam trading card drops tracking**: `SteamCommunityClient` scrapes the Steam Community badge page to populate `DropsRemaining` per game. A "Card drops remaining" filter toggle is added to the Games page. Card sync runs automatically with every Sync Library call.
- **Dashboard redesign**: Two global summary panels (total remaining idle hours + remaining achievements, each with a progress bar) and a "Currently Playing" section showing cover art, idle progress, achievement progress, and a countdown to the next scheduled achievement unlock. Auto-refreshes every minute.
- **Single-game enforcement**: Starting a new game automatically stops the previously running game with a Snackbar notification.
- **Achievement descriptions**: `Description` and `DescriptionI18n` fields added to the achievement table and displayed in the GameDetail page.

### Changed
- **Language and timezone selectors** moved from the Settings page to the AppBar, accessible from every page.
- **Settings page reorganised**: Steam Web API Key moved under the Steam Account card.

---

## v0.2.0 — 2026-06-28

### Added
- **Multi-language support**: All game and achievement names are stored in both English (`Name`, `DisplayName`) and Simplified Chinese (`NameI18n`, `DisplayNameI18n`). UI always displays the localised name with English fallback. Language toggle in Settings.
- **SteamHunters-based achievement scheduling**: Fetches `medianCompletionTime` from `steamhunters.com/api/apps/{appId}` and distributes unlock offsets proportionally across the median window (`CalculateFallbackScaled`). Falls back to global unlock-percentage ordering when SteamHunters data is unavailable.
- **Two-phase unlock scheduler**: Phase 1 (catch-up) unlocks all overdue achievements in sequence with 1–100 s random gaps on game start or restart. Phase 2 (polling) checks for newly due achievements every ~30 s with ±10 % jitter and a 1–100 s pre-unlock delay.
- **Background sync service** (`SyncBackgroundService`): cron-scheduled or on-demand. Syncs the full library (play times + localised names), card drops, and achievement data for all games. Settings page shows a live progress bar (0 → 100 %) with the current game name.
- **Reference completion time**: `ReferencePlayMinutes` (SteamHunters median) stored on `Game`, displayed in GameDetail and used as the progress bar reference on game cards.
- **Sync Now button** in Settings.

### Changed
- `EnableAchievements` field removed — all games with achievement data are scheduled automatically.
- Games page: fuzzy search box, "has achievements" filter, "not 100%" filter, green game name for fully completed games.
- GameDetail: Refresh button to manually re-fetch achievement data; unlock offset displayed as `X.XXh`.

---

## v0.1.0 — 2026-06-27

Initial release.

### Added
- **Project scaffold**: three-project solution (`SteamManager.Web`, `SteamManager.Core`, `SteamManager.Infrastructure`) targeting .NET 8.
- **Domain models**: `Game`, `Achievement`, `SteamConfig` EF Core entities backed by MySQL 8 via Pomelo. All datetime columns stored in UTC. Auto-migration on startup.
- **AES-256 encryption**: `AesEncryption` utility for encrypting the Steam refresh token at rest. Covered by unit tests (TDD).
- **SteamClientWrapper**: SteamKit2 wrapper with callback loop and exponential-backoff reconnection (5 s → 10 s → … → 5 min).
- **SteamSessionService**: 2FA-capable login flow using `IAuthenticator` polling. Encrypts and persists the refresh token on success; clears plaintext password. Session restored automatically on startup.
- **GameIdleService**: Sends `CMsgClientGamesPlayed` to simulate gameplay. Increments `TotalPlayMinutes` in the database every minute. Stops automatically at `TargetHours × 60` minutes.
- **AchievementIntervalCalculator**: Pure-static utility. `CalculateFallback` estimates offsets from global unlock percentages; `Calculate` computes median offsets from real player timestamps (reserved).
- **AchievementDataService**: Fetches achievement schema from Steam Web API, global percentages, SteamHunters data, and current player unlock state. 7-day cache per game.
- **UnlockSchedulerService**: Polls every ~30 s; unlocks due achievements with 1–100 s jitter.
- **AchievementHandler**: First implementation of `CMsgClientStoreUserStats2` unlock (superseded in v0.2.1).
- **StartupRecoveryService**: Restores Steam session and resumes any `Running` games automatically on app start.
- **Web UI**: Blazor Server dark-theme app with Dashboard, Games list, GameDetail, and Settings pages. `UiAuthMiddleware` for optional password protection.
- **Docker**: Multi-stage `Dockerfile` + `docker-compose.yml`. `SESSION_ENCRYPTION_KEY` missing causes startup failure.
- **CI**: GitHub Actions workflow publishes Docker image to Docker Hub on version tags and creates a GitHub Release automatically.

---
---

# 变更记录

以下记录 SteamManager 各版本的所有重要变更。

---

## v0.2.2 — 2026-06-29

### 修复
- **意外断连后自动重连**：`SteamSessionService` 现在监听非用户主动的断连事件，5 秒延迟后自动从数据库保存的 token 恢复会话。`GameIdleService` 新增 `OnLoggedOn` 钩子，重连成功后立即重发 `CMsgClientGamesPlayed`，恢复所有正在挂机的游戏。
- **重复 callback loop 泄漏**：`SteamClientWrapper.Connect()` 现在先取消旧的 callback loop 再创建新的，防止每次重连都泄漏一个后台循环。
- **`OnDisconnected` 事件签名**：从 `Action?` 改为 `Action<bool>?`，透传 `userInitiated` 标志，使重连逻辑能正确区分用户主动退出和服务端断连。

### 变更
- **后台同步不再有默认计划**：若 Settings 中未配置 `SyncCron`，同步服务持续阻塞等待手动触发，不再 fallback 到每天 0 点（`0 0 * * *`）。

---

## v0.2.1 — 2026-06-28

### 修复
- **成就解锁协议**：重写 `AchievementHandler` 和 `UserStatsHandler`，正确实现 `CMsgClientGetUserStats → CMsgClientStoreUserStats2` 两步协议及 binary KeyValue schema 解析（参考 ASFAchievementManager），修复此前无声失败的问题。
- **游戏启动时成就数据加载阻塞**：将初始成就刷新改为 fire-and-forget，游戏挂机立即启动，不再等待 HTTP 请求完成。

### 新增
- **成就解锁 Toast**：成就解锁时在 AppBar 下方弹出通知（图标 + 成就名 + 游戏名），通过 `AchievementUnlockNotifier` 单例事件总线路由。
- **Steam 集换卡牌掉落追踪**：`SteamCommunityClient` 爬取 Steam 社区徽章页面，更新每款游戏的 `DropsRemaining`。Games 页新增"Card drops remaining"筛选开关，卡牌同步随每次 Sync Library 自动执行。
- **Dashboard 重设计**：两个全局统计面板（剩余挂机总时长 + 剩余成就总数，各含进度条）+ 当前运行游戏区块（封面图、挂机进度、成就进度、下一个成就解锁倒计时），每分钟自动刷新。
- **单游戏限制**：启动新游戏自动停止正在运行的游戏，并显示 Snackbar 通知。
- **成就描述**：`Achievement` 实体新增 `Description` / `DescriptionI18n` 字段，在 GameDetail 页面展示。

### 变更
- **语言和时区选择器**从 Settings 页移至 AppBar，所有页面均可访问。
- **Settings 页重组**：Steam Web API Key 移至 Steam 账号卡片下方。

---

## v0.2.0 — 2026-06-28

### 新增
- **多语言支持**：游戏名和成就名分别存储英语（`Name`、`DisplayName`）和简体中文（`NameI18n`、`DisplayNameI18n`），UI 优先显示本地化名称，英语兜底。Settings 页新增语言切换。
- **基于 SteamHunters 的成就解锁调度**：从 `steamhunters.com/api/apps/{appId}` 获取 `medianCompletionTime`，按比例将各成就的解锁时间点分布在中位完成窗口内（`CalculateFallbackScaled`）。SteamHunters 不可用时降级为按全球解锁率排序估算。
- **两阶段解锁调度器**：Phase 1（补解锁）在游戏启动/恢复时按序解锁所有超期成就，每条间隔 1–100 秒随机延迟；Phase 2（轮询）约每 30 秒（±10% 抖动）检查新到期成就，解锁前随机等待 1–100 秒。
- **后台同步服务**（`SyncBackgroundService`）：支持 Cron 定时和手动触发，一次同步涵盖全库更新（游戏时长 + 本地化名称）、卡牌掉落和所有游戏的成就数据。Settings 页显示实时进度条（0→100%）及当前游戏名称。
- **参考完成时长**：`ReferencePlayMinutes`（SteamHunters 中位数）存储在 `Game` 实体，在 GameDetail 和游戏卡片进度条中展示。
- **Settings 页新增 Sync Now 按钮**。

### 变更
- 删除 `EnableAchievements` 字段，所有有成就数据的游戏均自动纳入调度。
- Games 页：新增模糊搜索框、"有成就"筛选、"未 100%"筛选，全成就游戏名称显示绿色。
- GameDetail：新增 Refresh 按钮手动重拉成就数据；解锁时间点格式改为 `X.XXh`。

---

## v0.1.0 — 2026-06-27

初始版本发布。

### 新增
- **项目骨架**：三项目解决方案（`SteamManager.Web`、`SteamManager.Core`、`SteamManager.Infrastructure`），目标框架 .NET 8。
- **领域模型**：`Game`、`Achievement`、`SteamConfig` EF Core 实体，数据库使用 MySQL 8 + Pomelo。所有时间字段存储 UTC，启动时自动执行 EF Core 迁移建表。
- **AES-256 加密**：`AesEncryption` 工具类，用于静态加密保存 Steam refresh token，包含 TDD 单元测试。
- **SteamClientWrapper**：SteamKit2 封装，含 callback loop 和指数退避重连（5s → 10s → … → 5min）。
- **SteamSessionService**：支持 2FA 的登录流程（`IAuthenticator` 轮询），登录成功后加密持久化 refresh token 并清除明文密码；启动时自动恢复 Session。
- **GameIdleService**：发送 `CMsgClientGamesPlayed` 模拟游戏运行，每分钟更新数据库 `TotalPlayMinutes`，达到 `TargetHours × 60` 分钟后自动停止。
- **AchievementIntervalCalculator**：纯静态工具，`CalculateFallback` 按全球解锁率估算偏移量；`Calculate` 从玩家真实解锁时间戳计算中位偏移量（保留备用）。
- **AchievementDataService**：从 Steam Web API 拉取成就 schema、全球解锁率、SteamHunters 数据及当前用户解锁状态，7 天缓存。
- **UnlockSchedulerService**：约每 30 秒轮询一次，对到期成就加 1–100 秒随机抖动后解锁。
- **AchievementHandler**：`CMsgClientStoreUserStats2` 解锁初版实现（v0.2.1 重写）。
- **StartupRecoveryService**：启动时恢复 Steam 会话并自动恢复所有 `Running` 状态的游戏。
- **Web UI**：Blazor Server 深色主题，包含 Dashboard、Games、GameDetail、Settings 页面；`UiAuthMiddleware` 提供可选访问密码保护。
- **Docker**：多阶段 `Dockerfile` + `docker-compose.yml`；`SESSION_ENCRYPTION_KEY` 未设置时启动失败。
- **CI**：GitHub Actions 在版本 tag 时自动推送 Docker 镜像到 Docker Hub 并创建 GitHub Release。
