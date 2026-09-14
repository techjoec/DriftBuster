#!/usr/bin/env bash
# Generates the runtime-r3 trees under $1/r3 (call with $1 = $PARITY_MULTI_SERVER_WORK, after runtime-r2/generate_trees.sh).
set -euo pipefail
W="$1/r3"
mkdir -p "$W"

# Plain hosts: two ini files and a json with a secret, drifting between hosts.
for h in hostA hostB hostC; do
  mkdir -p "$W/plain/$h"
  printf '[core]\nname = plain\nhost = %s\n' "$h" > "$W/plain/$h/app.ini"
  printf '{"Server": "%s.corp.local", "Port": 8080}\n' "$h" > "$W/plain/$h/app.json"
done
printf '{"Server": "hostA.corp.local", "password": "hunter2"}\n' > "$W/plain/hostA/secret.json"

# Unlistable and unsearchable subdirectories holding secrets, next to readable files with and without secrets.
mkdir -p "$W/subdirs/host/sealed" "$W/subdirs/host/writeonly" "$W/subdirs/host/open" "$W/subdirs/other"
printf '{"Server": "sealed.corp.local", "password": "hunter2"}\n' > "$W/subdirs/host/sealed/secret.json"
printf '{"Server": "writeonly.corp.local", "password": "hunter2"}\n' > "$W/subdirs/host/writeonly/secret.json"
printf '{"Server": "open.corp.local", "password": "hunter2"}\n' > "$W/subdirs/host/open/secret.json"
printf '[core]\nname = subdirs\n' > "$W/subdirs/host/app.ini"
printf '{"Server": "open.corp.local", "password": "hunter2"}\n' > "$W/subdirs/other/secret.json"
mkdir -p "$W/subdirs/other/open"
printf '{"Server": "open.corp.local", "password": "hunter2"}\n' > "$W/subdirs/other/open/secret.json"
printf '[core]\nname = other\n' > "$W/subdirs/other/app.ini"

# Locked roots reached through odd spellings and links: a mode-000 file, a mode-000 directory, links to both.
mkdir -p "$W/locks/dir" "$W/locks/good"
printf '{"Server": "locked-file.corp.local"}\n' > "$W/locks/file.json"
printf '[core]\nname = locked-dir\n' > "$W/locks/dir/app.ini"
printf '[core]\nname = good\n' > "$W/locks/good/app.ini"
ln -sfn file.json "$W/locks/link-to-file"
ln -sfn dir "$W/locks/link-to-dir"

# Chained relative links with .. parts: a -> b/../c/d, b -> real/sub, c -> real/c-target; the fingerprint follows the kernel.
mkdir -p "$W/chain/real/sub" "$W/chain/real/c-target/d" "$W/chain/c-target-lexical/d"
printf '[core]\nname = physical\n' > "$W/chain/real/c-target/d/app.ini"
printf '[core]\nname = lexical\n' > "$W/chain/c-target-lexical/d/app.ini"
ln -sfn real/sub "$W/chain/b"
ln -sfn c-target "$W/chain/real/c"
ln -sfn c-target-lexical "$W/chain/c"
ln -sfn b/../c/d "$W/chain/a"
ln -sfn "$W/chain/a" "$W/chain/abs-to-a"

# Slug collisions under three roots (the middle root is missing in the plan).
for r in r0 r2; do
  mkdir -p "$W/slugs/$r"
  for n in 'a b' 'a+b' 'a-b'; do
    printf '[core]\nname = %s\nroot = %s\n' "$n" "$r" > "$W/slugs/$r/$n.ini"
  done
done

# Secrets past and straddling the hunt's 128 KiB sample, in files the detector samples as ini.
for h in hostA hostB; do
  mkdir -p "$W/hunt-sample/$h"
  python3 - "$W/hunt-sample/$h" "$h" <<'PY'
import sys
out, host = sys.argv[1], sys.argv[2]
line = "setting_{:06d} = value\n"
body = "[core]\nhost = " + host + "\n"
n = 0
while len(body.encode()) < 128 * 1024 - 20:
    body += line.format(n); n += 1
straddle = body + "password = hunter2secret\n"
past = body + "x" * 64 + "\npassword = hunter2secret\n"
open(f"{out}/straddle.ini", "w").write(straddle)
open(f"{out}/past.ini", "w").write(past)
open(f"{out}/inside.ini", "w").write("[core]\npassword = hunter2secret\nhost = " + host + "\n")
PY
done

# Hosts whose only files are unreadable, unmatched or empty, and an empty directory root.
mkdir -p "$W/zero/locked-only" "$W/zero/binary-only" "$W/zero/empty-dir" "$W/zero/mixed/sub"
printf '[core]\nname = locked-only\n' > "$W/zero/locked-only/app.ini"
printf '\x00\x01\x02\x03\xff\xfe\x00\x00binary' > "$W/zero/binary-only/blob.bin"
printf '[core]\nname = mixed\n' > "$W/zero/mixed/app.ini"
printf '[core]\nname = mixed-locked\n' > "$W/zero/mixed/locked.ini"
printf '{"Server": "mixed.corp.local", "password": "hunter2"}\n' > "$W/zero/mixed/sub/secret.json"

# Glob metacharacters in root and directory names, a directory literally named ** and *, hidden directories.
for h in '[ab]' 'st*r' 'q?'; do
  mkdir -p "$W/meta/$h/[x]" "$W/meta/$h/**" "$W/meta/$h/*" "$W/meta/$h/.hidden-dir"
  printf '[core]\nname = bracket\nh = %s\n' "$h" > "$W/meta/$h/[x]/app.ini"
  printf '[core]\nname = starstar\n' > "$W/meta/$h/**/app.ini"
  printf '[core]\nname = star\nh = %s\n' "$h" > "$W/meta/$h/*/app.ini"
  printf '[core]\nname = hidden\n' > "$W/meta/$h/.hidden-dir/app.ini"
  printf '{"Server": "meta.corp.local", "password": "hunter2"}\n' > "$W/meta/$h/.hidden-dir/.secret.json"
done
mkdir -p "$W/meta/a"
printf '[core]\nname = literal-a\n' > "$W/meta/a/app.ini"

if [[ "$(id -u)" != "0" ]]; then
  chmod 000 "$W/subdirs/host/sealed" "$W/locks/file.json" "$W/locks/dir" "$W/zero/locked-only/app.ini" "$W/zero/mixed/locked.ini"
  chmod 300 "$W/subdirs/host/writeonly"
fi
