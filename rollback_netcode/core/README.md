# `state_ring` — pinned circular save-state pool

The deterministic memory layer under the libggpo integration.

## Why it exists

Rollback netcode re-simulates the recent past every time a remote input
arrives late. To do that it must, on the emulation thread:

1. **Save** the full volatile machine state once per simulated frame.
2. **Load** any of the last ~8 frames instantly, then fast-forward.

Doing that with `malloc`/`free` per frame (what FBNeo's `BurnStateCompress`
and the libggpo `vectorwar` sample both do) causes heap fragmentation and
frame-time spikes. `state_ring` allocates **one** contiguous pool at init and
never touches the heap again while the match runs.

```
pool  = one malloc:  [ slot 0 ][ slot 1 ][ slot 2 ] ... [ slot N-1 ]
                        each slot = full volatile state, fixed size
slot(frame) = frame & (N - 1)          // N is a power of two (default 16)
```

Because libggpo never rolls back further than its prediction window (8) and
never stores two states for the same frame number, `frame & (N-1)` only ever
overwrites a frame that is already too old to matter.

## What it captures

Exactly FBNeo's RunAhead volatile set: `BurnAreaScan(ACB_FULLSCAN | ACB_RUNAHEAD, …)`.
That set is already proven to re-simulate deterministically — RunAhead does it
every frame today. `state_ring` is a straight N-slot generalisation of
`StateRunAheadSave` / `StateRunAheadLoad` in `fbneo/src/burn/burn.cpp`.

## API

See [`state_ring.h`](state_ring.h). Shapes onto libggpo's `GGPOSessionCallbacks`:

| libggpo callback   | maps to               |
|--------------------|-----------------------|
| `save_game_state`  | `StateRingSave`       |
| `load_game_state`  | `StateRingLoad`       |
| `free_buffer`      | `StateRingFree` (no-op) |

`StateRingInit` must be called **after** the driver has run its first frame,
so the measured state size is stable; the size is then locked for the session
(a mid-session change returns `STATE_RING_ERR_SIZE_DRIFT` rather than
reallocating, which would corrupt in-flight rollbacks).

All calls run on the emulation thread only — they borrow the global `BurnAcb`
pointer, exactly as RunAhead / Rewind / `BurnStateSave` do.

## Not done yet

- Not added to any FBNeo makefile / meson build.
- No `StateRingInit` call site wired into the frame loop.

---

# `ggpo_bridge` — libggpo session + callback glue

[`ggpo_bridge.h`](ggpo_bridge.h) / [`ggpo_bridge.cpp`](ggpo_bridge.cpp).

Owns the `GGPOSession`, the player handles, and all 7 `GGPOSessionCallbacks`.
Depends on `state_ring` for save/load/free, and on a small **host interface**
(`GgpoBridgeHost`) so the file never names an FBNeo symbol — it stays
unit-testable and reusable from the gRPC agent.

```
                 GgpoBridgeTick()  (once per rendered frame, emu thread)
                         |
   ggpo_idle -> poll_local_input -> ggpo_add_local_input
                         |
                    stepOnce(bRollback=0)
                         |
   ggpo_synchronize_input -> host.step_frame(..., bRollback=0) -> ggpo_advance_frame
                         |
        (if a late input arrived, libggpo now calls back, synchronously:)
   cb_load_game_state  -> StateRingLoad
   cb_advance_frame    -> stepOnce(bRollback=1)   x (frames to replay)
   cb_save_game_state  -> StateRingSave           (re-save each replayed frame)
```

libggpo callback -> bridge mapping:

| callback           | bridge                                            |
|--------------------|---------------------------------------------------|
| `begin_game`       | `return true`                                     |
| `save_game_state`  | `StateRingSave`                                   |
| `load_game_state`  | `StateRingLoad`                                   |
| `free_buffer`      | `StateRingFree` (no-op)                           |
| `advance_frame`    | `stepOnce(bRollback=1)` — silent re-simulation    |
| `on_event`         | translated to `GgpoBridgeEvent` -> `host.on_event`|
| `log_game_state`   | `host.log_state` if provided, else `return true`  |

### Host contract

- `poll_local_input(out, nBytes, user)` — this machine's input for the frame.
- `step_frame(syncInputs, nPlayers, disconnectFlags, bRollback, user)` — run
  **exactly one** deterministic core frame. When `bRollback == 1`: **no draw,
  no audio**. Return 0 on success.
- `StateRingInit()` must have run before `GgpoBridgeStart()` (host warms the
  driver one frame first). The bridge does a loud late-init fallback but that
  path risks a size probe before the driver is stable.

### Offline determinism check

`GgpoBridgeStartSyncTest(cfg, host, nCheckDistance)` runs libggpo's synctest:
no sockets, saves every frame, rolls back `nCheckDistance` and compares the
`state_ring` checksums. Run this in CI per driver before trusting a game.

### Build deps

Needs libggpo's `ggponet.h` on the include path and the libggpo lib linked.
libggpo will be vendored under `rollback_netcode/third_party/ggpo/` (submodule).

### Threading

Everything here runs on the **emulation thread**. The gRPC agent posts commands
to a queue the emu thread drains between ticks; it must not call these directly.
