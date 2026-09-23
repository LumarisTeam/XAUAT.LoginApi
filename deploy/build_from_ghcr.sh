#!/usr/bin/env bash
#
# 服务器单例部署：从 ghcr 拉取预构建镜像，复用本目录的 compose 定义起一个容器。
#
# 为什么是"拉"而不是"构建"：本项目默认以 Native AOT 发布，服务器上从源码构建需要 SDK 镜像
# 加装 clang / zlib1g-dev，一次 publish 动辄数分钟并吃满 CPU 与内存；而 CI
# （.github/workflows/deploy-production.yml）已经在 ubuntu-latest 上构建并推送了镜像。
# 本脚本只负责拉取与重启。
#
# 变体切换（AOT / JIT）：AOT 是构建期选择，两种变体是两个镜像，靠 tag 区分——
#   默认（AOT）  -> ghcr.io/lijiajunply/xauat.loginapi:latest
#   JIT 变体      -> ghcr.io/lijiajunply/xauat.loginapi:jit
#   AOT=false ./build_from_ghcr.sh              # 切到 JIT 变体
#   ./build_from_ghcr.sh ghcr.io/...:<sha>-jit  # JIT 的某个具体版本
#
# 用法：
#   ./build_from_ghcr.sh
#   ./build_from_ghcr.sh ghcr.io/lijiajunply/xauat.loginapi:<commit-sha>   # 指定版本，也是回滚方式
#   AOT=false ./build_from_ghcr.sh
#   IMAGE=... NETWORK_NAME=... COMPOSE_PROJECT_NAME=... ./build_from_ghcr.sh
#   sh build_from_ghcr.sh                 # /bin/sh 是 dash 时同样可用（脚本会自己切到 bash）
#
# 同目录必须有：
#   docker-compose.yml 或 docker-compose.production.yml   （两种名字都认）
#   .env                                                   （见仓库根的 .env.example）

# 服务器上最常见的调用方式是 `sh build_from_ghcr.sh`，而 Debian/Ubuntu 的 /bin/sh 是 dash：
# 它既没有 pipefail，也没有 [[ ]] / (( )) / $SECONDS / $BASH_SOURCE，会在下面那行 set
# 直接以 "Illegal option -o pipefail" 退出，连参数校验都轮不到。
# 检测到当前不是 bash 就用 bash 重新执行自己，让 `sh x.sh` 与 `./x.sh` 完全等价。
if [ -z "${BASH_VERSION:-}" ]; then
  exec bash "$0" "$@"
fi

set -euo pipefail

CONTAINER_NAME="xauat-loginapi"

AOT="${AOT:-true}"
if [[ "$AOT" == "true" ]]; then
  DEFAULT_IMAGE="ghcr.io/lijiajunply/xauat.loginapi:latest"
else
  DEFAULT_IMAGE="ghcr.io/lijiajunply/xauat.loginapi:jit"
fi
IMAGE="${IMAGE:-${1:-$DEFAULT_IMAGE}}"

if [[ "$IMAGE" == *-jit || "$IMAGE" == *:jit ]]; then
  VARIANT="JIT 自包含（AOT 已关闭，非默认变体）"
else
  VARIANT="Native AOT（默认）"
fi
NETWORK_NAME="${NETWORK_NAME:-xauat-net}"
READY_TIMEOUT="${READY_TIMEOUT:-60}"

# compose 的 project 名默认取目录名。这里显式固定，否则固定 container_name 会与旧 project 撞名。
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-xauat-loginapi}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 显式导出：compose 插值优先级是 shell 环境 > 同目录 .env
export IMAGE NETWORK_NAME COMPOSE_PROJECT_NAME

# ------------------------------------------------------------------ 前置检查

command -v docker >/dev/null 2>&1 || { echo "错误：未找到 docker。" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "错误：需要 docker compose v2 插件（docker compose，而非旧版 docker-compose）。" >&2; exit 1; }

COMPOSE_FILE=""
for candidate in docker-compose.yml docker-compose.yaml docker-compose.production.yml; do
  if [[ -f "$candidate" ]]; then COMPOSE_FILE="$candidate"; break; fi
done
if [[ -z "$COMPOSE_FILE" ]]; then
  echo "错误：$SCRIPT_DIR 下找不到 docker-compose.yml 或 docker-compose.production.yml。" >&2
  exit 1
fi

if [[ ! -f .env ]]; then
  cat >&2 <<'ENV_HELP'
错误：未找到 .env。compose 的 env_file 指向它，缺失时 docker compose 会直接失败。

请在当前目录创建 .env，至少包含：
  ASPNETCORE_ENVIRONMENT=Production
  REDIS=                       # 建议填 Flask 现网那一个（两边共用同一批键）；留空 = 无缓存模式
  LOG_VIEW_TOKEN=              # 留空 = /Logs 完全开放

完整说明见仓库根目录的 .env.example。
ENV_HELP
  exit 1
fi

if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  owner="$(docker container inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$CONTAINER_NAME" 2>/dev/null || true)"
  [[ "$owner" == "<no value>" ]] && owner=""
  if [[ -n "$owner" && "$owner" != "$COMPOSE_PROJECT_NAME" ]]; then
    cat >&2 <<CONFLICT
错误：容器 $CONTAINER_NAME 已存在，但属于另一个 compose project「${owner}」。

compose 不会接管别的 project 的容器。二选一：
  docker rm -f $CONTAINER_NAME          # 让本脚本接管
  COMPOSE_PROJECT_NAME=$owner ./build_from_ghcr.sh   # 沿用那个 project
CONFLICT
    exit 1
  fi
fi

# ------------------------------------------------------------------ 共享网络

if ! docker network inspect "$NETWORK_NAME" >/dev/null 2>&1; then
  echo "==> 创建共享网络 $NETWORK_NAME"
  docker network create "$NETWORK_NAME" >/dev/null
fi

# ------------------------------------------------------------------ ghcr 登录

if [[ -n "${GHCR_PULL_TOKEN:-}" ]]; then
  : "${GHCR_USERNAME:?设置了 GHCR_PULL_TOKEN 就必须同时设置 GHCR_USERNAME}"
  trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT
  printf '%s' "$GHCR_PULL_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin
fi

# ------------------------------------------------------------------ 拉取与启动

previous_image="$(docker container inspect -f '{{.Config.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"

echo "==> 拉取镜像 $IMAGE"
docker compose -f "$COMPOSE_FILE" pull loginapi

echo "==> 启动容器（project=${COMPOSE_PROJECT_NAME}）"
docker compose -f "$COMPOSE_FILE" up -d --no-build --remove-orphans loginapi

# ------------------------------------------------------------------ 就绪等待

echo "==> 等待服务就绪（最多 ${READY_TIMEOUT}s）"
deadline=$(( SECONDS + READY_TIMEOUT ))
ready=0
while (( SECONDS < deadline )); do
  running="$(docker container inspect -f '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null || echo false)"
  if [[ "$running" != "true" ]]; then
    echo "错误：容器 $CONTAINER_NAME 已退出。日志：" >&2
    docker logs --tail 60 "$CONTAINER_NAME" >&2 || true
    exit 1
  fi

  # 用 bash 字符串匹配而不是管道 grep：pipefail 下 grep -q 提前退出会让 docker logs 吃到 SIGPIPE。
  logs="$(docker logs "$CONTAINER_NAME" 2>&1 || true)"
  if [[ "$logs" == *"Now listening on"* || "$logs" == *"Application started"* ]]; then
    ready=1
    break
  fi
  sleep 1
done

if (( ! ready )); then
  echo "错误：${READY_TIMEOUT}s 内没等到启动完成。最近日志：" >&2
  docker logs --tail 60 "$CONTAINER_NAME" >&2 || true
  exit 1
fi

# ------------------------------------------------------------------ 收尾

docker image prune --force >/dev/null

digest="$(docker image inspect --format '{{index .RepoDigests 0}}' "$IMAGE" 2>/dev/null || true)"

echo
echo "==> 部署完成"
echo "    容器 : $CONTAINER_NAME"
echo "    镜像 : $IMAGE"
echo "    变体 : $VARIANT"
if [[ -n "$digest" ]]; then echo "    摘要 : $digest"; fi
echo "    网络 : ${NETWORK_NAME}"
echo
echo "    自检 : docker run --rm --network $NETWORK_NAME curlimages/curl -s http://$CONTAINER_NAME:8080/health"
echo "    排障 : docker logs -f $CONTAINER_NAME"
echo "           （final 镜像是 chiseled 变体，无 shell 无 curl，docker exec 进不去）"
echo "    变体确认 : docker logs $CONTAINER_NAME | grep '\[startup\]'"
if [[ -n "$previous_image" && "$previous_image" != "$IMAGE" ]]; then
  echo "    回滚 : $0 $previous_image"
fi
