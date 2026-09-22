# AGENTS.md

请使用中文回答

This file provides guidance to Codex (claude.ai/code) when working with code in this repository.

## Project Overview

XAUAT.LoginApi 是西安建筑科技大学统一认证（CAS/UAAP）与教务系统访问服务，
.NET 10 + Native AOT + Minimal API。它是
[`xauat_login_flask`](../../PythonProjects/xauat_login_flask) 中 XAUAT 部分的 .NET 重写，
替代部署在 `schedule.xauat.site` 的那套 Flask。

`XAUAT.EduApi` **不做 SSO 握手**——它只 POST 到本服务拿 `{success, cookies}`
（见其 `SSOLoginService`）；EduApi 的日历订阅也只是把客户端 302 到这里。
所以本服务的接口形状是**跨服务契约**，不是内部细节。

## Build and Run Commands

```bash
dotnet build
dotnet run --project XAUAT.LoginApi.Web        # 默认 http://localhost:5192
dotnet test
dotnet test --filter "FullyQualifiedName~AuthServiceTests"
dotnet test --filter "FullyQualifiedName~AuthServiceTests.Login_ShouldReturnCachedEduCookieWithoutTouchingUpstream"

# AOT 编译校验（macOS 上只能验 arm64；linux-x64 无法交叉编译，由 CI 的 aot-check 把关）
dotnet publish XAUAT.LoginApi.Web/XAUAT.LoginApi.Web.csproj -c Release -r osx-arm64 \
  -p:PublishAot=true -o /tmp/aot
grep -E "warning IL2026|warning IL3050" /tmp/aot.log && echo "AOT 不兼容" || echo "干净"

# 抓取上游真实样本（写解析器之前必做，见下）
uv run --project ../../PythonProjects/xauat_login_flask \
  python tools/capture-fixtures.py <学号> <密码>
```

## 三条不能忘的约束

### 1. Redis 键名是跨实现契约，**不能加前缀**

与 PaymentAPI（`PaymentApi:`）、EduApi（`eduapi`）相反，本服务**复用 Flask 的原键名**，
并指向同一个 Redis 实例——这样灰度期间两边能并行跑，缓存不冷、封禁延续。
键名集中定义在 `Redis/LoginCacheKeys.cs`，由
`Tests/Compatibility/LoginCacheKeysTests.cs` 逐字钉死。

加了前缀不会报任何错，只会表现为"登录变慢、活跃统计归零、封禁失效"。

也因此**刻意不用** PaymentAPI 那套三级 `CacheService`：它会加前缀、并把值包成
`CacheItem<T>` JSON 信封，与裸值不兼容。取而代之的是 `Redis/LoginRedisStore.cs`
（直连 `IDatabase` 的薄封装，Redis 挂了就降级）。

### 2. Native AOT 是硬约束

csproj 把 `IL2026`/`IL3050` 设成了编译错误。因此：

- 上游 HTML 解析**只用 `[GeneratedRegex]`**（`Xauat/XauatHtmlParser.cs`）——
  真正需要解析的只有 CAS 登录页字段、学期 id、考试页 JS 数组三处，
  **不要**引入 HtmlAgilityPack / AngleSharp
- ICS **手写**（`Services/IcsCalendarWriter.cs`），不要引入 Ical.Net
- JSON 全部走源生成上下文；响应统一用 `ApiResults.Json<T>()` /
  `Api.Json`，**不要**用 `Results.Json(value, JsonSerializerOptions, ...)` 那个重载（带 `RequiresDynamicCode`）
- 日志用内置 `ILogger` + 自定义 provider，不要引入 Serilog

### 3. 上游行为再怪也要照抄

移植时踩过的几个"看起来像 bug 但是对的"：

- CAS 登录 POST **必须** `allow_redirects=false`，且只从 302 响应本身取 cookie——
  因此直接登录**只能拿到 SSO 票据（CASTGC），拿不到教务会话**，
  必须再调一次 `ExchangeSsoTicketAsync` 换票。教务 cookie 在跳转链的第 2 跳种下。
- 认证服务器是**明文 HTTP**（`http://authserver.xauat.edu.cn`），不要升级成 HTTPS。
- 学期 id 的正则是 `selected" value="(.*?)"`——看着像笔误，实则命中"被选中项"。
- `#pwdEncryptSalt` 有三态：输入框缺失 → 明文密码；有框无 `value` → 用默认盐；
  有框 `value=""` → **空串**（走明文）。写成"空串回退默认盐"会发出一份上游解不开的密文。
- `POST /ws/schedule-table/datum` 的 `studentId` 是**字面量字符串 `"null"`**，不是 JSON null。
- `/login/{u}/{p}` 登录失败**返回 200**，是刻意保留的历史怪癖。

## Architecture

```
Xauat/       纯协议层，无 Redis、无业务编排
             XauatSsoClient(CAS 登录 + 换票) / XauatAcademicClient(学期/课表/考试)
             XauatHtmlParser([GeneratedRegex]) / PasswordEncryptor(AES-CBC/PKCS7) / XauatCookieJar
Services/    AuthService(登录三级优先级) / CalendarService / IcsCalendarWriter / StatisticsService
Redis/       LoginRedisStore(裸 IDatabase) / BanService(限流与封禁语义) / LoginCacheKeys
Ops/         InMemoryLogStore(环形缓冲 2000) / OpsLoggerProvider / LogFileWriter(按天轮转 31 天)
Endpoints/   Auth / Calendar / Statistics / Ops
```

### 登录三级优先级（`Services/AuthService.cs`）

1. 缓存的 eduCookie 命中 → 直接返回。**不消耗限流额度**（否则正常用户会被自己刷限流）
2. 缓存的 SSO 票据命中 → 换票；成功则回填 eduCookie
3. 完整登录。限流名额是**预占制**（先占再打网络，成功后归还）

限流两级：20 分钟内失败 5 次 → 当场拒绝；3 次触发第一级（间隔 ≥20 分钟）→ 封禁 24 小时。
封禁键按 `(学校, 用户名)`，不再含密码哈希（原设计换个错误密码即可绕过）。

### Cookie 必须按调用隔离

**不要**用共享 `CookieContainer`/`UseCookies=true`——它们的生命周期绑在池化的
`HttpMessageHandler` 上，并发登录会互相串号（A 的教务会话被 B 覆盖）。
每次登录新建一个 `XauatCookieJar`，按 host 归属，显式拼 `Cookie` 头。

### 日志

`app.UseCors()` 与 `app.UseRateLimiter()` 顺序别动：`/Logs` 用了 `RequireCors`，
只注册策略不挂中间件会在**请求时**抛异常。
`/Logs` 的 CORS 白名单在 `Ops/LogsCors.cs`，注意 Flask 的 README 声称放行 `*.zeabur.app`
但代码里并没有——这里按代码实现。

## Testing

xUnit + Moq。两个测试替身在 `TestSupport/`：

- `FakeLoginRedisStore` —— 内存版 Redis，**记录每一步操作**，
  因此可以断言"命中缓存不消耗限流额度"这类行为（Flask 的测试就是这么写的）
- `FakeSsoClient` —— 默认行为是"完整登录只返回 SSO 票据、换票才返回教务会话"，
  即真实上游的行为，让测试默认跑在生产路径上

`Tests/TestFixtures/`（上游抓取样本）与 `XAUAT.LoginApi.Web/TestFixtures/`
（测试账号旁路数据）**用途不同**，别搞混。

## 尚未完成

`tools/capture-fixtures.py` **还没有用真实账号跑过**。CAS 选择器、考试页正则、AES 加密
都是按 Flask 代码逆推的，逻辑逐行对齐但未经真实响应验证。切换生产前必须先跑一次并核对
`capture-report.txt`。
