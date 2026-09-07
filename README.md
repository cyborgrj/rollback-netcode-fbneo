# Rollback Netcode for FinalBurn Neo

Integrating [libggpo](https://github.com/pond3r/ggpo) rollback netcode into the
FinalBurn Neo core, driven by a gRPC control agent.

Design priorities: **determinism**, **strict save-state memory management**
(fixed circular buffers, zero per-frame heap allocation), and safe use of the
FBNeo state API (`BurnStateSave` / `BurnStateLoad`, `BurnAreaScan`).

## Layout

```
rollback_netcode/
  core/                    our code
    state_ring.{h,cpp}     pinned N-slot circular save-state pool
    ggpo_bridge.{h,cpp}    GGPOSession + the 7 GGPO callbacks
    README.md              module docs
  third_party/
    ggpo/                  libggpo, vendored via `git subtree` (see below)

fbneo/                     upstream FBNeo checkout — NOT tracked here
                           (its own git repo; pinned in FBNEO_PIN.txt)
```

## FBNeo

`fbneo/` is a separate upstream checkout, git-ignored. The exact commit it must
build against is recorded in [`FBNEO_PIN.txt`](FBNEO_PIN.txt).

## Vendored libggpo

`rollback_netcode/third_party/ggpo/` is pulled in with `git subtree` so a plain
`git clone` of this repo gets everything, and local patches to libggpo are
possible.

Update it later with:

```bash
git subtree pull --prefix rollback_netcode/third_party/ggpo \
  https://github.com/pond3r/ggpo master --squash
```

## License

This project's own code is MIT — see [`LICENSE`](LICENSE). Vendored libggpo
under `rollback_netcode/third_party/ggpo/` is MIT
([its LICENSE](rollback_netcode/third_party/ggpo/LICENSE)). FBNeo itself is not
redistributed here — only a small patch under `rollback_netcode/patches/fbneo/`;
building requires your own FBNeo checkout under its own license.

## Status

- [x] `state_ring` — deterministic save-state ring
- [x] `ggpo_bridge` — libggpo session + callback glue
- [x] libggpo vendored
- [x] `fbneo_host` — implements `GgpoBridgeHost` (input map, step_frame, lifecycle)
- [x] `run.cpp` patch — routes the per-frame step through `FbnHostRunFrame()`
- [ ] unified build (core + fbneo_host + libggpo → makefile.vc / meson)
- [ ] a caller for `FbnHostStart` (menu / CLI / gRPC agent)
- [ ] audio-pacing reconciliation (GGPO tick vs DirectSound segments)
- [ ] gRPC control agent + command queue
