#!/usr/bin/env bash
# tools/env/db-migrate.sh —— D8：建库 / 升级一条命令。
#
#   bash tools/env/db-migrate.sh                     三个 schema 按清单建齐 / 升级
#   bash tools/env/db-migrate.sh --schema character  只跑一个 schema
#   bash tools/env/db-migrate.sh --schema-status     只读：已应用到第几版
#   MMO_DB_MIGRATE_SKIP_BUILD=1 bash tools/env/db-migrate.sh   不重新编译 probe
#
# 连接串仍从 MMO_GAMEDB 或 /srv/mmo/gamedb.env 读，脚本不打印、不落盘；
# 库用户不需要建库权限（技术架构 6 节）：只建 schema / 表 / 版本表。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
if [ ! -x "$DOTNET" ]; then
  DOTNET="$(command -v dotnet || true)"
fi
if [ -z "$DOTNET" ]; then
  echo "db_migrate=no_dotnet 需要 .NET SDK 10.0.403（见 global.json）" >&2
  exit 2
fi

PROJECT="$ROOT/tools/probe/CliProbe/CliProbe.csproj"
PROBE="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"

if [ "${MMO_DB_MIGRATE_SKIP_BUILD:-0}" != "1" ]; then
  if ! "$DOTNET" build "$PROJECT" -c Release --nologo -v q >/dev/null; then
    echo "db_migrate=build_failed probe 编译失败；先看 dotnet build tools/probe/CliProbe/CliProbe.csproj" >&2
    exit 2
  fi
fi

if [ ! -f "$PROBE" ]; then
  echo "db_migrate=probe_missing 没有 $PROBE；先跑一次不带 MMO_DB_MIGRATE_SKIP_BUILD=1 的迁移" >&2
  exit 2
fi

# --schema-status 是只读入口，不能和 --migrate 同时给；其余参数原样透传。
if [ "${1:-}" = "--schema-status" ]; then
  exec "$DOTNET" "$PROBE" "$@"
fi

exec "$DOTNET" "$PROBE" --migrate "$@"
