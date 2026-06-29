# SteamManager 设计文档

**日期：** 2026-06-28（最后更新：v0.2.2）
**状态：** 已批准
**项目：** SteamManager — 自动挂 Steam 游戏时长 + 智能成就解锁服务

---

## 1. 需求概述

实现一个可部署在 NAS 上的服务，功能：
1. **自动挂机**：模拟正在游玩指定游戏，自动累积 Steam 游戏时长
2. **智能成就解锁**：参考 SteamHunters 的全成就玩家中位完成时长，按比例分配各成就的解锁时间点，行为模式自然
3. **断点续传**：停止服务后，下次恢复时从中断处继续，不重置进度；已超期未解锁的成就在启动时依次补解锁
4. **Web UI**：通过浏览器操作，无需命令行；支持简体中文 / 英语双语展示
5. **Docker 部署**：打包为 Docker 镜像，部署在 NAS（群晖/QNAP 等）上
6. **Steam 集换卡牌掉落追踪**：通过 Steam 社区徽章页面抓取各游戏剩余卡牌掉落数
7. **单游戏限制**：同时只能运行一个游戏，启动新游戏自动停止当前游戏

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
  id                     INT          PRIMARY KEY AUTO_INCREMENT,
  app_id                 INT          NOT NULL UNIQUE,
  name                   VARCHAR(255) NOT NULL,           -- 英语名（永久保留）
  name_i18n              VARCHAR(255),                    -- 本地化名（如简体中文），UI 优先显示
  target_hours           DECIMAL(5,2) NOT NULL DEFAULT 10,
  status                 ENUM('Idle','Running','Completed') NOT NULL DEFAULT 'Idle',
  total_play_minutes     INT          NOT NULL DEFAULT 0, -- 累计挂机分钟数
  last_session_start     DATETIME,
  reference_play_minutes INT,                             -- SteamHunters 中位完成时长（分钟）
  achievements_cached_at DATETIME,                        -- 成就数据最后拉取时间
  drops_remaining        INT          NOT NULL DEFAULT 0, -- 剩余卡牌掉落数
  created_at             DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at             DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
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
  unlock_offset_minutes INT          NOT NULL DEFAULT 0, -- 距游戏开始的触发分钟数
  is_unlocked           TINYINT(1)   NOT NULL DEFAULT 0,
  unlocked_at           DATETIME,
  created_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  FOREIGN KEY (game_id) REFERENCES game(id) ON DELETE CASCADE,
  UNIQUE KEY uq_game_achievement (game_id, api_name)
);
```

### 4.2 EF Core 实体对应

| 表名 | EF Core 实体 | 主要字段 |
|------|-------------|---------|
| `steam_config` | `SteamConfig` | `Username`, `PasswordEnc`, `WebApiKey`, `SessionToken`, `SyncCron`, `Language`, `DisplayTimezone` |
| `game` | `Game` | `AppId`, `Name`, `NameI18n`, `TargetHours`, `Status`, `TotalPlayMinutes`, `ReferencePlayMinutes`, `AchievementsCachedAt`, `DropsRemaining` |
| `achievement` | `Achievement` | `GameId`, `AppId`, `ApiName`, `DisplayName`, `DisplayNameI18n`, `Description`, `DescriptionI18n`, `GlobalPercent`, `IconUrl`, `IconGrayUrl`, `UnlockOffsetMinutes`, `IsUnlocked`, `UnlockedAt` |

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

**职责：** 通过 SteamKit2 向 Steam 服务器发送"正在游玩"状态，每分钟 `game.TotalPlayMinutes++` 并写库。

- 达到 `TargetHours × 60` 分钟后自动停止，`Status = Completed`
- 手动停止时立即持久化进度
- 触发 `ProgressUpdated` 事件（`event Action<int appId, int minutes>`）供 Dashboard 实时更新

### 5.4 AchievementDataService

**职责：** 拉取成就数据并 Upsert 到 `achievement` 表。  
**接口：** `LoadAchievementsAsync(int gameId, int appId, CancellationToken ct = default)`

**数据流：**
```
1. Steam Web API: GetSchemaForGame(appid, language)
   → 成就列表、DisplayName（含本地化）、Description（含本地化）、IconUrl、IconGrayUrl

2. Steam Web API: GetGlobalAchievementPercentagesForApp(appid)
   → GlobalPercent

3. SteamHunters API: GET /api/apps/{appId}
   → medianCompletionTime（中位全成就完成时长，分钟）
   → 有数据：CalculateFallbackScaled(schema, medianMinutes) 按比例缩放偏移量
   → 无数据：CalculateFallback(schema, intervalPerPercent) 按全球解锁率估算

4. UPSERT achievement 表（按 game_id + api_name 唯一键）
   → 多语言：language=="english" 写 DisplayName/Description + 清空 I18n 列
              language!="english" 写 DisplayNameI18n/DescriptionI18n + 保留英语列

5. 同步当前用户已解锁成就状态（GetPlayerAchievements）

6. 更新 game.ReferencePlayMinutes = medianMinutes（或最大 offset）
   更新 game.AchievementsCachedAt = NOW()
```

**降级：** SteamHunters 不可用时，按全球解锁率从高到低排序，均匀估算间隔。

### 5.5 AchievementIntervalCalculator（纯静态）

| 方法 | 说明 |
|------|------|
| `CalculateFallback(schema, intervalPerPercent)` | 按全球解锁率差值估算偏移量（无外部数据时用） |
| `CalculateFallbackScaled(schema, totalMinutes, intervalPerPercent)` | 同上，但将最终偏移量线性缩放到 `totalMinutes`（SteamHunters 有 medianCompletionTime 时用） |
| `Calculate(playerAchievements, playerPlaytimesMinutes)` | 从多个玩家的真实解锁时间戳计算中位偏移量（保留，当前未启用） |

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

**职责：** 在游戏运行时按计划自动解锁成就，解锁后触发 `AchievementUnlockNotifier` 通知 UI。

**两阶段执行：**

```
Phase 1 — 补解锁（Catch-up）
  查询所有 is_unlocked=0 AND unlock_offset_minutes <= total_play_minutes
  按 offset 升序，逐条解锁，每条之间随机等待 1-100 秒

Phase 2 — 轮询（Polling）
  每 ~30 秒（±10% 抖动）检查一次是否有新到期成就
  发现后随机等待 1-100 秒再实际解锁
```

**断点续传：** 启动时直接进入 Phase 1 补解锁，自动处理历史欠账。

### 5.8 AchievementUnlockNotifier

**职责：** 单例事件总线，将后台解锁事件传播到 Blazor UI 层。

```csharp
public class AchievementUnlockNotifier {
    public event Action<UnlockedAchievementInfo>? AchievementUnlocked;
    public void NotifyUnlocked(UnlockedAchievementInfo info) => AchievementUnlocked?.Invoke(info);
}

public record UnlockedAchievementInfo(string GameName, string AchievementName, string? IconUrl);
```

`MainLayout.razor` 订阅此事件，在 AppBar 下方弹出成就解锁 Toast（含图标 + 游戏名 + 成就名）。

### 5.9 SyncBackgroundService

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

Step 3: 遍历所有游戏，逐一调用 AchievementDataService.LoadAchievementsAsync
  → 实时更新进度条（0→100%）和状态文本
```

### 5.10 SteamCommunityClient

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
  ├─ 4. 查询 game WHERE status = 'Running'
  │       └─ 对每个 Running 游戏：恢复 GameIdleService + UnlockSchedulerService
  │          UnlockSchedulerService 自动执行 Phase 1 补解锁，处理停机期间的欠账
  └─ 5. Web UI 正常提供服务，SyncBackgroundService 等待 cron 或手动触发
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

**当前运行游戏（Currently Playing）：**
- 若有游戏 `Status == Running`，显示：
  - 游戏封面（Steam CDN header.jpg）
  - 剩余挂机时间 + 进度条
  - 成就进度（X/Y unlocked）+ 进度条
  - **下一个成就**：图标 + 名称 + 距解锁剩余时间（`FormatMinutes`）
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

**单游戏限制：** 点击 Play 时，若有其他游戏 `Status == Running`，先调用 `StopAsync` 停止，再启动新游戏，并显示 Snackbar 通知。

**卡片布局（xs=12 sm=6 md=4 lg=3）：**
- **封面图**：Steam CDN `https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg`
- **游戏名称**：显示 `NameI18n ?? Name`；全成就完成时显示绿色（`#4caf50`）
- 右上角状态 Chip（`Idle / Running / Completed`）
- **游戏时长进度条**：参考时长 = `ReferencePlayMinutes`（有成就数据）或 `TargetHours × 60`；已玩 < 参考：绿色（已玩）+ 暗色（剩余）；已玩 ≥ 参考：绿色（参考时长）+ 黄色（超出）
- **成就进度条**（有成就数据时显示）：绿色（已解锁）+ 暗色（未解锁）
- **操作按钮**：详情（仅在 `Achievements.Count > 0` 时显示）、开始 / 停止

### 7.4 GameDetail（游戏详情）— 路由 `/games/{Id}`

- **Refresh 按钮**：手动触发 `LoadAchievementsAsync`，拉取成就数据 + 同步本账号解锁状态
- **自动加载**：进入页面时，若 `Achievements.Count == 0`，自动触发一次 Refresh
- 页头信息：游戏封面、名称、AppID、挂机时长 / 目标、成就进度（X/Y）、参考完成时长（来自 SteamHunters 中位数）、最后同步时间

**成就表格（按 `UnlockOffsetMinutes` 升序）：**

| 列 | 说明 |
|----|------|
| 图标 | 已解锁用 `IconUrl`，锁定用 `IconGrayUrl` |
| 名称 | `DisplayNameI18n ?? DisplayName`（主）+ 描述（`DescriptionI18n ?? Description`，灰色小字） |
| Unlock At | 理论解锁时刻，格式 `X.XXh`（两位小数） |
| Global % | 全球解锁率 |
| 状态 | Unlocked（绿）/ Locked（灰）Chip |
| Time | 已解锁：显示实际解锁时间戳；未解锁且超期：`Pending unlock`；未解锁未超期：`in X.Xh` |

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
