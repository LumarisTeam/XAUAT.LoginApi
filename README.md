# XAUAT.LoginApi

西安建筑科技大学统一认证（CAS/UAAP）与教务系统访问服务，Native AOT 极简 API。

**它是 [`xauat_login_flask`](../../PythonProjects/xauat_login_flask) 中 XAUAT 部分的 .NET 重写**，
目标是在协议层面与现有部署逐字兼容，以便把 `schedule.xauat.site` 平滑切过来。

## 它替代了什么

当前 `schedule.xauat.site` 上跑的是那套 Flask，而它正是整个 XAUAT 链路的登录真身：

```
Flutter / iOS / 管理端
        │
        ▼
XAUAT.EduApi  (POST /v1/login；日历订阅 GET /v1/course/Calendar 由它自己生成)
        │
        └──► https://schedule.xauat.site/auth/login      ← Flask 做 SSO
                  │
                  └──► http://authserver.xauat.edu.cn    (CAS 统一认证)
                            └──► https://swjw.xauat.edu.cn (教务系统)
```

EduApi 自己**不做 SSO 握手**：它只 POST JSON 给 Flask 拿 `{success, cookies}`。
本服务就是把 SSO 登录接过来自己做。

> **日历订阅已迁走**：ICS 生成（含 ICS 序列化、课表/考试取数）已搬到 XAUAT.EduApi 的
> `v1/course/Calendar`，因为那里的 `CourseService`/`ExamService` 本来就解析同一套教务数据。
> 本服务现在只负责登录与封禁。

## 快速开始

```bash
# 依赖 .NET SDK 10.0
dotnet build

# 本地运行（默认 http://localhost:5192）
dotnet run --project XAUAT.LoginApi.Web

# 测试
dotnet test
```

环境变量走 `.env`（由 DotNetEnv 从工作目录向上查找，真实环境变量优先）。
复制 `.env.example` 起步即可；**不配置 Redis 也能跑**，只是退化为无缓存、无限流的全量登录。

### 想在没有真实学号密码的情况下跑通全链路

```bash
TEST_ACCOUNT_ENABLED=true
TEST_ACCOUNT_USERNAME=frontend-test
TEST_ACCOUNT_PASSWORD=frontend-test-password
```

命中后 `/auth/login` 返回伪造 cookie，全程不碰上游。（日历那边的测试账号数据现在归
XAUAT.EduApi 的 `TestFixtures/`。）

## API

| 路由 | 方法 | 说明 |
| --- | --- | --- |
| `/auth/login` | POST | body `{username, password, school="xauat"}` → `{success, cookies}` |
| `/login/{username}/{password}` | GET | 遗留路由。**登录失败也返回 200**（见下方"有意差异"） |
| `/statistics/users`、`/user_count` | GET | `{"count": N}` |
| `/security/ban_status` | GET / POST | GET 查询封禁；POST `{school, username}` 解封 |
| `/security/ban_logs` | GET | 封禁日志 JSON（**新增**，替代 Flask 的 HTML 页） |
| `/Logs` | GET | 应用日志分页，`page`/`pageSize`/`level`/`search` |
| `/health` | GET | 纯文本 `ok` |

`/Logs` 默认开放；配置 `LOG_VIEW_TOKEN` 后需带 `X-Log-Token` 或 `Authorization: Bearer`。

## 与 Flask 的兼容性契约

### Redis 键：**刻意不加前缀**

与 PaymentAPI（`PaymentApi:`）、EduApi（`eduapi`）**相反**，本服务复用 Flask 的原键名，
且应当指向**同一个 Redis 实例**。这样灰度期间两个实现可以并行跑：缓存不冷、
封禁状态延续、切换时用户不需要重新登录。

| 键 | TTL | 值 |
| --- | --- | --- |
| `login-cookies-{用户名}-{sha256(密码)}` | 1200s | eduCookie 串 |
| `sso-cookies-{用户名}-{sha256(密码)}` | 604800s | SSO 票据 |
| `login-failed-{用户名}-{sha256(密码)}` | 1200s | 失败计数 |
| `current_semester` | 604800s | 学期 id（全局单键） |
| `security:ban:{学校}:{用户名}` | 86400s | 封禁 JSON（**键形状有变**，见下） |
| `security:rate_limit_events:{学校}:{用户名}` | 86400s | 限流事件 ZSET |
| `security:ban_logs` | — | 封禁日志 LIST |

`XAUAT.LoginApi.Tests/Compatibility/LoginCacheKeysTests.cs` 把这些键名逐字钉死——
改动它之前请先想清楚与 Flask 的互通怎么办。键名一旦加前缀，Flask 写的缓存就读不到，
而且**不会有任何报错**，只表现为登录变慢、活跃统计归零。

### 上游端点

| 用途 | 地址 |
| --- | --- |
| CAS 登录页 / 表单提交 | `http://authserver.xauat.edu.cn/authserver/login`（**明文 HTTP，上游如此**） |
| 当前学期 | `GET https://swjw.xauat.edu.cn/student/for-std/course-table` |
| 课程 id 列表 | `GET .../course-table/get-data?bizTypeId=2&semesterId=…&dataId=` |
| 课程明细 | `POST .../ws/schedule-table/datum`（body 里 `studentId` 是字面量字符串 `"null"`） |
| 考试安排 | `GET .../for-std/exam-arrange` |

## 与 Flask 的**有意差异**

迁移不是照抄。以下是刻意做出的每一处改变，都有理由：

| # | 差异 | 理由 |
| --- | --- | --- |
| 1 | 封禁键从 `(学校, 用户名, 密码哈希)` 收敛为 `(学校, 用户名)` | 原粒度下**换个错误密码就能绕过封禁**。代价：Flask 期间产生的旧封禁不会延续（读路径仍兼容旧键，见 `BanService.FindActiveBansAsync`） |
| 2 | ~~ICS 补上 `VTIMEZONE` …~~ **已随日历迁到 XAUAT.EduApi** | 那边的 `IcsCalendarWriter` 逐字继承了这份实现与它的 UID 约定 |
| 3 | 活跃用户数改为**请求时 SCAN 现算** | Flask 靠 Redis keyspace 通知维护计数器，但 `gunicorn -w 4` 下 4 个 worker 会重复扣减导致系统性偏低；而且它要 `CONFIG SET notify-keyspace-events`，会改到**整个共享 Redis 实例**的全局配置。`user:count:total` 键因此废弃 |
| 4 | `/admin/ban_logs` HTML 页 → `GET /security/ban_logs` JSON | 本服务是纯 API，不渲染页面 |
| 5 | 不实现 Flask 的 `/` 落地页 | 同上。**切流时 `schedule.xauat.site/` 会 404**——如果有人靠它拿 webcal 订阅链接，请先告知 |
| 6 | Redis 降级路径补上了 SSO 换票 | Flask 在 Redis 挂掉时不做换票，于是把教务系统不认的 `CASTGC` 当成功返回（调用方随后必然 401）。降级路径的目标就是"仍然能登录" |
| 7 | 请求体不是合法 JSON 时返回 **400** | Flask 会因 `get_json(force=True)` 抛异常而变成 500；但同一段代码里的 `"Invalid JSON body"` 分支显然才是作者意图 |
| 8 | ~~新增 HTTP 层并发限流~~ **已随日历迁到 XAUAT.EduApi** | 那条公开路径现在由 EduApi 的 `EduCrawler` 策略（同样是 8 并发 / 16 队列）兜底 |
| 9 | `security:ban_logs` 加 `LTRIM` 封顶 10000 条 | Flask 从不裁剪，该列表无限增长（读却只取前 201 条） |
| 10 | ~~考试页改用 `/for-std/exam-arrange/`（**带尾斜杠**）~~ **已随日历迁到 XAUAT.EduApi** | 那条知识（无斜杠会返回「学籍信息」页且 HTTP 200）保留在 EduApi `ExamService.ExamArrangePath` 的注释里 |
| 11 | `/login/{u}/{p}` 失败**仍返回 200** | 这不是差异，是**刻意保留**的 Flask 历史怪癖：老客户端可能按"200 + success 字段"判断，改成 401 会让它们把失败当成功 |

## 部署

镜像走 Native AOT，最终镜像是 `runtime-deps:10.0-noble-chiseled`（**无 shell、无 curl**，
排障只能用 `docker logs`）。

```bash
# 本机从源码构建并起容器（POSIX sh，dash 下也能跑）
./build.sh
AOT=false ./build.sh          # JIT 变体（AOT 出问题时的逃生口）

# 服务器上从 ghcr 拉预构建镜像
./deploy/build_from_ghcr.sh
./deploy/build_from_ghcr.sh ghcr.io/lijiajunply/xauat.loginapi:<sha>   # 指定版本 / 回滚
```

CI（`.github/workflows/deploy-production.yml`）只跑测试并推送镜像到 ghcr，**不部署**；
服务器上的发布完全由上面第二条命令手动触发，发布时机因此由你决定。

镜像在 ghcr 上默认继承仓库可见性（私有），服务器拉取前需要一张只读凭据。用
`GHCR_PULL_TOKEN=<classic PAT，仅 read:packages> GHCR_USERNAME=<你的 GitHub 用户名> ./deploy/build_from_ghcr.sh`
传入即可，脚本用完就 `docker logout`；不传则沿用本机已有的 docker 凭据。

容器名为 `xauat-loginapi`，加入外部网络 `xauat-net`（与 EduApi / PaymentAPI 共用），
默认不映射宿主机端口，对外经反向代理访问。日志落在具名卷上，重启时回填到 `/Logs`。

### 启动横幅的一个坑

首行会打印 `[startup] Native AOT: true/false`，用于确认线上跑的是哪个变体。
但 `dotnet run` 时它会**误报 True**：csproj 里的 `PublishAot=true` 会把
`RuntimeFeature.IsDynamicCodeSupported` 编成常量 false，即使进程是 JIT 运行的。
`docker build` 的两种变体都是准的（`-p:PublishAot` 与产物一致），只有本地 `dotnet run` 会骗人。

## 上游样本：已抓取并验证 ✅

登录侧解析器**已用真实账号抓取的上游响应验证过**（2026-09-22），样本经脱敏后固化在
`XAUAT.LoginApi.Tests/TestFixtures/`，并由 `Tests/Xauat/RealFixtureTests.cs` 守成回归测试：

| 环节 | 结果 |
| --- | --- |
| CAS 登录页字段 | `lt=""`、`execution="e1s1"`、`_eventId="submit"`、`pwdEncryptSalt="AbCdEfGhJkMnPqRs"` —— 与抓取脚本独立提取的结果完全一致 |
| 字符集 | CAS 与教务**全是 UTF-8**，无需引入 `System.Text.Encoding.CodePages` |

学期、课表、考试那三份样本与它们的回归测试**随日历功能迁到了 XAUAT.EduApi**
（那边是 `ExamService`/`CourseService` 在解析同一套教务数据）。

### 两个只有真实样本才能发现的坑

1. **`pwdEncryptSalt` 是只有 `id`、没有 `name` 的 input**。
   用"一条正则同时匹配 name 和 value"的实现会直接漏掉它，进而**把明文密码发出去**。
   本项目"先切 `<input>` 标签、再逐个读属性"的写法正是为此。

2. **`/for-std/exam-arrange` 少了尾斜杠会返回「学籍信息」页**（HTTP 200、无重定向）。
   Flask 因此一直在静默返回空考试列表。这条现在归 XAUAT.EduApi——它的 `ExamService`
   用的是带尾斜杠的地址，那份考古记录在该类的 `ExamArrangePath` 注释里。
   顺带一个更新：2026-09-22 抓的两个样本其实**都是「学籍信息」页**（见上），
   所以"考试页到底返回哪个变量"至今没有真实样本，得在考试周重抓。

## 项目结构

```
XAUAT.LoginApi.Web/
├── Xauat/          纯协议层：CAS 客户端、HTML 正则解析、AES 加密、cookie 罐
├── Services/       业务编排：登录三级优先级、活跃统计、测试账号
├── Redis/          裸 IDatabase 封装 + 限流封禁语义（刻意不用三级缓存，见上）
├── Ops/            日志环形缓冲 + 按天轮转文件
└── Endpoints/      Minimal API 端点

XAUAT.LoginApi.Tests/   112 个测试；TestFixtures/ 放上游抓取样本（仅 CAS 登录页）
tools/capture-fixtures.py
```

## 为什么不引入这些库

Native AOT 是硬约束（csproj 把 `IL2026`/`IL3050` 设成了编译错误）：

- **HtmlAgilityPack / AngleSharp** → 真正需要解析的只有三处，全部用 `[GeneratedRegex]` 源生成正则解决
- **Ical.Net** → ref 反射重；ICS 已迁到 XAUAT.EduApi，那边同样是手写（约 200 行）
- **Flurl** → 用 `HttpClient` 直连
- **Serilog** → 用内置 `ILogger` + 自定义 provider
