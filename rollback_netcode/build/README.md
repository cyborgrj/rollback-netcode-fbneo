# Build

Two steps: build our static lib, then build FBNeo with it linked in.

```
librollbackfbneo.a   <-- this Makefile: core + fbneo_host + libggpo
        |
        v  linked via patches/fbneo/0002-mingw-link-rollback.diff
   fbneo/  (make mingw)  -->  fbneo.exe
```

## Why a static lib (and not just more files in FBNeo's makefile)

FBNeo's MinGW build keys every object by **basename** across a flat `vpath`.
libggpo ships `main.cpp`, `log.cpp`, `poll.cpp`, `sync.cpp` … which collide with
FBNeo's own. Bundling libggpo + our code into `librollbackfbneo.a` sidesteps the
collision and keeps the FBNeo patch tiny (two lines in `makefile.mingw`).

## Prerequisites

The FBNeo Windows toolchain — MSYS2 with MinGW-w64:

```bash
pacman -S --needed make mingw-w64-i686-gcc      # 32-bit (default FBNeo target)
# or, for a 64-bit build:
pacman -S --needed make mingw-w64-x86_64-gcc
```

Use the matching shell (**MSYS2 MINGW32** for 32-bit, **MINGW64** for 64-bit) for
*both* this lib and FBNeo, so the ABI matches.

## Build

```bash
# from repo root, one shot (patches FBNeo + builds the lib):
./rollback_netcode/build/setup-fbneo.sh
#   ...BUILD_X64_EXE=1   to pass through for a 64-bit build

# then FBNeo itself:
cd fbneo && make mingw
```

Or by hand:

```bash
cd rollback_netcode/build
make                      # -> librollbackfbneo.a  (32-bit)
make BUILD_X64_EXE=1      # -> librollbackfbneo.a  (64-bit)
make clean

cd ../../fbneo
git apply ../rollback_netcode/patches/fbneo/0001-run-cpp-ggpo-tick.diff
git apply ../rollback_netcode/patches/fbneo/0002-mingw-link-rollback.diff
make mingw
```

### Overrides

| var | default | meaning |
|-----|---------|---------|
| `CXX` | `g++` | compiler (use the cross name when cross-building) |
| `AR` | `ar` | archiver |
| `FBNEO` | `../../fbneo` | path to the FBNeo checkout (for `burnint.h`) |
| `BUILD_X64_EXE` | unset | `-m64` instead of `-m32` |
| `CXXFLAGS` | `-O2 -pipe` | base flags (arch/std/defs are appended) |

## What the lib contains

| object(s) | from |
|-----------|------|
| `state_ring.o`, `ggpo_bridge.o`, `fbneo_host.o` | `rollback_netcode/core`, `rollback_netcode/fbneo` |
| `bitvector.o game_input.o input_queue.o log.o main.o poll.o sync.o timesync.o platform_windows.o` | `third_party/ggpo/src/lib/ggpo` |
| `udp.o udp_proto.o` | `.../lib/ggpo/network` |
| `p2p.o spectator.o synctest.o` | `.../lib/ggpo/backends` |

Unresolved symbols left for the final FBNeo link: `BurnAreaScan`, `BurnAcb`,
`BurnDrvFrame`, `BurnDrvGetInputInfo`, `pBurnDraw`, `pBurnSoundOut`,
`bBurnRunAheadFrame`, `bDrvOkay`, `bprintf` — all defined in FBNeo.

## Status / caveats

- **Not yet compiled end-to-end from this environment** (no MinGW toolchain
  here). The file lists, include paths and link order are derived from reading
  FBNeo's `makefile.mingw` + libggpo's `CMakeSources.cmake`; expect to nudge an
  include path or a flag on the first real run.
- `makefile.vc` (MSVC) is not wired yet — MinGW only for now.
- Still nothing *calls* `FbnHostStart()` — the exe will build and run exactly as
  stock FBNeo until a session is started (menu / CLI / gRPC agent — next step).
