#!/usr/bin/env bash
# Wire the rollback_netcode modules into the pinned FBNeo checkout:
#   1. apply the source/build patches under patches/fbneo/
#   2. build librollbackfbneo.a
#
# After this, build FBNeo as usual:  cd fbneo && make mingw
#
# Idempotent: patches already applied are skipped.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"   # repo root
fbneo="${FBNEO:-$here/fbneo}"
patches="$here/rollback_netcode/patches/fbneo"

[ -d "$fbneo/src" ] || { echo "!! FBNeo not found at $fbneo — clone it there first."; exit 1; }

echo ":: applying patches to $fbneo"
for p in "$patches"/*.diff; do
	name="$(basename "$p")"
	if git -C "$fbneo" apply --reverse --check "$p" >/dev/null 2>&1; then
		echo "   - $name  (already applied, skipping)"
	elif git -C "$fbneo" apply --check "$p" >/dev/null 2>&1; then
		git -C "$fbneo" apply "$p"
		echo "   - $name  (applied)"
	else
		echo "!! $name does not apply cleanly — the FBNeo checkout may have drifted from FBNEO_PIN.txt"
		exit 1
	fi
done

echo ":: building librollbackfbneo.a"
make -C "$here/rollback_netcode/build" "$@"

echo ":: done.  now:  cd \"$fbneo\" && make mingw ${*:+$*}"
