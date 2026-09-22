#!/usr/bin/env bash
#
# 根目录源码构建：git pull -> docker build -> 换掉旧容器。
# 与 deploy/build.sh 是两条并行路径，别混着用：
#
#   本脚本      本机从源码构建镜像并起容器。适合服务器上没有 ghcr 凭据、或就是要跑当前工作区代码。
#   deploy/     从 ghcr 拉 CI 构建好的不可变镜像，用 compose 起（CI 的 deploy job 走的就是它）。
#
# 两条路径的容器名都是 xauat-loginapi，互相不能叠加：本脚本起的是 docker run 直接创建的容器，
# 不带 compose 标签，之后再用 deploy/build.sh 会因容器名冲突而失败，需要先
# `docker rm -f xauat-loginapi`（脚本末尾会把这条命令打出来）。
#
# 与 PaymentAPI 脚本的差异：
#   - 容器加入 xauat-net，但不映射任何宿主机端口。本服务对外是经反向代理访问的
#     （它替代的是 schedule.xauat.site 上的 Flask），不需要在宿主机上直接暴露。
#   - 额外挂一个具名卷到 /app/logs，否则容器重建后日志与 /Logs 的历史都会丢。
#
# AOT 是构建期开关，两种变体各自打 tag、并存互不覆盖：
#   ./build.sh              # Native AOT（默认）-> xauat-loginapi:latest
#   AOT=false ./build.sh    # JIT 自包含         -> xauat-loginapi:jit
#
# 用法（在仓库根目录）：
#   ./build.sh
#   sh build.sh                         # 只用 POSIX 语法写的，dash 下也能跑
#   AOT=false ./build.sh
#   DOCKER="sudo docker" ./build.sh
#
# 本脚本刻意**只使用 POSIX sh 语法**，不用 [[ ]]、(( ))、$SECONDS、$BASH_SOURCE、set -o pipefail：
# 服务器上最常见的调用方式就是 `sh build.sh`，而 Debian/Ubuntu 的 /bin/sh 是 dash，
# 上面那些 bash 专有写法会在 `set -o pipefail` 那一行就以
# "set: Illegal option -o pipefail" 直接退出（后面全部不执行，连参数校验都轮不到）。
# 已用 /bin/dash 实测通过。

set -eu

CONTAINER_NAME="xauat-loginapi"
NETWORK_NAME="${NETWORK_NAME:-xauat-net}"
LOG_VOLUME="${LOG_VOLUME:-xauat-loginapi-logs}"
READY_TIMEOUT="${READY_TIMEOUT:-60}"
AOT="${AOT:-true}"
DOCKER="${DOCKER:-docker}"

# 用 $0 而不是 $BASH_SOURCE[0]：后者是 bash 专有。`sh build.sh` 时 $0 就是脚本路径本身。
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# AOT 取值先在最前面校验：取值非法时 Dockerfile 里那道校验要等 SDK 镜像拉完才开始跑，
# 而这里能立刻报错。
case "$AOT" in
  true)  IMAGE="${IMAGE:-xauat-loginapi:latest}" ;;
  false) IMAGE="${IMAGE:-xauat-loginapi:jit}" ;;
  *)
    echo "错误：AOT 只接受 true 或 false，收到：'$AOT'" >&2
    exit 1
    ;;
esac

# ------------------------------------------------------------------ 前置检查

if ! $DOCKER version >/dev/null 2>&1; then
  echo "错误：'$DOCKER' 不可用。需要已安装并启动 Docker；若只能用 sudo，请 DOCKER=\"sudo docker\" ./build.sh" >&2
  exit 1
fi

if [ ! -f .env ]; then
  cat >&2 <<'ENV_HELP'
错误：未找到 ./.env。

请在仓库根目录创建 .env，至少包含：
  ASPNETCORE_ENVIRONMENT=Production
  REDIS=                       # 建议填 Flask 现网那一个（两边共用同一批键）；留空 = 无缓存模式
  LOG_VIEW_TOKEN=              # 留空 = /Logs 完全开放

完整说明见 .env.example。注意 .env 不要提交进 git（.dockerignore 已排除它）。
ENV_HELP
  exit 1
fi

# ------------------------------------------------------------------ 更新代码

echo "==> 拉取最新代码"
git pull

# ------------------------------------------------------------------ 构建

echo "==> 构建镜像 ${IMAGE}（AOT_ENABLED=${AOT}）"
$DOCKER build --build-arg "AOT_ENABLED=$AOT" -t "$IMAGE" .

# ------------------------------------------------------------------ 共享网络与卷

if ! $DOCKER network inspect "$NETWORK_NAME" >/dev/null 2>&1; then
  echo "==> 创建共享网络 $NETWORK_NAME"
  $DOCKER network create "$NETWORK_NAME" >/dev/null
fi

if ! $DOCKER volume inspect "$LOG_VOLUME" >/dev/null 2>&1; then
  echo "==> 创建日志卷 $LOG_VOLUME"
  $DOCKER volume create "$LOG_VOLUME" >/dev/null
fi

# ------------------------------------------------------------------ 换容器

if $DOCKER container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  compose_project="$($DOCKER container inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$CONTAINER_NAME" 2>/dev/null || true)"
  case "$compose_project" in
    "<no value>") compose_project="" ;;
  esac
  if [ -n "$compose_project" ]; then
    echo "==> 移除旧容器（原属 compose project「${compose_project}」，此后改由本脚本接管）"
  else
    echo "==> 移除旧容器"
  fi
  $DOCKER rm -f "$CONTAINER_NAME" >/dev/null
fi

echo "==> 启动容器"
# --network-alias 与 --name 同名，是显式声明 DNS 名；
# --restart / env_file / 不映射端口三项与 deploy/docker-compose.production.yml 对齐，改一处要改两处。
$DOCKER run -d \
  --name "$CONTAINER_NAME" \
  --network "$NETWORK_NAME" \
  --network-alias "$CONTAINER_NAME" \
  --restart unless-stopped \
  --env-file .env \
  -e LOG_DIR=/app/logs \
  -v "$LOG_VOLUME:/app/logs" \
  "$IMAGE" >/dev/null

# ------------------------------------------------------------------ 就绪等待

# 最常见的启动失败是 .env 里 REDIS 填了地址却连不上：abortConnect 默认 true，
# 进程会直接退出而不是降级。所以不只看容器在不在跑，还要等它真的监听起来。
echo "==> 等待服务就绪（最多 ${READY_TIMEOUT}s）"
deadline=$(( $(date +%s) + READY_TIMEOUT ))
ready=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  running="$($DOCKER container inspect -f '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null || echo false)"
  if [ "$running" != "true" ]; then
    echo "错误：容器 $CONTAINER_NAME 已退出。日志：" >&2
    $DOCKER logs --tail 60 "$CONTAINER_NAME" >&2 || true
    exit 1
  fi

  # 用 case 做字符串匹配而不是 `docker logs | grep -q`：
  # 一来 case 是 POSIX 的，二来也顺带避开了 pipefail 下 grep -q 提前退出导致
  # docker logs 吃到 SIGPIPE、整个管道被判失败而误报的坑。
  logs="$($DOCKER logs "$CONTAINER_NAME" 2>&1 || true)"
  case "$logs" in
    *"Now listening on"* | *"Application started"*)
      ready=1
      break
      ;;
  esac
  sleep 1
done

if [ "$ready" -ne 1 ]; then
  echo "错误：${READY_TIMEOUT}s 内没等到启动完成。最近日志：" >&2
  $DOCKER logs --tail 60 "$CONTAINER_NAME" >&2 || true
  exit 1
fi

# ------------------------------------------------------------------ 收尾

# 变体从容器自己打的启动横幅里取，而不是从上面的 $AOT 变量推——万一镜像 tag 被 IMAGE 覆盖、
# 或构建参数没生效，这样能立刻看出来。
variant="$($DOCKER logs "$CONTAINER_NAME" 2>&1 | grep -F '[startup] Native AOT:' | tail -1 || true)"

echo
echo "==> 构建并启动完成"
echo "    容器 : $CONTAINER_NAME"
echo "    镜像 : $IMAGE"
echo "    变体 : ${variant:-未从日志取到，见 docker logs $CONTAINER_NAME}"
echo "    网络 : ${NETWORK_NAME}（不映射宿主机端口，经反向代理访问）"
echo "    日志卷: $LOG_VOLUME -> /app/logs"
echo
echo "    自检 : docker run --rm --network $NETWORK_NAME curlimages/curl -s http://$CONTAINER_NAME:8080/health"
echo "    日志 : docker logs -f $CONTAINER_NAME"
echo "    重建 : ./build.sh ； 切 JIT 变体： AOT=false ./build.sh"
echo
echo "    注意 : 本容器由 docker run 直接创建，不带 compose 标签。"
echo "           以后若改回 CI/deploy 那条路径（deploy/build.sh），先执行："
echo "             docker rm -f $CONTAINER_NAME"
