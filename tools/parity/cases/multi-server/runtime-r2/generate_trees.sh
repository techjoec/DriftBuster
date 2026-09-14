#!/usr/bin/env bash
# Generates the runtime-r2 trees under $1/r2 (call with $1 = $PARITY_MULTI_SERVER_WORK, after runtime-r1/generate_trees.sh).
set -euo pipefail
W="$1/r2"
mkdir -p "$W"

# Registry manifests whose copied options hold special floats, big integers and keys that sort differently by code point and
# by UTF-16 unit (cache JSON is sort_keys + ensure_ascii=False, so the cache digest compares the order and the spelling).
for h in hostA hostB; do
  mkdir -p "$W/regmeta/$h"
  printf '{"registry_scan": {"token": "tok-%s", "max_depth": {"\\uffff": 1, "\\ud83d\\ude00": 2, "\\u00e9": 3, "a": NaN, "B": -Infinity}, "max_hits": 123456789012345678901234567890, "time_budget_s": 1e16}}\n' "$h" > "$W/regmeta/$h/a.regscan.json"
  printf '{"registry_scan": {"token": "t2", "max_depth": -0.0, "max_hits": 0.30000000000000004, "time_budget_s": 1.5e-7, "keywords": ["x"]}}\n' > "$W/regmeta/$h/b.regscan.json"
  printf '{"registry_scan": {"token": "t3", "max_depth": [1, [2, {"z": 1e-320, "y": 5e-324}]], "max_hits": true, "time_budget_s": null}}\n' > "$W/regmeta/$h/c.registry.json"
done
printf '{"registry_scan": {"token": "tok-hostB", "max_depth": {"\\uffff": 1, "\\ud83d\\ude00": 2}, "max_hits": 7, "time_budget_s": 2.5}}\n' > "$W/regmeta/hostB/a.regscan.json"

# Duplicate host ids over two roots that share relative paths (cache entries overwritten inside one run).
mkdir -p "$W/dupcache/one" "$W/dupcache/two"
printf '[core]\nname = one\n' > "$W/dupcache/one/app.ini"
printf '{"Server": "one.corp.local"}\n' > "$W/dupcache/one/app.json"
printf '[core]\nname = two\n' > "$W/dupcache/two/app.ini"
printf '{"Server": "one.corp.local"}\n' > "$W/dupcache/two/app.json"

# Budget trees: files of known sizes (each ini is 76 bytes).
mkdir -p "$W/budget/r0" "$W/budget/r1"
for n in 1 2 3; do
  printf '[core]\nname = budget-file-%s\npad = %041d\n' "$n" 0 > "$W/budget/r0/f$n.ini"
  printf '[core]\nname = budget-file-%s\npad = %041d\n' "$n" 0 > "$W/budget/r1/f$n.ini"
done

# UTF-16 / UTF-32 content, one past the 128 KiB sample, odd byte count, CR-only breaks.
for h in hostA hostB; do
  mkdir -p "$W/wide/$h"
  python3 - "$W/wide/$h" "$h" <<'PY'
import sys
out, host = sys.argv[1], sys.argv[2]
body = "[settings]\r\n" + "".join(f"key_{n:05d} = value \U0001F600 {n}\r\n" for n in range(6000)) + f"tail = {host}\r\n"
open(f"{out}/big-le.ini", "wb").write(b"\xff\xfe" + body.encode("utf-16-le"))
open(f"{out}/be.ini", "wb").write(("[core]\rname = be\rhost = " + host + "\r").encode("utf-16-be"))
open(f"{out}/odd.json", "wb").write(b"\xff\xfe" + ('{"host": "' + host + '"}').encode("utf-16-le") + b"\x00")
open(f"{out}/u32.xml", "wb").write(b"\xff\xfe\x00\x00" + ('<?xml version="1.0"?><configuration><appSettings><add key="h" value="' + host + '"/></appSettings></configuration>').encode("utf-32-le"))
PY
done

# Past the 256 KiB canonical clamp and the 600-line diff clamp, drifting at both ends.
for h in hostA hostB; do
  mkdir -p "$W/clamp/$h"
  awk -v h="$h" 'BEGIN { print "[settings]"; print "head = " h; for (n = 1; n <= 12000; n++) printf "setting_%06d = value-%06d\n", n, (h == "hostB" && n % 50 == 0) ? n + 1 : n; print "tail = " h }' > "$W/clamp/$h/huge.ini"
done

# Degenerate files.
for h in hostA hostB; do
  mkdir -p "$W/degenerate/$h"
  : > "$W/degenerate/$h/empty.json"
  printf '\xef\xbb\xbf' > "$W/degenerate/$h/bom-only.xml"
  printf '\r' > "$W/degenerate/$h/cr-only.ini"
  printf '[core]\nname = nul\x00byte\nhost = %s\n' "$h" > "$W/degenerate/$h/nul.conf"
  printf '\xef\xbb\xbf{"Server": "%s"}\r\n' "$h" > "$W/degenerate/$h/bom.json"
done

# Unreadable files interleaved with a tight budget, special files first in walk order, and an unsearchable subdirectory.
mkdir -p "$W/lockbudget/host/sub"
printf '[core]\nname = a-open\npad = %041d\n' 0 > "$W/lockbudget/host/a-open.ini"
printf '[core]\nname = b-lock\npad = %041d\n' 0 > "$W/lockbudget/host/b-lock.ini"
printf '[core]\nname = c-open\npad = %041d\n' 0 > "$W/lockbudget/host/c-open.ini"
printf '[core]\nname = d-lock\npad = %041d\n' 0 > "$W/lockbudget/host/d-lock.ini"
printf '[core]\nname = e-open\npad = %041d\n' 0 > "$W/lockbudget/host/e-open.ini"
printf '[core]\nname = hidden\n' > "$W/lockbudget/host/sub/f-hidden.ini"
mkfifo "$W/lockbudget/host/0-fifo.ini"
ln -sfn /dev/zero "$W/lockbudget/host/0-zero.ini"
python3 -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$W/lockbudget/host/0-sock.ini"

# Error mapping in one run: locked root dir, FIFO root, missing root, unsearchable parent, a /dev/zero link root, a good host.
mkdir -p "$W/errmap/locked" "$W/errmap/good" "$W/errmap/sealed/inner" "$W/errmap/partial/sub"
printf '[core]\nname = locked\n' > "$W/errmap/locked/app.ini"
printf '[core]\nname = good\n' > "$W/errmap/good/app.ini"
printf '{"Server": "good.corp.local", "password": "hunter2"}\n' > "$W/errmap/good/secret.json"
printf '[core]\nname = sealed\n' > "$W/errmap/sealed/inner/app.ini"
printf '[core]\nname = partial\n' > "$W/errmap/partial/app.ini"
printf '[core]\nname = refused\n' > "$W/errmap/partial/sub/refused.ini"
printf '{"Server": "good.corp.local", "password": "hunter2"}\n' > "$W/errmap/partial/secret.json"
mkfifo "$W/errmap/fifo-root"
# A root directory that can be listed but not searched (mode 0644), a link to it and a link to the mode-000 root.
mkdir -p "$W/errmap/listonly/sub"
printf '[core]\nname = listonly\n' > "$W/errmap/listonly/app.ini"
printf '[core]\nname = listonly-sub\n' > "$W/errmap/listonly/sub/deep.ini"
ln -sfn listonly "$W/errmap/listonly-link"
ln -sfn locked "$W/errmap/locked-link"
ln -sfn /dev/zero "$W/errmap/zero-root"

if [[ "$(id -u)" != "0" ]]; then
  chmod 000 "$W/lockbudget/host/b-lock.ini" "$W/lockbudget/host/d-lock.ini" "$W/errmap/locked"
  chmod 644 "$W/lockbudget/host/sub" "$W/errmap/partial/sub" "$W/errmap/listonly/sub" "$W/errmap/listonly"
  chmod 000 "$W/errmap/sealed"
fi
