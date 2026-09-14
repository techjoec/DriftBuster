#!/usr/bin/env bash
# Generates the runtime-r1 trees under $1/r1; run_parity.sh calls it with $1 = $PARITY_MULTI_SERVER_WORK after generate_multi_server_trees.
set -euo pipefail
W="$1/r1"
mkdir -p "$W"
# A dotdot through a symlink: link -> deep/sub, so link/../hostA is deep/hostA physically and hostA lexically.
mkdir -p "$W/dotdot/deep/sub" "$W/dotdot/deep/hostA" "$W/dotdot/hostA" "$W/dotdot/plain"
printf '[core]\nname = physical\n' > "$W/dotdot/deep/hostA/app.ini"
printf '[core]\nname = lexical\nextra = 1\n' > "$W/dotdot/hostA/app.ini"
printf '[core]\nname = plain\n' > "$W/dotdot/plain/app.ini"
ln -sfn deep/sub "$W/dotdot/link"
# Root links: a loop, a dangling link, a link to a directory.
mkdir -p "$W/links/target"
printf '[core]\nname = linked\n' > "$W/links/target/app.ini"
ln -sfn loop2 "$W/links/loop1"; ln -sfn loop1 "$W/links/loop2"
ln -sfn nowhere "$W/links/dangling"
ln -sfn target "$W/links/dirlink"
# Special roots: a FIFO root and a socket root.
mkdir -p "$W/special"
mkfifo "$W/special/pipe-root"
python3 -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$W/special/socket-root"
# Special entries inside a tree: link loop, link to a fifo, link to the parent directory, fifo.
mkdir -p "$W/inner/hostA" "$W/inner/hostB"
printf '[core]\nname = inner\n' > "$W/inner/hostA/app.ini"
printf '[core]\nname = inner2\n' > "$W/inner/hostB/app.ini"
mkfifo "$W/inner/hostA/fifo.ini"
ln -sfn fifo.ini "$W/inner/hostA/fifo-link.ini"
ln -sfn selfloop "$W/inner/hostA/selfloop"
ln -sfn .. "$W/inner/hostA/parent"
ln -sfn ../hostB/app.ini "$W/inner/hostA/outside.ini"
# Unicode names (Unicode 16 letter U+1C89, No, astral, fullwidth tilde, combining mark).
for h in hostA hostB; do
  mkdir -p "$W/unicode/$h"
  i=0
  for n in $'\u1c89' $'\u00b2x' $'e\u0301' $'\U0001F600' $'\uff5e' 'Zed' $'\u2169' $'\u1c89\u1c8a' $'A\u0130'; do
    i=$((i+1))
    printf '[core]\nname = "n%s"\nhost = "%s"\n' "$i" "$([[ $h == hostA ]] && echo a || echo b)" > "$W/unicode/$h/$n.ini"
  done
done
# Encodings: invalid UTF-8 sequences, CRLF, lone CR, NEL, U+2028, sequences split at 8192.
for h in hostA hostB; do
  mkdir -p "$W/enc/$h"
  printf '[core]\r\nname = crlf\r\nhost = %s\r\n' "$h" > "$W/enc/$h/crlf.ini"
  printf '[core]\rname = cr\rhost = %s\r' "$h" > "$W/enc/$h/cr.ini"
  printf '[core]\nname = nel\xc2\x85x\nsep = a\xe2\x80\xa8b\nhost = %s\n' "$h" > "$W/enc/$h/nel.ini"
  printf '[core]\nbad1 = \xed\xa0\x80\nbad2 = \xc0\x80\nbad3 = \xf4\x90\x80\x80\nbad4 = \xe0\x80\nlatin = caf\xe9\nhost = %s\ntail = \xf0\x9f\x98' "$h" > "$W/enc/$h/invalid.ini"
  printf '<?xml version="1.0"?>\r<configuration><appSettings><add key="a" value="caf\xe9 \xc0\x80 \xed\xa0\x80 %s"/>\r</appSettings></configuration>\r' "$h" > "$W/enc/$h/web.config"
  printf '\xef\xbb\xbf<?xml version="1.0"?>\n<configuration><appSettings><add key="a" value="x\xc2\x85y\xe2\x80\xa8z %s"/>\r\n</appSettings></configuration>\n\xf0\x9f' "$h" > "$W/enc/$h/app.config"
  python3 - "$W/enc/$h/split.ini" "$h" <<'PY'
import sys
path, host = sys.argv[1], sys.argv[2]
head = b"[core]\nhost = " + host.encode() + b"\npad = "
body = head + b"a" * (8191 - len(head) - 0)
body = body[:8191] + b"\xf0\x9fx\r"   # invalid sequence straddles 8192 and a CR at the edge
body += b"\nmore = 1\r" + b"b" * (16384 - len(body) - 10) + b"\r\nlast = " + host.encode() + b"\n"
open(path, "wb").write(body)
PY
done
# Undecodable file name (Linux bytes 0xFF).
mkdir -p "$W/badname/hostA" "$W/badname/hostB"
printf '[core]\nname = bad\n' > "$W/badname/hostA/$(printf 'bad\xff').ini"
printf '[core]\nname = good\n' > "$W/badname/hostA/good.ini"
printf '[core]\nname = good\n' > "$W/badname/hostB/good.ini"
# Budget: a first root with a big file, a second root that is never reached.
mkdir -p "$W/budget/root0" "$W/budget/root1"
awk 'BEGIN { print "[settings]"; for (n = 1; n <= 400; n++) printf "k%04d = v%04d\n", n, n }' > "$W/budget/root0/a.ini"
printf '[core]\nname = second\n' > "$W/budget/root0/b.ini"
printf 'not config at all \x00\x01\x02' > "$W/budget/root0/0-binary.bin"
printf '[core]\nname = never\n' > "$W/budget/root1/c.ini"
# Odd names.
mkdir -p "$W/oddnames/hostA" "$W/oddnames/hostB"
for h in hostA hostB; do
  printf '[core]\nname = back\nh = %s\n' "$h" > "$W/oddnames/$h/a\\b.ini"
  printf '[core]\nname = nl\nh = %s\n' "$h" > "$W/oddnames/$h/$(printf 'new\nline').ini"
  printf '[core]\nname = hash\n' > "$W/oddnames/$h/#x:y?.ini"
  printf '[core]\nname = dot\n' > "$W/oddnames/$h/.hidden.ini"
  printf '[core]\nname = sp\n' > "$W/oddnames/$h/ lead space .ini"
done
# Secrets under a dotdot root and a link root.
mkdir -p "$W/secrets/real"
printf 'server host=secret.corp.local\n[core]\nname = s\n' > "$W/secrets/real/app.ini"
ln -sfn real "$W/secrets/link"
