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

## Status

- [x] `state_ring` — deterministic save-state ring
- [x] `ggpo_bridge` — libggpo session + callback glue
- [x] libggpo vendored
- [ ] FBNeo host layer (implements `GgpoBridgeHost`, calls `GgpoBridgeTick`)
- [ ] unified build (core + libggpo)
- [ ] gRPC control agent + command queue
