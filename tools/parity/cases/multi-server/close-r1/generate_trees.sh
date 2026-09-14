#!/usr/bin/env bash
# Generates the close-r1 trees under $1/close-r1 (call with $1 = $PARITY_MULTI_SERVER_WORK). Names that are not UTF-8 cannot be
# committed, so every tree is generated.
set -euo pipefail
W="$1/close-r1"
ff="$(printf '\xff')"
fffd="$(printf '\xef\xbf\xbd')"
mkdir -p "$W"

# A baseline host.
mkdir -p "$W/base"
printf '[core]\nname = base\n' > "$W/base/app.ini"

# links/link -> undecodable/<0xFF>: a root whose physical path is not UTF-8 (Python's root.resolve() yields '\udcff').
mkdir -p "$W/undecodable/$ff/sub" "$W/links"
printf '[core]\nname = bytes\n' > "$W/undecodable/$ff/app.ini"
printf '[core]\nname = bytes-sub\n' > "$W/undecodable/$ff/sub/sub.ini"
ln -s "$W/undecodable/$ff" "$W/links/link"

# links/dotdot -> dotdot/<0xFF>, next to dotdot/x: the root links/dotdot/../x is dotdot/x for the kernel and for Python.
mkdir -p "$W/dotdot/$ff" "$W/dotdot/x"
printf '[core]\nname = real\n' > "$W/dotdot/x/app.ini"
ln -s "$W/dotdot/$ff" "$W/links/dotdot"

# links/trap -> trap/<0xFF>, where trap/U+FFFD is a symlink to elsewhere/deep: the U+FFFD spelling of the link target leads to
# elsewhere/x, a different directory holding a different app.ini.
mkdir -p "$W/trap/$ff" "$W/trap/x" "$W/elsewhere/deep" "$W/elsewhere/x"
printf '[core]\nname = real\n' > "$W/trap/x/app.ini"
printf '[core]\nname = guessed\n' > "$W/elsewhere/x/app.ini"
ln -s "$W/elsewhere/deep" "$W/trap/$fffd"
ln -s "$W/trap/$ff" "$W/links/trap"

# mixed/<0xFF> and mixed/U+FFFD next to mixed/good: a '\udcff' root over an existing byte-0xFF directory, beside a valid root.
mkdir -p "$W/mixed/$ff" "$W/mixed/$fffd" "$W/mixed/good"
printf '[core]\nname = bytes\n' > "$W/mixed/$ff/bytes.ini"
printf '[core]\nname = guessed\n' > "$W/mixed/$fffd/guessed.ini"
printf '[core]\nname = good\n' > "$W/mixed/good/app.ini"
