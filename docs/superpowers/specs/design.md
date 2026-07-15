# SteamManager 设计文档

**日期：** 2026-06-28（最后更新：v0.4.0 设计）
**状态：** 已批准
**项目：** SteamManager — 自动挂 Steam 游戏时长 + 智能成就解锁服务

---

## 1. 需求概述

实现一个可部署在 NAS 上的服务，功能：
1. **自动挂机**：模拟正在游玩指定游戏，自动累积 Steam 游戏时长
2. **智能成就解锁**：参考 SteamHunters 的全成就玩家中位完成时长，按比例分配各成就的解锁时间点，行为模式自然
3. **断点续传**：服务重启后，以启动时刻为基准重新锚定所有待解锁成就的计划时间点，避免瞬间补解锁大量成就；Playing 状态游戏已累计的挂机时长通过重读 Steam API 自动恢复
4. **Web UI**：通过浏览器操作，无需命令行；支持简体中文 / 英语双语展示
5. **Docker 部署**：打包为 Docker 镜像，部署在 NAS（群晖/QNAP 等）上
6. **Steam 集换卡牌掉落追踪**：通过 Steam 社区徽章页面抓取各游戏剩余卡牌掉落数
7. **双游戏并发限制**：最多同时 Playing 2 款游戏；Refresh 第 3 款时报错，需先手动停止一款；Stop 游戏会清空该游戏所有待解锁成就的 `scheduled_unlock_at`

---

## 2. 技术栈

| 层 | 技术 |
|----|------|
| 框架 | ASP.NET Core 8 + Blazor Server |
| Steam 协议 | SteamKit2（直接通信 Steam 网络，无需 Steam 客户端） |
| 实时 UI | SignalR（Blazor Server 内置） |
| 数据存储 | MySQL 8 + Entity Framework Core 8（Pomelo.EntityFrameworkCore.MySql） |
| 日志 | Serilog（输出到控制台 + 滚动文件） |
| 容器化 | Docker（多阶段构建）+ docker-compose |
| UI 组件库 | MudBlazor（深色主题） |
| Cron 解析 | Cronos |
| 运行时 | .NET 8，镜像约 200MB |

**参考实现：**
- 挂机机制：[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)（SteamKit2 挂机方式）
- 成就解锁：[ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager)（两步协议：`CMsgClientGetUserStats → CMsgClientStoreUserStats2`，binary KeyValue schema 解析）

---

## 3. 项目结构

```
SteamManager/
├── src/
│   ├── SteamManager.Web/                     # ASP.NET Core + Blazor Server（启动入口）
│   │   ├── Components/
│   │   │   ├── Layout/
│   │   │   │   ├── MainLayout.razor          # AppBar（含语言/时区选择器）+ 成就解锁 Toast
│   │   │   │   └── NavMenu.razor             # 侧边导航
│   │   │   └── Pages/
│   │   │       ├── Dashboard.razor           # 首页：全局统计面板 + 当前运行游戏详情
│   │   │       ├── Games.razor               # 游戏库：卡片网格 + 搜索/筛选 + Sync Library
│   │   │       ├── GameDetail.razor          # 游戏详情：成就列表（路由 /games/{Id}）
│   │   │       └── Settings.razor            # Steam 账号 + API Key + 定时同步
│   │   ├── Middleware/
│   │   │   └── UiAuthMiddleware.cs           # UI 访问密码保护
│   │   ├── Program.cs
│   │   └── appsettings.json
│   ├── SteamManager.Core/                    # 核心业务逻辑（无外部依赖）
│   │   ├── Services/
│   │   │   ├── ISteamSessionService.cs       # Steam 登录接口
│   │   │   ├── IGameIdleService.cs           # 挂时长接口
│   │   │   ├── IAchievementDataService.cs    # 成就数据获取接口
│   │   │   ├── IUnlockSchedulerService.cs    # 成就解锁调度接口
│   │   │   ├── ISyncService.cs               # 库同步服务接口
│   │   │   ├── AchievementUnlockNotifier.cs  # 解锁通知事件总线（singleton）
│   │   │   └── AchievementIntervalCalculator.cs  # 解锁时间点计算（纯静态工具）
│   │   ├── Models/
│   │   │   ├── Game.cs                       # 游戏实体（含 DropsRemaining）
│   │   │   ├── Achievement.cs                # 成就实体（含 Description/DescriptionI18n）
│   │   │   └── SteamConfig.cs                # Steam 账号配置（含 DisplayTimezone）
│   │   └── Dto/
│   │       ├── SteamAchievementDto.cs        # Steam Web API 成就数据传输对象
│   │       └── AchievementIntervalDto.cs     # 解锁间隔计算结果
│   └── SteamManager.Infrastructure/          # 外部依赖封装
│       ├── Persistence/
│       │   ├── AppDbContext.cs               # EF Core DbContext
│       │   ├── AppDbContextFactory.cs        # IDesignTimeDbContextFactory（EF 迁移工具用）
│       │   └── Migrations/                   # EF Core 迁移文件
│       ├── Services/
│       │   ├── SteamSessionService.cs        # Steam 登录 + Session 持久化
│       │   ├── GameIdleService.cs            # 挂时长逻辑（ProgressUpdated 事件）
│       │   ├── AchievementDataService.cs     # 成就数据拉取 + Upsert
│       │   ├── UnlockSchedulerService.cs     # 成就解锁调度（补解锁 + 轮询）
│       │   ├── SyncBackgroundService.cs      # 后台定时/手动触发全局同步
│       │   └── StartupRecoveryService.cs     # 启动时恢复 RUNNING 状态游戏
│       ├── Steam/
│       │   ├── SteamClientWrapper.cs         # SteamKit2 封装（连接/回调管理）
│       │   ├── AchievementHandler.cs         # 成就读写（两步协议封装）
│       │   └── UserStatsHandler.cs           # CMsgClientGetUserStats / StoreUserStats2 收发
│       ├── Http/
│       │   ├── SteamWebApiClient.cs          # Steam Web API 调用
│       │   ├── SteamHuntersClient.cs         # SteamHunters API（medianCompletionTime）
│       │   └── SteamCommunityClient.cs       # 抓取 Steam 社区徽章页面（卡牌掉落数）
│       └── Crypto/
│           └── AesEncryption.cs              # AES-256 加解密工具
├── tests/
│   ├── SteamManager.Core.Tests/              # AchievementIntervalCalculator 单元测试
│   └── SteamManager.Infrastructure.Tests/   # AES 加密单元测试
├── docs/superpowers/
│   ├── specs/design.md                       # 本文档
│   └── plans/
├── .env                                      # 本地开发敏感配置（不提交 Git）
├── .env.example                              # 配置模板
├── Dockerfile
├── docker-compose.yml
└── README.md
```

---

## 4. 数据库设计

ORM：Entity Framework Core 8，驱动：`Pomelo.EntityFrameworkCore.MySql`。

### 4.1 表结构

**所有 `DATETIME` 列均存储 UTC 时间。** 显示时在应用层转换。

```sql
-- Steam 账号配置（单行）
CREATE TABLE steam_config (
  id                 INT          PRIMARY KEY AUTO_INCREMENT,
  username           VARCHAR(64),
  password_enc       TEXT,                    -- AES-256 加密；登录成功后置 NULL
  web_api_key        VARCHAR(64),             -- Steam Web API Key（用于 GetOwnedGames 等）
  session_token      TEXT,                    -- AES-256 加密的 RefreshToken
  session_updated_at DATETIME,
  sync_cron          VARCHAR(100) NOT NULL DEFAULT '0 0 * * *',  -- 5-field cron，默认每天 0 点
  language           VARCHAR(16)  NOT NULL DEFAULT 'english',    -- 'english' 或 'schinese'
  display_timezone   VARCHAR(64)  NOT NULL DEFAULT 'UTC',        -- 显示时区（IANA 标识）
  created_at         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- 游戏信息
CREATE TABLE game (
  id                        INT          PRIMARY KEY AUTO_INCREMENT,
  app_id                    INT          NOT NULL UNIQUE,
  name                      VARCHAR(255) NOT NULL,           -- 英语名（永久保留）
  name_i18n                 VARCHAR(255),                    -- 本地化名（如简体中文），UI 优先显示
  status                    ENUM('Idle','Playing','Scheduled','Completed') NOT NULL DEFAULT 'Idle',
  -- Playing：游戏在线，MCT 未达标
  -- Scheduled：MCT 已达标，游戏已下线，仍有成就待按计划解锁
  -- Completed：MCT 达标 + 全成就解锁
  steam_playtime_at_refresh INT          NOT NULL DEFAULT 0, -- 最近一次 Start/Refresh 时读取的 Steam 时长（分钟）
  target_minutes            INT,                             -- MCT 或 fallback（分钟），Refresh 时写入
  session_started_at        DATETIME,                        -- 当前 Playing session 开始时刻，null = 未在挂机
  reference_play_minutes    INT,                             -- SteamHunters 中位完成时长（分钟，Refresh 时缓存）
  achievements_cached_at    DATETIME,                        -- 成就数据最后拉取时间
  drops_remaining           INT          NOT NULL DEFAULT 0, -- 剩余卡牌掉落数
  created_at                DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at                DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- 成就
CREATE TABLE achievement (
  id                    INT          PRIMARY KEY AUTO_INCREMENT,
  game_id               INT          NOT NULL,
  app_id                INT          NOT NULL,
  api_name              VARCHAR(128) NOT NULL,
  display_name          VARCHAR(255) NOT NULL,           -- 英语名（永久保留）
  display_name_i18n     VARCHAR(255),                    -- 本地化名，UI 优先显示
  description           TEXT,                            -- 英语描述
  description_i18n      TEXT,                            -- 本地化描述
  global_percent        DOUBLE       NOT NULL DEFAULT 0, -- 全球解锁率(%)
  icon_url              TEXT,                            -- 已解锁成就图标 URL
  icon_gray_url         TEXT,                            -- 未解锁成就图标（灰色）URL
  scheduled_unlock_at   DATETIME,                        -- 计划解锁时刻（UTC）；null = 未计划或已解锁后清空
  is_unlocked           TINYINT(1)   NOT NULL DEFAULT 0,
  unlocked_at           DATETIME,
  created_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  FOREIGN KEY (game_id) REFERENCES game(id) ON DELETE CASCADE,
  UNIQUE KEY uq_game_achievement (game_id, api_name)
);

-- 游戏挂机队列
CREATE TABLE game_queue (
  id         INT      PRIMARY KEY AUTO_INCREMENT,
  game_id    INT      NOT NULL UNIQUE,   -- 每款游戏最多出现一次
  position   INT      NOT NULL,          -- 排序位置（1-based），拖拽时更新
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (game_id) REFERENCES game(id) ON DELETE CASCADE
);
-- 入队条件：game_reference_player 存在 + 已做过 Refresh（有 scheduled_unlock_at 值）
-- 游戏 Completed 后自动从队列移除

-- 游戏参考玩家链接（每款游戏最多一条）
CREATE TABLE game_reference_player (
  id                   INT          PRIMARY KEY AUTO_INCREMENT,
  game_id              INT          NOT NULL UNIQUE,        -- FK → game.id，每款游戏仅允许一个参考玩家
  player_url           VARCHAR(512) NOT NULL,               -- SteamHunters 玩家主页 URL
  override_burst_check TINYINT(1)   NOT NULL DEFAULT 0,     -- 1 = 忽略同一时刻 ≥5 个成就的限制
  created_at           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  FOREIGN KEY (game_id) REFERENCES game(id) ON DELETE CASCADE
);
```

### 4.2 EF Core 实体对应

| 表名 | EF Core 实体 | 主要字段 |
|------|-------------|---------|
| `steam_config` | `SteamConfig` | `Username`, `PasswordEnc`, `WebApiKey`, `SessionToken`, `SyncCron`, `Language`, `DisplayTimezone` |
| `game` | `Game` | `AppId`, `Name`, `NameI18n`, `Status`, `SteamPlaytimeAtRefresh`, `TargetMinutes`, `SessionStartedAt`, `ReferencePlayMinutes`, `AchievementsCachedAt`, `DropsRemaining` |
| `achievement` | `Achievement` | `GameId`, `AppId`, `ApiName`, `DisplayName`, `DisplayNameI18n`, `Description`, `DescriptionI18n`, `GlobalPercent`, `IconUrl`, `IconGrayUrl`, `ScheduledUnlockAt`, `IsUnlocked`, `UnlockedAt` |
| `game_queue` | `GameQueue` | `GameId`, `Position` |
| `game_reference_player` | `GameReferencePlayer` | `GameId`, `PlayerUrl`, `OverrideBurstCheck` |

### 4.3 多语言字段规则

| 字段 | 写入规则 |
|------|---------|
| `Name` / `DisplayName` | 仅在 `language == "english"` 时写入；其他语言下保留不变 |
| `NameI18n` / `DisplayNameI18n` | 仅在 `language != "english"` 时写入；切换回 `english` 时清空 |
| UI 显示 | `NameI18n ?? Name`，`DisplayNameI18n ?? DisplayName` |

---

## 5. 核心服务设计

### 5.1 SteamSessionService

**职责：** 管理 Steam 账号登录和 Session 生命周期。

**接口：**
```csharp
public interface ISteamSessionService {
    LoginState State { get; }       // NotLoggedIn / AwaitingTwoFactor / LoggedIn / SessionExpired
    string? DisplayName { get; }
    ulong? SteamId64 { get; }
    event Action? StateChanged;

    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);
    Task<LoginState> BeginLoginAsync(string username, string password, CancellationToken ct = default);
    Task<bool> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default);
    Task LogoutAsync();
}
```

**首次登录流程：**
1. 用户在 Settings 页面输入用户名 + 密码，点击登录
2. Steam 要求 2FA 时：UI 弹出输入框，用户手动输入验证码
3. 登录成功后，`RefreshToken` 用 AES-256 加密，写入 `steam_config.session_token`，密码清空

**后续启动：**
- 从 `steam_config` 读取加密 `session_token`，解密后恢复 Session，无需人工干预
- Token 过期时，UI 标注"SessionExpired"，等待用户重新登录

### 5.2 SteamClientWrapper — 断线重连

`SteamClientWrapper` 提供两个连接相关能力：

- **`ConnectWithReconnectAsync`**：初始连接时的指数退避重试（5s → 10s → … → 5min），用于启动和登录流程
- **`OnDisconnected(bool userInitiated)`**：断连事件，携带是否用户主动的标志

**自动断线重连（`SteamSessionService`）：**  
登录成功后，`SteamSessionService` 订阅 `OnDisconnected`。若 `userInitiated=false`（Steam 服务端主动断连），5 秒后自动调用 `TryRestoreSessionAsync` 恢复会话。重连成功后：
- `SteamSessionService` 重新置 `LoggedIn` 状态并刷新 `DisplayName` / `SteamId64`
- `GameIdleService` 监听 `OnLoggedOn` 事件，重连后立即重发 `CMsgClientGamesPlayed`，恢复所有正在挂机的游戏

用户点击退出时，`LogoutAsync` 取消重连监听，不触发自动重连。

### 5.3 GameIdleService

**职责：** 通过 SteamKit2 向 Steam 服务器发送"正在游玩"状态；跟踪 session 时长；MCT 达标时自动停止挂机。

**接口（IGameIdleService）：**
```csharp
Task StartAsync(int appId, CancellationToken ct = default);
Task StopAsync(int appId);
IReadOnlyList<int> PlayingAppIds { get; }   // 当前 Playing 状态的游戏，最多 2 个
event Action<int appId, int elapsedMinutes>? ProgressUpdated;
event Action<int appId>? McTargetReached;
```

**并发限制：** `StartAsync` 时检查 `PlayingAppIds.Count`：
- `< 2` → 正常启动
- `≥ 2` → 抛出异常，UI 显示"最多同时运行 2 款游戏，请先停止一款"

**MCT 达标判断（每分钟检查）：**
```
elapsed = (now - game.SessionStartedAt).TotalMinutes
needed  = game.TargetMinutes - game.SteamPlaytimeAtRefresh
if elapsed >= needed:
    发送 CMsgClientGamesPlayed（空列表）下线游戏
    game.SessionStartedAt = null
    game.Status = 所有成就 IsUnlocked ? Completed : Scheduled
```

**Stop 行为（手动或 MCT 达标）：**
1. 向 Steam 发送下线
2. `game.SessionStartedAt = null`
3. 若手动 Stop（MCT 未达标）：清空该游戏所有 `achievement.ScheduledUnlockAt`（置 null），`game.Status = Idle`
4. 若 MCT 达标自动 Stop：不清空 `ScheduledUnlockAt`，状态按上述逻辑设为 Scheduled 或 Completed

### 5.4 AchievementDataService

**职责：** 游戏详情页"Refresh"的核心编排服务。拉取元数据、验证参考玩家数据、计算成就计划时间点、启动挂机。

**接口：** `RefreshAsync(int gameId, CancellationToken ct = default)`

**执行流程：**
```
前置检查：
  当前 Playing 游戏数量 ≥ 2 → 抛出异常（UI 报错，需先停止一款）

Step 1: Steam Web API: GetSchemaForGame(appid, language)
  → 成就列表、DisplayName（含本地化）、Description（含本地化）、IconUrl、IconGrayUrl

Step 2: Steam Web API: GetGlobalAchievementPercentagesForApp(appid)
  → GlobalPercent

Step 3: SteamHunters API: GET /api/apps/{appId}
  → medianCompletionTime（中位完成时长，分钟）
  → 有数据：game.TargetMinutes = medianMinutes，game.ReferencePlayMinutes = medianMinutes
  → 无数据（fallback）：拉取参考玩家的成就解锁时间跨度作为 TargetMinutes

Step 4: 拉取参考玩家成就解锁记录（SteamHuntersClient.GetPlayerAchievementsAsync）
  → 参考玩家来自 game_reference_player.player_url
  → 若无参考玩家 → 跳过 Step 5 ~ Step 6，直接用 GlobalPercent 估算（旧降级逻辑）
  → 若参考玩家不可用 → 同上降级

Step 5: 验证参考玩家数据
  → 统计同一时刻解锁的成就数量
  → 若任意时刻有 ≥ 5 个成就同时解锁，且 override_burst_check = 0 → 抛出异常，UI 报错提示

Step 6: 计算 scheduled_unlock_at（Method A：保留原始相对偏移）
  → 以参考玩家第一个成就解锁时刻为 T=0
  → refreshTime + (playerAchievement.UnlockedAt - playerFirstUnlock) = scheduled_unlock_at
  → 仅更新 is_unlocked = 0 的成就（已解锁成就保持 scheduled_unlock_at = null）
  → 若刷新的是已有 scheduled_unlock_at 的游戏（重复 Refresh），以新 refreshTime 重算所有未解锁成就

Step 7: UPSERT achievement 表（按 game_id + api_name 唯一键）
  → 多语言：language=="english" 写 DisplayName/Description + 清空 I18n 列
             language!="english" 写 DisplayNameI18n/DescriptionI18n + 保留英语列

Step 8: 同步当前用户已解锁成就状态（GetPlayerAchievements）
  → 已解锁成就：is_unlocked = 1，unlocked_at 保留，scheduled_unlock_at = null

Step 9: 读取 Steam 当前游戏时长 → game.SteamPlaytimeAtRefresh
  更新 game.AchievementsCachedAt = NOW()
  （此时不更新 Status，游戏保持 Idle 直到从队列启动）

Step 10: 调用 GameQueueService.AddToQueueAsync(gameId)
  → 若游戏已在队列中 → 保留原位置（仅更新 scheduled_unlock_at，不重排）
  → 若游戏不在队列 → 追加到队列末尾

完成检查：
  若 MCT 已达标（steam_playtime_at_refresh >= target_minutes）且所有成就已解锁
  → 直接标记 game.Status = Completed，不入队
```

**降级（无参考玩家或 SteamHunters 不可用）：** 按全球解锁率从高到低排序，均匀估算 scheduled_unlock_at 间隔。

**Force Refresh：** 在执行完整 RefreshAsync 之前，先调用 `AchievementHandler.ResetAllAsync(appId)` 将所有成就重置为未解锁，再从 Step 1 开始执行。仅允许对非 Playing 状态的游戏执行。

### 5.5 AchievementIntervalCalculator（纯静态）

| 方法 | 说明 |
|------|------|
| `CalculateScheduledTimes(playerRecords, refreshTime, alreadyUnlockedApiNames)` | **主路径**：Method A — 保留参考玩家原始相对偏移，锚定到 refreshTime；已解锁成就跳过。返回 `Dictionary<string apiName, DateTime scheduledUnlockAt>` |
| `CalculateFallback(schema, refreshTime, intervalPerPercent)` | 降级：按全球解锁率差值估算 scheduled_unlock_at（无参考玩家时用） |
| `CalculateFallbackScaled(schema, refreshTime, totalMinutes, intervalPerPercent)` | 降级：同上，但将最终时间点线性缩放到 totalMinutes 范围内 |
| `ValidateBurstThreshold(playerRecords, threshold = 5)` | 检查参考玩家记录中是否有任意时刻 ≥ threshold 个成就同时解锁；返回违规时刻列表 |

### 5.6 AchievementHandler — 两步解锁协议

**职责：** 通过 SteamKit2 读写用户成就数据。

**两步协议（参考 ASFAchievementManager）：**
```
Step 1: 发送 CMsgClientGetUserStats（EMsg.ClientGetUserStats）
        → 接收 CMsgClientGetUserStatsResponse（含 schema bytes）
        → 用 binary KeyValue 解析 schema，提取字段 ID 映射

Step 2: 构造 CMsgClientStoreUserStats2（EMsg.ClientStoreUserStats2）
        → 将目标成就的字段值设为 1
        → 发送并等待 CMsgClientStoreUserStats2Result
```

此协议绕过了 Steam 客户端验证，直接修改服务端成就记录。非服务端验证（server-side）的成就无法解锁，程序自动跳过并记录警告。

### 5.7 UnlockSchedulerService

**职责：** 全局分钟级扫描器，独立于具体游戏，定时解锁所有已到期的计划成就；并检查 Playing 游戏的 MCT 达标状态。

**运行逻辑（每 60 秒）：**
```
Step 1 — 解锁到期成就
  查询 achievement WHERE scheduled_unlock_at <= NOW()
                    AND is_unlocked = 0
                    AND scheduled_unlock_at IS NOT NULL
  按 scheduled_unlock_at 升序逐条解锁：
    → AchievementHandler.UnlockAsync(appId, apiName)
    → achievement.IsUnlocked = 1
    → achievement.UnlockedAt = NOW()
    → achievement.ScheduledUnlockAt = NULL
    → 触发 AchievementUnlockNotifier 通知 UI
    → 检查所属游戏：若 game.Status = Scheduled 且所有成就已解锁
         → game.Status = Completed
         → GameQueueService.RemoveFromQueueAsync(gameId)（自动触发 AdvanceQueueAsync）

Step 2 — MCT 到期检查（对所有 Playing 游戏）
  elapsed = (NOW() - game.SessionStartedAt).TotalMinutes
  needed  = game.TargetMinutes - game.SteamPlaytimeAtRefresh
  if elapsed >= needed → GameIdleService.StopAsync(appId, mcTargetReached: true)
```

无随机延迟、无抖动。自然间隔完全由 `scheduled_unlock_at` 字段本身控制。

**扫描范围：** 全局（不限于队列内游戏）。凡 `scheduled_unlock_at IS NOT NULL AND is_unlocked = 0` 均在范围内。解锁后 `scheduled_unlock_at = NULL`。

### 5.8 GameQueueService

**职责：** 管理游戏挂机队列；响应槽位释放事件自动推进队列；为 Dashboard 提供队列状态。

**接口（IGameQueueService）：**
```csharp
Task<bool> AddToQueueAsync(int gameId);       // 入队条件验证 + 追加末尾
Task RemoveFromQueueAsync(int gameId);
Task<IList<GameQueueEntry>> GetQueueAsync();  // 按 position 升序，含游戏基本信息
Task ReorderAsync(int gameId, int newPosition); // 拖拽排序
Task StartQueueAsync();                        // Dashboard Start 按钮触发
Task AdvanceQueueAsync();                      // 内部自动推进（槽位释放时调用）
```

**入队条件（AddToQueueAsync 内验证）：**
- `game_reference_player` 存在
- 游戏有至少一个 `scheduled_unlock_at IS NOT NULL` 的成就（即做过 Refresh）
- 游戏 Status 不是 Completed

**StartQueueAsync（Dashboard Start 按钮）：**
```
当前 Playing 游戏数量 = n
需要启动数量 = min(2 - n, 队列中 status=Idle 的游戏数)
按 position 升序，依次调用 GameIdleService.StartAsync(appId)
  → game.SteamPlaytimeAtRefresh = Steam API 当前值（重新锚定）
  → game.SessionStartedAt = now
  → game.Status = Playing
  → 重新计算 ScheduledUnlockAt（以 SessionStartedAt 为新锚点，保留原始相对偏移）
     防止 Refresh 与 Start 时间差过大导致大量成就已过期
```

**AdvanceQueueAsync（自动推进，Playing → Scheduled 时触发）：**
```
当前 Playing 数量 < 2 且队列中仍有 Idle 游戏
  → 取 position 最小的 Idle 游戏，调用 GameIdleService.StartAsync
```

**游戏完成时（Scheduled → Completed）：**
```
从 game_queue 中删除该游戏
触发 AdvanceQueueAsync（填补槽位）
```

**拖拽排序（ReorderAsync）：**
将目标游戏移动到 newPosition，其余游戏 position 顺序重排（1-based 连续整数）。
仅允许对 Status = Idle 的队列游戏重排；Playing 游戏锁定位置不可拖拽。

### 5.9 AchievementUnlockNotifier

**职责：** 单例事件总线，将后台解锁事件传播到 Blazor UI 层。

```csharp
public class AchievementUnlockNotifier {
    public event Action<UnlockedAchievementInfo>? AchievementUnlocked;
    public void NotifyUnlocked(UnlockedAchievementInfo info) => AchievementUnlocked?.Invoke(info);
}

public record UnlockedAchievementInfo(string GameName, string AchievementName, string? IconUrl);
```

`MainLayout.razor` 订阅此事件，在 AppBar 下方弹出成就解锁 Toast（含图标 + 游戏名 + 成就名）。

### 5.10 SyncBackgroundService

**职责：** 定时或手动触发全局同步。

**触发方式：**
- 按 `steam_config.sync_cron` 定时触发（**仅在配置了 Cron 表达式时生效**，无默认值）
- Settings 页"Sync Now"按钮手动触发（通过 `SemaphoreSlim` 唤醒）
- 若未配置 Cron，服务持续阻塞等待手动触发，不会按任何默认计划自动执行

**同步流程：**
```
Step 1: SteamWebApiClient.GetOwnedGames(steamId, language)
  → 刷新游戏库（新增游戏 / 更新 TotalPlayMinutes + 本地化名称）

Step 2: SteamCommunityClient.GetCardDropsRemainingAsync(steamId)
  → 抓取 Steam 社区徽章页面，更新所有游戏的 DropsRemaining

Step 3: 遍历所有游戏，逐一调用 AchievementMetadataSyncService.SyncMetadataAsync
  → 仅同步成就元数据（schema、GlobalPercent、用户当前已解锁状态）
  → 不重算 ScheduledUnlockAt，不修改队列，不触发挂机
  → 实时更新进度条（0→100%）和状态文本
```

### 5.11 SteamCommunityClient

**职责：** 通过 HTTP 抓取 Steam 社区徽章页面，获取各游戏剩余卡牌掉落数。

**实现：**
```
GET https://steamcommunity.com/profiles/{steamId64}/badges/?p={page}
```
- 逐页抓取（最多 20 页），用正则提取 `data-appid` 和"X card drop remaining"
- 返回 `Dictionary<int appId, int dropsRemaining>`

此 HTTP 请求依赖用户的 Steam 会话 Cookie（通过 `SteamClientWrapper` 获取），无需额外认证。

---

## 6. 启动恢复流程

```
应用启动
  │
  ├─ 1. 验证 SESSION_ENCRYPTION_KEY 已设置，否则启动失败
  ├─ 2. EF Core MigrateAsync() 自动建表/升级
  ├─ 3. TryRestoreSessionAsync() — 从 steam_config.session_token 恢复 Steam 登录
  │       └─ 失败 → 标记 NotLoggedIn，等待用户在 Settings 登录
  ├─ 4. 查询 game WHERE status = 'Playing'
  │       └─ 对每个 Playing 游戏执行"Start 恢复"逻辑：
  │            a. 调用 Steam API 读取当前游戏时长
  │               → game.SteamPlaytimeAtRefresh = steam_current_minutes（重新锚定）
  │            b. game.SessionStartedAt = serverStartTime
  │            c. 成就 scheduled_unlock_at 重算：
  │               以 serverStartTime 为新锚点，保留参考玩家原始相对偏移
  │               （跳过已解锁成就）
  │            d. 恢复 GameIdleService（重发 CMsgClientGamesPlayed）
  │       → 目的：避免重启后瞬间解锁大量成就；已累计时长通过 steam 重读自动恢复
  ├─ 5. 启动 UnlockSchedulerService（全局分钟级扫描）
  ├─ 6. GameQueueService.AdvanceQueueAsync()
  │       → 重启后若 Playing 游戏数 < 2，自动从队列填补（启动下一个 Idle 游戏）
  └─ 7. Web UI 正常提供服务，SyncBackgroundService 等待 cron 或手动触发
```

---

## 7. Web UI 设计

访问地址：`http://nas-ip:8080`，基于 Blazor Server（`@rendermode InteractiveServer`），MudBlazor 深色主题。

**访问认证：** 环境变量 `UI_ACCESS_PASSWORD` 配置访问密码；验证通过后写 Cookie，有效期 30 天。

**全局样式：** `html, body { color: rgba(255,255,255,0.87); }` 确保所有文字白色。MudText 的 `Color.Secondary` 不可用（映射到深色背景色），一律改用 `Style="color:rgba(255,255,255,0.6)"`。

### 7.1 AppBar（顶部导航栏）

每个页面公共顶部栏，右侧包含两个全局选择器：

- **语言选择器**：`English` / `简体中文`，保存到 `steam_config.language`；下次 Sync 时按此语言拉取本地化名称
- **时区选择器**：19 个常用时区（IANA ID + 中文标签），保存到 `steam_config.display_timezone`；应用于全局时间戳显示

`MainLayout.razor` 通过 `IServiceScopeFactory` 创建独立 DB scope，避免与页面组件共用同一 `DbContext` 实例造成并发冲突。

成就解锁 Toast 也由 `MainLayout.razor` 处理，订阅 `AchievementUnlockNotifier` 事件。

### 7.2 Dashboard（首页）

**全局统计面板（两个卡片并排）：**

| 面板 | 内容 |
|------|------|
| Remaining Idle Time | 所有游戏剩余挂机时长之和（小时）+ 全局进度条 |
| Remaining Achievements | 所有游戏剩余成就数之和 + 全局进度条 |

**队列控制区：**
- **Start 按钮**：调用 `GameQueueService.StartQueueAsync()`，从队列填满 2 个并发槽；无 Idle 队列游戏时禁用
- **队列列表**（按 position 排序）：支持拖拽排序（MudBlazor MudDropContainer）；Playing 状态游戏锁定不可拖拽；每行显示游戏名 + 状态 Chip + 移除按钮

**当前运行游戏（Currently Playing）：**
- 若有游戏 `Status == Playing`，显示（最多 2 个并排）：
  - 游戏封面（Steam CDN header.jpg）
  - 剩余挂机时间 + 进度条
  - 成就进度（X/Y unlocked）+ 进度条
  - **下一个成就**：图标 + 名称 + 距 `scheduled_unlock_at` 剩余时间
- 实时更新：订阅 `IGameIdleService.ProgressUpdated` 事件，每分钟触发 `ComputeStats()` 并刷新 UI
- 定时刷新：`PeriodicTimer`（1 min）重新从 DB 拉取数据，使用 `IServiceScopeFactory` 创建独立 scope

**DbContext 并发注意：** Dashboard 和 MainLayout 都在 `OnInitializedAsync` 中访问 DB，必须各自使用独立 scope（通过 `IServiceScopeFactory`），不能共用同一 scoped `AppDbContext`。

### 7.3 Games（游戏库）— 卡片网格

**顶部工具栏：**
- **模糊搜索框**：实时过滤游戏名称（`NameI18n ?? Name`）
- **只显示有成就**开关：过滤 `Achievements.Count > 0` 的游戏
- **只显示未全成就**开关：过滤成就未 100% 解锁的游戏
- **Card drops remaining 开关**：过滤 `DropsRemaining > 0` 的游戏
- **Sync Library 按钮**：触发全量同步（库 + 卡牌掉落），同步期间禁用按钮

**游戏卡片操作按钮：** 详情 / Refresh（有参考玩家时可用，触发后自动入队）/ Stop（Playing 游戏）。Refresh 成功后显示"已加入队列"Snackbar；若无参考玩家则 Refresh 按钮提示需先在详情页配置参考玩家。

**卡片布局（xs=12 sm=6 md=4 lg=3）：**
- **封面图**：Steam CDN `https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg`
- **游戏名称**：显示 `NameI18n ?? Name`；全成就完成时显示绿色（`#4caf50`）
- 右上角状态 Chip（`Idle / Playing / Scheduled / Completed`）
- **游戏时长进度条**：参考时长 = `TargetMinutes`（来自 ReferencePlayMinutes / MCT，若为 null 则不显示进度条）；Steam 已记录时长 < 目标：绿色（已玩）+ 暗色（剩余）；已达目标：全绿
- **成就进度条**（有成就数据时显示）：绿色（已解锁）+ 暗色（未解锁）
- **操作按钮**：详情（仅在 `Achievements.Count > 0` 时显示）、开始 / 停止

### 7.4 GameDetail（游戏详情）— 路由 `/games/{Id}`

- **Refresh 按钮**：触发 `AchievementDataService.RefreshAsync`，执行完整流程（拉取元数据 + 参考玩家数据验证 + 计算 scheduled_unlock_at + 启动挂机）；若参考玩家数据有 ≥5 个成就同时解锁且未开 override，显示错误说明
- **自动加载**：进入页面时，若 `Achievements.Count == 0`，自动触发一次 Refresh
- **参考玩家配置**：页面内可填写 / 修改 SteamHunters 玩家 URL，含"忽略成就数量限制"勾选框（`override_burst_check`）
- 页头信息：游戏封面、名称、AppID、已挂时长 / 目标、成就进度（X/Y）、参考完成时长（SteamHunters 中位数）、最后同步时间、游戏状态

**成就表格（按 `ScheduledUnlockAt` 升序，null 排末尾）：**

| 列 | 说明 |
|----|------|
| 图标 | 已解锁用 `IconUrl`，锁定用 `IconGrayUrl` |
| 名称 | `DisplayNameI18n ?? DisplayName`（主）+ 描述（`DescriptionI18n ?? Description`，灰色小字） |
| Scheduled At | 计划解锁时刻（`ScheduledUnlockAt`，显示本地时区）；null 显示 `—` |
| Global % | 全球解锁率 |
| 状态 | Unlocked（绿）/ Scheduled（蓝）/ Locked（灰）Chip |
| Time | 已解锁：显示实际解锁时间戳；未解锁且计划时刻已过：`Pending unlock`；未解锁未到期：`in Xh Xm`；无计划：`—` |

### 7.5 Settings（设置）

左列：
- **Steam 账号**：登录 / 重新登录（用户名 + 密码 + 2FA）
- **Steam Web API Key**：同步库存和拉取成就数据所需

右列：
- **Background Sync 配置：**
  - 5-field Cron 表达式（可选，不填则仅支持手动触发），带实时语法校验和下次执行时间预览
  - **Sync Now** 按钮：立即触发完整同步（库 + 卡牌掉落 + 所有游戏成就）
  - 同步期间显示进度条（0→100%）和当前游戏名称

> **注：** 语言和时区选择器已移至 AppBar，Settings 页不再包含这两项。

---

## 8. Docker 部署

### Dockerfile（多阶段构建）

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SteamManager.Web -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SteamManager.Web.dll"]
```

### 环境变量

`.env`（不提交 Git）：
```env
SESSION_ENCRYPTION_KEY=your_32_chars_minimum_key_here
UI_ACCESS_PASSWORD=your_ui_password
DB_HOST=192.168.x.x
DB_PORT=3306
DB_NAME=steam_manager
DB_USER=steam
DB_PASSWORD=steam_pass
```

`docker-compose.yml`：
```yaml
services:
  steam-manager:
    image: chaosmin/steam-manager:latest
    ports:
      - "8080:8080"
    restart: unless-stopped
    env_file:
      - .env
    environment:
      - TZ=Asia/Shanghai
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
```

MySQL 独立部署，不包含在 compose 中。连接串通过 `DB_*` 环境变量在启动时组装。

---

## 9. 技术约束与注意事项

**安全：**
1. `SESSION_ENCRYPTION_KEY` 必填，未设置启动失败；修改此 Key 会导致已存储 session_token 无法解密，需清空后重新登录
2. Steam 密码仅在登录期间临时存储，登录成功后立即清除
3. `.env` 加入 `.gitignore`，不提交敏感配置

**Steam Web API Key 说明：**
- SteamKit2 登录（模拟 Steam 客户端协议）与 Steam Web API 是两套独立系统
- 同步库存（`GetOwnedGames`）、拉取成就数据（`GetSchemaForGame` 等）均需 Web API Key
- Web API Key 申请：https://steamcommunity.com/dev/apikey

**EF Core：**
- 启动时自动 `MigrateAsync()`
- Blazor Server 的 scoped `DbContext` 在同一电路（circuit）内会缓存 tracked entities；所有只读查询必须使用 `AsNoTracking()` 以获取最新数据
- `AppDbContextFactory` 实现 `IDesignTimeDbContextFactory<AppDbContext>`，读取 `.env` 文件，使 `dotnet-ef` 工具可在启动守卫之外独立运行
- Layout 组件和后台 Timer 必须通过 `IServiceScopeFactory` 创建独立 DB scope，避免并发 DbContext 操作冲突

**SteamHunters API：**
- 使用 `GET https://steamhunters.com/api/apps/{appId}`，返回 `medianCompletionTime`（分钟）和 `playersPerfectedCount`
- 不可用时自动降级为按全球解锁率估算

**卡牌掉落抓取：**
- `SteamCommunityClient` 依赖用户登录后的 Steam 会话 Cookie，若 session 过期则抓取失败；失败时同步流程仅跳过卡牌更新，不影响主流程

**其他：**
- Steam ToS：此工具修改成就数据，使用者需自行承担账号风险
- 非服务端验证成就（server-side）无法通过 ClientStoreUserStats2 解锁，程序自动跳过
