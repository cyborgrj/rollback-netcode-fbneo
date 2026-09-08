#!/usr/bin/env bash
# Wire the rollback_netcode modules into the pinned FBNeo checkout:
#   1. apply the source/build patches under patches/fbneo/
#   2. build librollbackfbneo.a
#
# After this, build FBNeo as usual:  cd fbneo && make mingw
#
# Idempotent: patches already applied are skipped. Tolerant of CRLF/LF
# differences (FBNeo sources are CRLF, the .diff files are LF) via
# --ignore-whitespace, and never hard-fails on a patch whose state it cannot
# verify - a later `make` is a clearer signal than blocking here.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"   # repo root
fbneo="${FBNEO:-$here/fbneo}"
patches="$here/rollback_netcode/patches/fbneo"

[ -d "$fbneo/src" ] || { echo "!! FBNeo not found at $fbneo — clone it there first."; exit 1; }

GA=(git -C "$fbneo" apply --ignore-whitespace)

echo ":: applying patches to $fbneo"
for p in "$patches"/*.diff; do
	name="$(basename "$p")"
	if "${GA[@]}" --reverse --check "$p" >/dev/null 2>&1; then
		echo "   - $name  (already applied, skipping)"
	elif "${GA[@]}" --check "$p" >/dev/null 2>&1; then
		"${GA[@]}" "$p"
		echo "   - $name  (applied)"
	else
		echo "   - $name  (state unclear — leaving the tree as-is; verify with 'cd fbneo && git status')"
	fi
done

echo ":: building librollbackfbneo.a"
make -C "$here/rollback_netcode/build" "$@"

echo ":: done.  now:  cd \"$fbneo\" && make mingw ${*:+$*}"
