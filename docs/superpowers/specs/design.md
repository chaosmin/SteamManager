# SteamManager 设计文档

**日期：** 2026-06-26  
**状态：** 已批准  
**项目：** SteamManager — 自动挂 Steam 游戏时长 + 智能成就解锁服务

---

## 1. 需求概述

实现一个可部署在 NAS 上的服务，功能：
1. **自动挂机**：模拟正在游玩指定游戏，自动累积 Steam 游戏时长
2. **智能成就解锁**：参考其他全成就玩家的解锁时间间隔，按相似节奏自动解锁成就，使行为模式自然
3. **断点续传**：停止服务后，下次恢复时从中断处继续，不重置进度
4. **Web UI**：通过浏览器操作，无需命令行
5. **Docker 部署**：打包为 Docker 镜像，部署在 NAS（群晖/QNAP 等）上

---

## 2. 技术栈

| 层 | 技术 |
|----|------|
| 框架 | ASP.NET Core 8 + Blazor Server |
| Steam 协议 | SteamKit2（直接通信 Steam 网络，无需 Steam 客户端） |
| 实时 UI | SignalR（Blazor Server 内置） |
| 数据存储 | MySQL 8 + Entity Framework Core（Pomelo.EntityFrameworkCore.MySql） |
| 日志 | Serilog（输出到控制台 + 滚动文件） |
| 容器化 | Docker（多阶段构建）+ docker-compose |
| 运行时 | .NET 8，镜像约 200MB |

**参考实现：**
- 挂机机制：[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)（SteamKit2 挂机方式）
- 成就解锁：[ASFAchievementManager](https://github.com/CatPoweredPlugins/ASFAchievementManager)（通过 `EMsg.ClientStoreUserStats2` 解锁，无需 Steam 客户端）
- 成就数据参考：[SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager)（仅作成就 Schema 参考）

---

## 3. 项目结构

```
SteamManager/
├── src/
│   ├── SteamManager.Web/                     # ASP.NET Core + Blazor Server（启动入口）
│   │   ├── Pages/
│   │   │   ├── Dashboard.razor               # 首页：状态总览 + 实时进度
│   │   │   ├── Games.razor                   # 游戏管理：添加/配置/启停
│   │   │   └── Settings.razor                # 账号设置 + API Key + 缓存管理
│   │   ├── Components/
│   │   │   ├── GameCard.razor                # 单游戏状态卡片
│   │   │   └── AchievementList.razor         # 成就列表组件
│   │   ├── Program.cs
│   │   └── wwwroot/
│   ├── SteamManager.Core/                    # 核心业务逻辑
│   │   ├── Services/
│   │   │   ├── SteamSessionService.cs        # Steam 登录 + Session 持久化
│   │   │   ├── GameIdleService.cs            # 挂时长逻辑
│   │   │   ├── AchievementDataService.cs     # 成就数据获取 + 缓存
│   │   │   └── UnlockSchedulerService.cs     # 成就解锁调度 + 断点续传
│   │   └── Models/
│   │       ├── GameConfig.cs                 # 游戏配置模型
│   │       ├── GameProgress.cs               # 游戏进度状态模型
│   │       ├── AchievementScheduleItem.cs    # 成就调度条目模型
│   │       ├── AchievementCache.cs           # 成就数据缓存模型
│   │       └── SteamConfig.cs                # Steam 账号配置模型
│   └── SteamManager.Infrastructure/          # 外部依赖封装
│       ├── Persistence/
│       │   ├── AppDbContext.cs               # EF Core DbContext
│       │   ├── Migrations/                   # EF Core 迁移文件
│       │   └── Repositories/                 # 仓储实现
│       ├── Steam/
│       │   ├── SteamClientWrapper.cs         # SteamKit2 封装（连接/回调管理）
│       │   └── AchievementHandler.cs         # 成就读写（参考 ASFAchievementManager）
│       └── Http/
│           ├── SteamWebApiClient.cs          # Steam Web API 调用
│           └── SteamHuntersClient.cs         # steamhunters.com API 调用
├── docs/
│   └── superpowers/specs/
├── Dockerfile
├── docker-compose.yml
└── README.md
```

---

## 4. 数据库设计

ORM：Entity Framework Core 8，驱动：`Pomelo.EntityFrameworkCore.MySql`。

### 4.1 表结构

**所有 `DATETIME` 列均存储 UTC 时间。** 显示时在应用层按用户选择的时区转换。

```sql
-- Steam 账号配置（单行）
CREATE TABLE steam_config (
  id                 INT          PRIMARY KEY AUTO_INCREMENT,
  username           VARCHAR(64)  NOT NULL,
  password_enc       TEXT         NOT NULL,   -- AES-256 加密
  web_api_key        VARCHAR(64),
  session_token      TEXT,                    -- AES-256 加密的 RefreshToken
  session_updated_at DATETIME,                -- UTC
  display_timezone   VARCHAR(64)  NOT NULL DEFAULT 'UTC',  -- IANA 时区，如 Asia/Shanghai
  created_at         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,  -- UTC
  updated_at         DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP  -- UTC
);

-- 游戏配置
CREATE TABLE game_config (
  id                   INT          PRIMARY KEY AUTO_INCREMENT,
  app_id               INT          NOT NULL UNIQUE,
  name                 VARCHAR(255) NOT NULL,
  target_hours         DECIMAL(5,2) NOT NULL DEFAULT 0,
  enable_achievements  TINYINT(1)   NOT NULL DEFAULT 0,
  status               ENUM('IDLE','RUNNING','COMPLETED') NOT NULL DEFAULT 'IDLE',
  created_at           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at           DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- 游戏挂机进度（断点续传）
CREATE TABLE game_progress (
  id                   INT      PRIMARY KEY AUTO_INCREMENT,
  app_id               INT      NOT NULL UNIQUE,
  accumulated_minutes  INT      NOT NULL DEFAULT 0,
  last_session_start   DATETIME,
  created_at           DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at           DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  FOREIGN KEY (app_id) REFERENCES game_config(app_id)
);

-- 成就解锁调度（每游戏每成就一行）
CREATE TABLE achievement_schedule (
  id              INT          PRIMARY KEY AUTO_INCREMENT,
  app_id          INT          NOT NULL,
  achievement_id  VARCHAR(128) NOT NULL,
  offset_minutes  INT          NOT NULL,  -- 相对累计挂机时长的触发分钟数
  done            TINYINT(1)   NOT NULL DEFAULT 0,
  unlocked_at     DATETIME,
  created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY uq_app_achievement (app_id, achievement_id),
  INDEX idx_schedule_lookup (app_id, done, offset_minutes),  -- 覆盖续传查询模式
  FOREIGN KEY (app_id) REFERENCES game_config(app_id)
);

-- 成就数据缓存（来自 Steam Web API + steamhunters）
CREATE TABLE achievement_cache (
  id          INT      PRIMARY KEY AUTO_INCREMENT,
  app_id      INT      NOT NULL UNIQUE,
  data        JSON     NOT NULL,   -- 完整成就数据（包含间隔计算结果）
  fetched_at  DATETIME NOT NULL,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
```

### 4.2 EF Core 实体对应

| 表名 | EF Core 实体 | 主要字段 |
|------|-------------|---------|
| `steam_config` | `SteamConfig` | `Username`, `PasswordEnc`, `WebApiKey`, `SessionToken`, `DisplayTimezone` |
| `game_config` | `GameConfig` | `AppId`, `Name`, `TargetHours`, `EnableAchievements`, `Status` |
| `game_progress` | `GameProgress` | `AppId`, `AccumulatedMinutes`, `LastSessionStart` |
| `achievement_schedule` | `AchievementScheduleItem` | `AppId`, `AchievementId`, `OffsetMinutes`, `Done`, `UnlockedAt` |
| `achievement_cache` | `AchievementCache` | `AppId`, `Data`, `FetchedAt` |

---

## 5. 核心服务设计

### 5.1 SteamSessionService

**职责：** 管理 Steam 账号登录和 Session 生命周期。

**首次登录流程：**
1. 用户在 Settings 页面输入用户名 + 密码，点击登录
2. Steam 发送手机 2FA 验证码到用户手机
3. Web UI 弹出输入框，用户手动输入验证码
4. 登录成功后，将 `RefreshToken` 用 AES-256 加密，写入 `steam_config.session_token`

**后续启动：**
- 从 `steam_config` 表读取加密 `session_token`，解密后恢复 Session，无需人工干预
- RefreshToken 有效期通常数月
- Token 过期时，Web UI Dashboard 顶部展示重新登录提示

**Steam 密码存储策略：**
- 登录成功获取 `RefreshToken` 后，立即将 `steam_config.password_enc` 清空（置 NULL）
- 只有用户主动触发"重新登录"时才临时写入密码，使用后立即清除
- 长期仅依赖 `session_token` 恢复 Session，减少密码泄露面

**加密密钥策略：**
- `SESSION_ENCRYPTION_KEY` 为**必填项**，未设置时应用启动失败并输出明确错误，不提供 fallback
- 原因：Docker 容器 ID 每次重建都会变，任何"自动派生"方案都会导致容器重建后 session 无法解密

### 5.2 SteamClientWrapper — 断线重连

**断线重连策略（指数退避）：**
```
断线事件触发
  │
  ├─ 通知所有运行中的 GameIdleService 暂停计时
  ├─ 等待 delay（初始 5s，每次 ×2，上限 5min）
  ├─ 尝试重连
  │   ├─ 成功 → 恢复所有任务，重置 delay
  │   └─ 失败 → 继续退避循环
  └─ 重连期间 Web UI 展示"Steam 连接中..."状态
```

### 5.3 GameIdleService

**职责：** 通过 SteamKit2 向 Steam 服务器发送"正在游玩"状态，积累游戏时长。

**实现要点：**
- 调用 SteamKit2 `SteamFriends` 设置当前游戏 AppID
- 每分钟将 `game_progress.accumulated_minutes` +1 并写库
- 达到 `game_config.target_hours × 60` 后自动停止，更新 `game_config.status = 'COMPLETED'`
- 手动停止时立即持久化当前进度
- Steam 断线时暂停计时，重连后自动恢复

### 5.4 AchievementDataService（混合缓存）

**职责：** 获取并缓存全成就玩家的解锁间隔数据。

**数据流：**
```
1. 查询 achievement_cache WHERE app_id = ? AND fetched_at > NOW() - INTERVAL 7 DAY
   ├─ 命中 → 直接返回 data 字段
   └─ 未命中 → 执行拉取流程：

2. Steam Web API: GetSchemaForGame(appid)
   → 获取成就列表、名称、全球解锁率(%)

3. steamhunters.com API
   → 查找该游戏 100% 成就玩家 SteamID（取前 20 个）

4. Steam Web API: GetPlayerAchievements(steamid, appid) × 20
   → 获取每位玩家各成就的解锁时间戳(Unix)

5. 计算解锁间隔：
   - 按解锁时间戳排序成就顺序
   - 计算相邻成就解锁时间差的中位数（分钟）
   - 生成 [{achievementId, offsetMinutes}] 序列

6. UPSERT achievement_cache SET data = ?, fetched_at = NOW()
```

**Steam Web API 限速：** 20 个玩家数据需 20+ 次 API 调用，每次调用之间强制延迟 1s，遇到 HTTP 429 时等待 60s 后重试，最多重试 3 次。

**降级策略：** steamhunters.com 不可用时，按全球解锁率从高到低排序成就，用解锁率差值估算间隔（可通过应用配置调整比例系数）。

**成就调度初始化时机：**
- 用户在 Games 页面点击"启动"时触发
- 若 `achievement_schedule` 已有该游戏的记录（断点续传场景）则跳过生成
- 若 `AchievementDataService` 拉取失败：挂机任务正常启动，但不解锁成就，UI 展示警告；用户可在 Settings 手动触发重新拉取后再启用成就解锁

### 5.6 UnlockSchedulerService（断点续传）

**职责：** 按计划解锁成就，支持中断后从断点恢复。

**续传逻辑：**
```
查询 game_progress.accumulated_minutes = 55
查询下一个未完成成就：
  SELECT * FROM achievement_schedule
  WHERE app_id = ? AND done = 0
  ORDER BY offset_minutes ASC LIMIT 1
  → offset_minutes = 60

等待时间 = 60 - 55 = 5 分钟
启动定时器，5 分钟后执行解锁
解锁后：UPDATE achievement_schedule SET done=1, unlocked_at=NOW()
```

**成就解锁机制（参考 ASFAchievementManager）：**
1. 发送 `EMsg.ClientGetUserStats` → 获取游戏成就 Schema 和当前状态
2. 解析 Schema，找到目标成就对应的 stat 整数和 bit 位
3. `stat_value |= (1 << bitNum)` 设置对应 bit
4. 发送 `EMsg.ClientStoreUserStats2` → 提交到 Steam 服务器

**限制：** 带 ⚠️ 标记的服务端验证成就无法通过此方式解锁，程序跳过并在 UI 标注。

**随机抖动：** 每个成就等待时间加入 ±10% 随机偏差，使解锁节奏更自然。

---

## 6. 启动恢复流程

应用重启后需从 DB 重建运行状态：

```
应用启动
  │
  ├─ 1. 验证 SESSION_ENCRYPTION_KEY 已设置，否则启动失败
  ├─ 2. EF Core MigrateAsync() 自动建表/升级
  ├─ 3. 读取 steam_config，恢复 Steam Session
  │       └─ 失败 → 标记"未登录"，等待用户在 Settings 页面登录
  ├─ 4. 查询 game_config WHERE status = 'RUNNING'
  │       └─ 对每个 RUNNING 游戏：重建 GameIdleService + UnlockSchedulerService
  │           ├─ 从 game_progress.accumulated_minutes 恢复计时起点
  │           └─ 从 achievement_schedule WHERE done=0 恢复调度队列
  └─ 5. Web UI 正常提供服务
```

---

## 7. Web UI 设计

访问地址：`http://nas-ip:5000`，基于 Blazor Server，通过 SignalR 实时推送状态更新。

**访问认证：** UI 启用简单密码保护（单用户模式）。密码通过环境变量 `UI_ACCESS_PASSWORD` 配置；未设置时允许访问但首页展示安全警告。验证通过后写 Cookie，有效期 30 天。

### 6.1 Dashboard（首页）
- Steam 账号状态：头像、昵称、登录状态
- 各游戏状态卡片：封面、已挂时长 / 目标时长进度条、成就解锁进度（X/Y）
- 下一个成就倒计时
- Session 过期告警横幅

### 6.2 Games（游戏管理）
- 添加游戏：输入 AppID，自动拉取游戏名称和封面
- 每游戏配置项：目标时长（小时）、是否启用成就解锁
- 启动 / 停止按钮，状态：运行中 / 已完成 / 待机

### 6.3 Settings（设置）
- Steam 账号：用户名、密码（显示为 `****`）、登录/重新登录按钮
- Steam Web API Key 填写
- **时区选择**：下拉框展示全量 IANA 时区列表，默认值读取容器 `TZ` 环境变量（如 `Asia/Shanghai`），未设置则默认 `UTC`。选定时区保存至 `steam_config.display_timezone`，UI 所有时间展示均按此时区转换，DB 始终存储 UTC
- 成就缓存管理：各游戏缓存状态（`fetched_at` 按所选时区显示）、手动刷新按钮

---

## 7. Docker 部署

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

### docker-compose.yml

MySQL 独立部署，不包含在 compose 中。连接信息通过环境变量注入：

**敏感配置通过 `.env` 文件管理，加入 `.gitignore` 避免提交到代码仓库。**

`.env`（不提交）：
```env
SESSION_ENCRYPTION_KEY=your_32_chars_minimum_key_here
UI_ACCESS_PASSWORD=your_ui_password
DB_PASSWORD=steam_pass
```

`docker-compose.yml`：
```yaml
services:
  steam-manager:
    image: steam-manager:latest
    build: .
    ports:
      - "5000:5000"
    restart: unless-stopped
    env_file:
      - .env
    environment:
      - TZ=Asia/Shanghai                        # 容器时区，作为 UI 时区选择的初始默认值
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5000
      - ConnectionStrings__Default=Server=192.168.1.x;Port=3306;Database=steam_manager;User=steam;Password=${DB_PASSWORD};Convert Zero Datetime=True;AllowPublicKeyRetrieval=True;ConnectionTimeout=30;
```

**连接串说明：**
- `Convert Zero Datetime=True`：防止 MySQL `0000-00-00` 值引发异常
- 连接串不含 `timezone` 参数；EF Core 启动时通过 `SET time_zone = '+00:00'` 强制会话使用 UTC，确保读写一致

**应用启动时通过 EF Core Migrations 自动建表，无需手动执行 SQL。**

---

## 8. 应用配置（appsettings.json）

仅保留非业务配置（DB 连接串、应用行为参数），敏感值通过环境变量覆盖：

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=steam_manager;User=steam;Password=steam_pass;"
  },
  "AchievementData": {
    "CacheExpiryDays": 7,
    "MaxReferencePlayers": 20,
    "IntervalJitterPercent": 10,
    "FallbackIntervalPerPercentDiff": 2
  }
}
```

Steam 账号、API Key、Session Token 全部存储在 MySQL `steam_config` 表中（加密），不写入配置文件。

---

## 9. 技术约束与注意事项

**安全：**
1. **SESSION_ENCRYPTION_KEY 必填**：未设置时应用拒绝启动，输出明确错误；不提供任何自动派生 fallback
2. **UI_ACCESS_PASSWORD**：未设置时允许访问但首页展示安全警告，建议生产环境必须配置
3. **Steam 密码最短暂存**：登录成功后立即清除 `password_enc`，长期仅依赖 `session_token`
4. **敏感配置隔离**：`SESSION_ENCRYPTION_KEY`、`UI_ACCESS_PASSWORD`、DB 密码均通过 `.env` 文件管理，`.env` 加入 `.gitignore`
5. **HTTPS**：默认 HTTP，建议通过 NAS 反向代理（群晖 Application Portal / Nginx Proxy Manager）启用 HTTPS，避免 Steam 操作明文传输

**可靠性：**
6. **SteamKit2 断线重连**：指数退避（5s → 10s → ... → 5min），断线期间任务暂停，重连后自动恢复
7. **启动恢复**：应用重启后自动从 DB 恢复所有 `RUNNING` 状态的游戏任务
8. **成就数据拉取失败**：挂机继续，成就解锁跳过，UI 展示警告，支持手动重试

**数据：**
9. **时区一致性**：DB 存 UTC，EF Core 启动强制 `SET time_zone = '+00:00'`；UI 按 `steam_config.display_timezone` 转换展示
10. **时区初始化**：首次启动读取容器 `TZ` 环境变量写入 `display_timezone`；已存在则不覆盖
11. **EF Core Migrations**：启动时自动执行 `Database.MigrateAsync()`，升级自动应用新迁移

**其他：**
12. **非服务端验证成就**：带 ⚠️ 标记的成就无法解锁，程序自动跳过并在 UI 标注
13. **Steam ToS**：此工具修改成就数据，使用者需自行承担账号风险
14. **steamhunters.com 依赖**：为第三方服务，不可用时自动降级为解锁率估算法
15. **Steam Web API 限速**：每次 API 调用间隔 ≥1s，429 响应等待 60s 后重试，最多 3 次
16. **容器迁移**：MySQL 独立部署，容器重建不影响数据；仅需更新连接串
