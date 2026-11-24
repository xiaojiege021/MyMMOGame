#!/usr/bin/env bash
# tools/check/check-c5.sh —— C5 对话单一来源（开发架构 14.6 顺序 7）。
#
# 完成标准（14.6 的 C5 行，逐条对应下面的断言）：
#   1. 客户端不再硬编码那 10 条定词；
#   2. 改 config/dialogue 后屏上跟着变；
#   3. 全仓只有一份文案。
#
# 口径（写死在断言里，后面加卡不要放松）：
#   - 文案正本只有 config/dialogue/*.json；其它地方（服务端 / 客户端 / proto / SQL /
#     配置）不许再写第二份。
#   - 客户端唯一的内容表 UiContentTable.cs 由 tools/gen/gen-ui-content.py 生成；
#     它是派生件，不是第二份正本：生成物里的每一条都来自 config/dialogue，生成器重跑
#     必须逐字一致。手写代码（UiWorldCodex / 各 U 屏 / 自检）不许出现任何一条定词。
#   - 「改配置后屏上跟着变」用一次临时副本演练：改一句话、加一条，再生成，
#     断言屏上读到的生成表跟着变；真 config 一个字节都不动。
#
#   bash tools/check/check-c5.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

TMP="$(mktemp -d)"
TITLE="C5+D9（文案：对话 + 界面）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 静态断言：正本、生成物、手写代码 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import glob, json, os, re

root = os.environ["ROOT"]
assets = os.path.join(root, "client/MMOGame/Assets")
content_path = os.path.join(assets, "HotUpdate/Ui/UiContentTable.cs")
codex_path = os.path.join(assets, "HotUpdate/Ui/UiWorldCodex.cs")
screen_path = os.path.join(assets, "HotUpdate/Ui/UiDialogueScreen.cs")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(os.path.join(root, p)))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

world = cfg("config/world.json")
speakers = list(world.get("speakers") or [])

# ---------------------------------------------------------------- 1. 配置正本
files = sorted(glob.glob(os.path.join(root, "config/dialogue/*.json")))
docs = {}
for path in files:
    doc = json.load(open(path, encoding="utf-8"))
    name = os.path.basename(path)[:-5]
    docs[name] = doc
    if doc.get("id") != name or doc.get("name") != name:
        report("dialogue_file_id", False, name)
        break
else:
    report("dialogue_file_id", True)

report("dialogue_speakers", len(files) >= 9 and len(speakers) >= 9
       and all(s in docs for s in speakers)
       and set(docs) == set(speakers),
       "files=%d speakers=%d" % (len(files), len(speakers)))

lines = []
bad_fields = []
for name in speakers:
    doc = docs.get(name) or {}
    if not doc.get("lines"):
        bad_fields.append(name + ":empty")
        continue
    for line in doc["lines"]:
        chapter = line.get("chapter")
        if not isinstance(chapter, int) or not 1 <= chapter <= 12:
            bad_fields.append(name + ":chapter")
        if not line.get("text"):
            bad_fields.append(name + ":text")
        if not line.get("limit"):
            bad_fields.append(name + ":limit")
        if line.get("region") and line["region"] not in (world.get("regions") or []):
            bad_fields.append(name + ":region")
        lines.append((chapter, line.get("region") or "",
                      line.get("text") or "", line.get("limit") or ""))
report("dialogue_lines", not bad_fields and len(lines) >= 14,
       "lines=%d bad=%s" % (len(lines), ",".join(bad_fields[:6])))

# ---------------------------------------------------------------- 2. 生成物逐条比对
content = read(content_path)
codex = read(codex_path)
screen = read(screen_path)
report("generated_version", "public const int Version = 2;" in content, "Version 不是 2")
report("generated_dialogue_table",
       "UiWorldCodex.SpeakerLine[][] SpeakerDialogue" in content
       and "public const int Version = 2;" in content,
       "生成物没有 SpeakerDialogue")

pattern = re.compile(r"new UiWorldCodex\.SpeakerLine\s*\{(.*?)\}", re.S)
generated = []
for chunk in pattern.findall(content):
    m_chapter = re.search(r"Chapter = (\d+)", chunk)
    m_region = re.search(r'Region = ("(?:\\.|[^"\\])*")', chunk)
    m_text = re.search(r'Text = ("(?:\\.|[^"\\])*")', chunk)
    m_limit = re.search(r'Limit = ("(?:\\.|[^"\\])*")', chunk)
    if not (m_chapter and m_region and m_text and m_limit):
        generated.append(("__parse_fail__",))
        continue
    generated.append((int(m_chapter.group(1)),
                      json.loads(m_region.group(1)),
                      json.loads(m_text.group(1)),
                      json.loads(m_limit.group(1))))
report("generated_line_count", len(generated) == len(lines),
       "generated=%d config=%d" % (len(generated), len(lines)))
report("generated_lines_match",
       generated == lines,
       "第一条不一致时看这里")

# ---------------------------------------------------------------- 3. 手写代码不写文案
config_texts = [x[2] for x in lines] + [x[3] for x in lines]
hand_hits = []
for dirpath, dirnames, names in os.walk(assets):
    dirnames[:] = [d for d in dirnames if d not in (".git", "Library")]
    for name in names:
        if not name.endswith((".cs", ".txt", ".json")):
            continue
        path = os.path.join(dirpath, name)
        # Windows 上 os.path.join 给反斜杠，content_path 里带正斜杠，直接比永远不等，
        # 于是「生成物自己」也被当成手写文案扫出命中。先归一成正斜杠再比。
        if path.replace(os.sep, "/") == content_path.replace(os.sep, "/"):
            continue
        body = read(path)
        hit = [t for t in config_texts if t in body]
        if hit:
            hand_hits.append(os.path.relpath(path, root) + ":" + hit[0][:12])
report("client_no_hardcoded_dialogue", not hand_hits, ";".join(hand_hits[:6]))
report("codex_drops_speaker_texts",
       "SpeakerTexts" not in codex
       and "QiaoChapter1" not in codex
       and "QiaoChapter2" not in codex,
       "UiWorldCodex 还留着旧的手写台词表 / 常量")
report("codex_uses_generated_dialogue",
       "UiContentTable.SpeakerDialogue" in codex
       and "Lines = lines" in codex
       and "TotalDialogueLines" in codex,
       "UiWorldCodex 没把生成表接进 Speakers")
report("screen_reads_speaker_lines",
       "speaker.Lines" in screen
       and "UiWorldCodex.TotalDialogueLines" in screen
       and "speaker.SecondText" in screen,
       "对白屏没读生成表里的 Lines")

# ---------------------------------------------------------------- 4. 全仓运行期只有一份文案
runtime_hits = []
for base in ("src", "client/MMOGame/Assets", "tools", "proto", "db", "config"):
    for dirpath, dirnames, names in os.walk(os.path.join(root, base)):
        dirnames[:] = [d for d in dirnames
                       if d not in (".git", "bin", "obj", "Library")]
        for name in names:
            if not name.endswith((".cs", ".py", ".sh", ".json", ".proto", ".sql", ".txt")):
                continue
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            if rel == os.path.relpath(content_path, root).replace(os.sep, "/") \
               or rel.startswith("config/dialogue/"):
                continue
            body = read(path)
            hit = [t for t in config_texts if t in body]
            if hit:
                runtime_hits.append(rel + ":" + hit[0][:12])
# 本脚本自己也不许把定词抄进来（上面已扫 tools/，这里是显式提醒）。
report("repo_single_source", not runtime_hits, ";".join(runtime_hits[:6]))

meta = read(content_path + ".meta")
report("generated_meta", re.search(r"^guid: [0-9a-f]{32}$", meta, re.M) is not None,
       "生成物缺 .meta / guid")

ci = read(os.path.join(root, "tools/check/ci.sh"))
report("ci_registers_c5", re.search(r'^CHECKS="[\s\S]*\bc5\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 c5")
allsh = read(os.path.join(root, "tools/check/check-all.sh"))
report("check_all_registers_c5",
       re.search(r"^\s*c5\)", allsh, re.M) is not None and re.search(r"\bc5\b", allsh) is not None,
       "check-all.sh 没挂 c5")

print("INFO speakers=%d lines=%d generated=%d" % (len(speakers), len(lines), len(generated)))
print("PASS static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
    INFO) printf '  ..   %s\n' "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -q '^PASS static_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL|INFO) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 2. 生成器重跑与提交的 UiContentTable.cs 逐字一致 =="
GEN="$TMP/UiContentTable.cs"
if python3 tools/gen/gen-ui-content.py --out "$GEN" >"$TMP/gen.log" 2>&1 \
   && diff -q client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs "$GEN" >/dev/null; then
  ok "生成器重跑逐字一致（正本仍是 config/dialogue）"
else
  bad "客户端生成物过期，先跑 python3 tools/gen/gen-ui-content.py"
  diff -u client/MMOGame/Assets/HotUpdate/Ui/UiContentTable.cs "$GEN" 2>/dev/null | head -8 | detail
  tail -3 "$TMP/gen.log" | detail
fi

echo "== 3. 改配置演练：改一句 / 加一条，生成表跟着变 =="
mkdir -p "$TMP/root/tools/gen" "$TMP/out"
cp -R config "$TMP/root/config"
cp tools/gen/ui_map_layout.json "$TMP/root/tools/gen/ui_map_layout.json"
BEFORE="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' config/dialogue/乔小满.json)"
if ROOT="$ROOT" TMPROOT="$TMP/root" OUT="$TMP/out/UiContentTable.cs" python3 - > "$TMP/mutate.txt" 2>&1 <<'PY'
import json, os, subprocess, sys

root = os.environ["ROOT"]
tmp_root = os.environ["TMPROOT"]
out = os.environ["OUT"]
path = os.path.join(tmp_root, "config/dialogue/乔小满.json")
doc = json.load(open(path, encoding="utf-8"))
old = doc["lines"][0]["text"]
doc["lines"][0]["text"] = "C5 演练：这一句只在临时配置里"
doc["lines"].append({
    "chapter": 2,
    "region": "枯岚道",
    "text": "C5 演练：新加的一条",
    "limit": "只用于核对，不进正本",
})
with open(path, "w", encoding="utf-8") as f:
    json.dump(doc, f, ensure_ascii=False, indent=2)

subprocess.check_call([sys.executable, os.path.join(root, "tools/gen/gen-ui-content.py"),
                       "--root", tmp_root, "--out", out],
                      stdout=subprocess.DEVNULL)

body = open(out, encoding="utf-8").read()
# 基准条数从临时配置现数：以后内容卡加台词，这个演练不用再改「15」。
before_n = 0
for name in os.listdir(os.path.join(tmp_root, "config/dialogue")):
    if name.endswith(".json"):
        before_n += len(json.load(open(os.path.join(tmp_root, "config/dialogue", name),
                                       encoding="utf-8")).get("lines") or [])
after_n = body.count("new UiWorldCodex.SpeakerLine")
assert "C5 演练：这一句只在临时配置里" in body, "改过的第一句没进生成表"
assert old not in body, "旧的第一句还在生成表里"
assert "C5 演练：新加的一条" in body, "新加的一条没进生成表"
assert after_n == before_n, "加一条后应是 %d 条，实际 %d" % (before_n, after_n)
print("ok changed_line_added_line=1 count=%d" % after_n)
PY
then
  ok "只改临时 config，生成表就换成了新句子 / 新条数（屏读的是这张表）"
else
  bad "改配置演练失败"
  cat "$TMP/mutate.txt" | detail
fi
AFTER="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' config/dialogue/乔小满.json)"
if [ "$BEFORE" = "$AFTER" ]; then
  ok "真 config/dialogue 一个字节都没动"
else
  bad "真 config/dialogue 被演练改动了"
fi

# ════════════════════════════════════════════════════════════════════
# D9 客户端文案表（14.6 顺序 14）：界面上的中文从 .cs 搬进 config/ui_text/*.json，
# 源码只留 `UiStrings.<组>.<key>`。与上面 C5 同一域（文案只有一个正本），所以并在这张卡里，
# 不新开 check-d9.sh（10.1：脚本数不许再涨）。
# ════════════════════════════════════════════════════════════════════

echo "== 4. D9 正本与生成物自洽 =="
ROOT="$ROOT" python3 - > "$TMP/d9_static.txt" 2>&1 <<'PY'
import glob, json, os, re

root = os.environ["ROOT"]
cfg_dir = os.path.join(root, "config/ui_text")
out_dir = os.path.join(root, "client/MMOGame/Assets/HotUpdate/Ui/UiStrings")
client = os.path.join(root, "client/MMOGame/Assets/HotUpdate")


def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))


def read(p):
    return open(p, encoding="utf-8").read()


files = sorted(glob.glob(os.path.join(cfg_dir, "*.json")))
report("d9_config_registered", len(files) >= 70, "组数=%d" % len(files))

bad, total, groups = [], 0, []
for p in files:
    doc = json.load(open(p, encoding="utf-8"))
    name = os.path.basename(p)[:-5]
    if doc.get("schema") != "ui-text/1":
        bad.append(name + ":schema")
    if doc.get("group") != name:
        bad.append(name + ":group")
    if not (doc.get("source") or "").startswith("client/MMOGame/Assets/HotUpdate/"):
        bad.append(name + ":source")
    entries = doc.get("entries") or []
    if not entries:
        bad.append(name + ":empty")
    keys = [e.get("key") for e in entries]
    if len(keys) != len(set(keys)):
        bad.append(name + ":dup_key")
    for e in entries:
        if not e.get("text"):
            bad.append(name + ":empty_text")
            break
        if not re.fullmatch(r"T\d{3,4}", e.get("key") or ""):
            bad.append(name + ":key_format")
            break
    total += len(entries)
    groups.append(name)
report("d9_config_entries_clean", not bad, ";".join(bad[:6]))
report("d9_config_entries_floor", total >= 1300, "条数=%d" % total)

shards = sorted(glob.glob(os.path.join(out_dir, "*.cs")))
shard_groups = sorted(os.path.basename(s)[:-3] for s in shards)
report("d9_shards_match_config", shard_groups == sorted(groups),
       "多=%s 少=%s" % (sorted(set(shard_groups) - set(groups))[:4],
                       sorted(set(groups) - set(shard_groups))[:4]))

hand = []
for s, g in zip(shards, shard_groups):
    body = read(s)
    if "不要手改" not in body or ("config/ui_text/%s.json" % g) not in body:
        hand.append(g)
report("d9_shards_declare_generated", not hand, ";".join(hand[:6]))

# 表里有、屏上没用 = 孤儿：改文案时改了也不会生效。
refs = set()
for dirpath, dirnames, names in os.walk(client):
    dirnames[:] = [d for d in dirnames if d not in ("UiStrings", "Generated")]
    for n in names:
        if not n.endswith(".cs"):
            continue
        for m in re.finditer(r"UiStrings\.(\w+)\.(T\d{3,4})", read(os.path.join(dirpath, n))):
            refs.add(m.group(1) + "." + m.group(2))
orphan = []
for p in files:
    doc = json.load(open(p, encoding="utf-8"))
    g = doc["group"]
    for e in doc.get("entries") or []:
        if (g + "." + e["key"]) not in refs:
            orphan.append(g + "." + e["key"])
report("d9_entries_all_referenced", not orphan,
       "没人引用=%d 例=%s" % (len(orphan), ";".join(orphan[:5])))

print("INFO d9_groups=%d entries=%d refs=%d" % (len(groups), total, len(refs)))
print("PASS d9_static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
    INFO) printf '  ..   %s\n' "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/d9_static.txt"
if ! grep -q '^PASS d9_static_end' "$TMP/d9_static.txt"; then
  bad "D9 静态断言没跑完"; grep -vE '^(PASS|FAIL|INFO) ' "$TMP/d9_static.txt" | tail -6 | detail
fi

echo "== 5. D9 源码里没有手写中文字面量 =="
if OUTR="$(python3 tools/gen/gen-ui-text.py --report 2>&1)"; then
  ok "HotUpdate 下 0 处中文字面量（UiContentTable / UiStrings 除外）"
else
  bad "还有中文字面量没进表"
  printf '%s\n' "$OUTR" | head -8 | detail
fi

echo "== 6. D9 生成器重跑与提交的分片逐字一致 =="
if python3 tools/gen/gen-ui-text.py --out "$TMP/d9gen" >/dev/null 2>&1; then
  if diff -r --exclude='*.meta' "$TMP/d9gen" "$ROOT/client/MMOGame/Assets/HotUpdate/Ui/UiStrings" >"$TMP/d9diff.txt" 2>&1; then
    ok "生成器重跑逐字一致"
  else
    bad "生成物与正本不一致（手改过 / 忘了重跑）"
    head -6 "$TMP/d9diff.txt" | detail
  fi
else
  bad "生成器跑不起来"; detail < /dev/null
fi

echo "== 7. D9 改文案演练：只改临时 config，生成物跟着变 =="
BEFORE="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' config/ui_text/UiFlightScreen.json)"
mkdir -p "$TMP/d9root/config"
cp -R config/ui_text "$TMP/d9root/config/ui_text"
if ROOT="$TMP/d9root" TMPOUT="$TMP/d9mut" python3 - <<'PY' > "$TMP/d9mutate.txt" 2>&1
import json, os, subprocess, sys

root = os.environ["ROOT"]
out = os.environ["TMPOUT"]
path = os.path.join(root, "config/ui_text/UiFlightScreen.json")
doc = json.load(open(path, encoding="utf-8"))
first = doc["entries"][0]
old = first["text"]
first["text"] = "D9 演练：只在临时配置里"
doc["entries"].append({"key": "T900", "text": "D9 演练：新加的一条"})
with open(path, "w", encoding="utf-8") as f:
    json.dump(doc, f, ensure_ascii=False, indent=2)

subprocess.check_call([sys.executable, "tools/gen/gen-ui-text.py",
                       "--root", root, "--out", out], stdout=subprocess.DEVNULL)
body = open(os.path.join(out, "UiFlightScreen.cs"), encoding="utf-8").read()
assert "D9 演练：只在临时配置里" in body, "改过的那条没进生成物"
assert old not in body, "旧文案还在生成物里"
assert "D9 演练：新加的一条" in body, "新加的一条没进生成物"
print("ok")
PY
then
  ok "只改临时 config，生成物就换成了新文案（屏读的是这张表）"
else
  bad "D9 改文案演练失败"
  head -6 "$TMP/d9mutate.txt" | detail
fi
AFTER="$(python3 -c 'import hashlib,sys; print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' config/ui_text/UiFlightScreen.json)"
if [ "$BEFORE" = "$AFTER" ]; then
  ok "真 config/ui_text 一个字节都没动"
else
  bad "真 config/ui_text 被演练改动了"
fi

finish "$TITLE"
