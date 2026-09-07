# `fbneo_host` — FBNeo ↔ rollback core integration

The one module allowed to know both FBNeo and the rollback core. Implements
`GgpoBridgeHost` in concrete FBNeo terms.

[`fbneo_host.h`](fbneo_host.h) / [`fbneo_host.cpp`](fbneo_host.cpp)

## What it does

### 1. Deterministic input map (built at `FbnHostStart`)

Walks the active driver's input list via `BurnDrvGetInputInfo` in its natural
order. Every `BIT_DIGITAL` input named `P<n> …` is assigned the next bit index
for player `n`. Result: a compact little-endian bitmask per player,
`ceil(maxBits/8)` bytes wide (uniform across players — libggpo uses one
`input_size`). The list order is identical on both peers for a given build, so
the mapping agrees implicitly — nothing is negotiated.

- **Analog / constant inputs** — logged once, left unsynchronised (v1 targets
  digital fighting games).
- **Genuine common inputs** (Reset, Service, Diagnostic) — not transmitted.
- **DIP switches** — match-static; they live in the save state, not the
  per-frame input. Both peers must select the same DIPs before `FbnHostStart`.

### 2. `GgpoBridgeHost` callbacks

| callback | does |
|----------|------|
| `poll_local_input` | packs the local player's slice of the driver bytes (already filled by `GetInput(true)`) into the bitmask |
| `step_frame` | writes libggpo's synchronized inputs for **all** players back into the driver bytes, then `BurnDrvFrame()`. When `bRollback`: nulls `pBurnDraw`/`pBurnSoundOut` and sets `bBurnRunAheadFrame` first — the exact silent-frame recipe from FBNeo's RunAhead |
| `on_event` | logs via `bprintf` (TODO: forward to the gRPC agent) |

### 3. Lifecycle + per-frame entry point

```c
FbnHostStart(&cfg);            // build map + GgpoBridgeStart  (driver must be initialised)
FbnHostStartSyncTest(&cfg, n); // offline determinism check
FbnHostRunFrame();             // once per frame from RunFrame(); 1=advanced 0=catching-up <0=fatal
FbnHostStop();
```

`StateRingInit` is **deferred** to the first `FbnHostRunFrame()` — by then the
driver has executed a frame and its volatile-state size is stable to lock in.

## Wiring into FBNeo

One patch: [`../patches/fbneo/0001-run-cpp-ggpo-tick.diff`](../patches/fbneo/0001-run-cpp-ggpo-tick.diff)
routes the per-frame step through `FbnHostRunFrame()` in
`RunFrame()` when `FbnHostIsActive()`. Everything else (Kaillera, replay,
RunAhead, Rewind) is untouched and mutually exclusive with an active session.

Still needed: build wiring (add `core/*.cpp`, `fbneo/*.cpp`, libggpo sources +
include paths to `makefile.vc` / `meson.build`), a caller for `FbnHostStart`
(menu / CLI / gRPC agent), and audio-pacing reconciliation.

## Boundary

`fbneo_host.cpp` includes `burnint.h` and our two core headers — nothing from
the win32 burner. The only burner-level symbol it uses is `bDrvOkay`, re-declared
locally (same as `burn/cheat.cpp` does). Keeps it close to buildable for the
SDL burner too.
