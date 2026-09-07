# FBNeo patches

Small, reviewable diffs against the pinned upstream FBNeo tree
(`fbneo/`, commit in [`../../../FBNEO_PIN.txt`](../../../FBNEO_PIN.txt)).

Keep each patch minimal — a call site, an `#include`, a build hook — with the
real logic living in `rollback_netcode/`. That keeps `git subtree pull` /
upstream rebases cheap.

| patch | touches | purpose |
|-------|---------|---------|
| `0001-run-cpp-ggpo-tick.diff` | `src/burner/win32/run.cpp` | route the per-frame emulation step through `FbnHostRunFrame()` when a rollback session is active |

## Applying

```bash
cd fbneo
git apply ../rollback_netcode/patches/fbneo/0001-run-cpp-ggpo-tick.diff
```

(or `git apply -R` to revert). The working tree already has this applied if you
followed the session that created it.

## Still TODO (not in these patches)

- **Build wiring**: add `rollback_netcode/core/*.cpp`,
  `rollback_netcode/fbneo/*.cpp` and the libggpo sources to
  `makefile.vc` / `meson.build`, with the include paths from
  `fbneo_host.cpp`'s header comment.
- **Session setup UI / config**: something must call `FbnHostStart()` with a
  populated `FbnHostConfig` (menu item, command line, or the gRPC agent).
- **Audio pacing**: the live tick currently emits sound into whatever buffer
  `RunGetNextSound()` pointed `pBurnSoundOut` at. GGPO tick cadence vs DirectSound
  segment size still needs reconciling (drive the tick from `RunGetNextSound`,
  or run video-timed with a resampling ring).
