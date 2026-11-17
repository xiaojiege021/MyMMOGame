#!/usr/bin/env bash
# tools/check/check-contract.sh —— D16 契约闸：proto 生成物清单 + 队列风险列 + 卡片契约模板。
#
# 完成标准见 docs/开发架构.md 14.11.3。无库段，可在 CI 跑：
#   bash tools/check/check-contract.sh
#
# 核对内容（全部只读）：
#   1. proto/manifest.json 覆盖 proto/mmo/*.proto 全部，改 proto 没重出生成物必红；
#   2. 两端生成物与清单 sha256 对不上必红（手改生成物也在这里抓）；
#   3. 服务端与客户端同名 Xxx.cs 逐字节一致，客户端没有 XxxGrpc.cs；
#   4. 任务队列每一行都有风险标记，图例在开发架构 14.11.1 与队列表下各一份；
#   5. 卡片契约模板六栏与 D8 / P1-5a 两个真实示例还在；
#   6. 自检：上面四类坏法在临时副本上逐一改坏，必须各自被抓住。
#
# 清单由 tools/gen/gen-proto-manifest.py 生成（gen-proto.sh 生成之后自动调用）；
# 本脚本只核对，不改清单、不改生成物、不跑 protoc。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if ! command -v python3 >/dev/null 2>&1; then
  echo "  FAIL 没有 python3：清单与哈希都靠它读"
  echo "核对：0 项通过，1 项未通过"
  exit 1
fi

exec python3 "$ROOT/tools/check/check-contract.py" --root "$ROOT" --self-test "$@"
