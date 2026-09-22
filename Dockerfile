# Native AOT 产物是自包含的原生可执行文件，因此：
#   - 不需要 aspnet 运行时镜像，runtime-deps 即可（chiseled 变体还能再去掉 shell，缩小攻击面）
#   - 不能加 /p:UseAppHost=false（那样就没有 apphost 了），ENTRYPOINT 直接跑原生二进制
#   - 必须显式指定 -r linux-x64（macOS 上无法交叉编译到 linux-x64，所以本机验证不了，见 CI 的 aot-check）
#
# ---------- 构建期开关：AOT_ENABLED ----------
# 只认 true / false 两个值（默认 true，与既有行为一致）：
#   true  -> Native AOT，产出原生二进制
#   false -> 普通 JIT 发布，仍带 --self-contained，产出 apphost（名字同为 XAUAT.LoginApi）+ 托管 dll
#
# 为什么 JIT 模式不换基础镜像：自包含产物自带 .NET 运行时，所以照样跑在
# runtime-deps:10.0-noble-chiseled 上。两种模式的最终镜像、USER、ENTRYPOINT、
# "无 shell 无 curl"的排障约束因此完全一致，部署侧只需要区分镜像 tag。
#
# 用法：
#   docker build -t xauat-loginapi .
#   docker build --build-arg AOT_ENABLED=false -t xauat-loginapi:jit .

# FROM 之前声明：全局 ARG，只在这里可见，各 stage 内要重新声明才能用
ARG AOT_ENABLED=true

# ---------- build & publish ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG AOT_ENABLED

# 取值校验放在最前面：写成 True / 0 / yes 时，下面的 shell 比较会当它是"非 true"从而跳过
# clang，而 MSBuild 那边 True 又是合法布尔值、照旧走 AOT，最后在链接阶段报出看不懂的错误。
RUN case "$AOT_ENABLED" in \
        true|false) echo "AOT_ENABLED=$AOT_ENABLED" ;; \
        *) echo "AOT_ENABLED 只接受 true 或 false，收到：'$AOT_ENABLED'" >&2; exit 1 ;; \
    esac

# Native AOT 在 Linux 上链接需要 clang 与 zlib 头文件，缺失会在 publish 阶段报链接错误。
# JIT 模式不需要，跳过可省掉几十 MB，也让这一层能独立命中缓存。
RUN if [ "$AOT_ENABLED" = "true" ]; then \
        apt-get update \
        && apt-get install -y --no-install-recommends clang zlib1g-dev \
        && rm -rf /var/lib/apt/lists/*; \
    else \
        echo "AOT_ENABLED=false：JIT 发布不需要 clang/zlib1g-dev，跳过"; \
    fi

WORKDIR /src
COPY ["XAUAT.LoginApi.Web/XAUAT.LoginApi.Web.csproj", "XAUAT.LoginApi.Web/"]
# restore 与 publish 必须带同一个 PublishAot 值：csproj 里默认是 true，两阶段不一致
# 会让 NuGet 解析出不同的依赖图（ILCompiler 这类只在 AOT 需要的包）。
RUN dotnet restore "XAUAT.LoginApi.Web/XAUAT.LoginApi.Web.csproj" \
        -r linux-x64 \
        -p:PublishAot=$AOT_ENABLED

COPY . .
WORKDIR "/src/XAUAT.LoginApi.Web"
RUN dotnet publish "XAUAT.LoginApi.Web.csproj" \
        -c $BUILD_CONFIGURATION \
        -r linux-x64 \
        --self-contained \
        -p:PublishAot=$AOT_ENABLED \
        -o /app/publish

# 预建空的 logs 目录并让发布产物带上它：chiseled 镜像没有 shell，final 阶段跑不了 RUN mkdir，
# 只能靠 COPY 把这个目录（连同 owner）带过去。compose 里的具名卷首次挂载时会继承这里的
# 目录内容与属主，从而让非 root 的 APP_UID 能写入——否则日志会因权限失败静默丢失。
RUN mkdir -p /app/publish/logs

# ---------- final ----------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS final
WORKDIR /app
EXPOSE 8080
USER $APP_UID
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
ENTRYPOINT ["./XAUAT.LoginApi"]
