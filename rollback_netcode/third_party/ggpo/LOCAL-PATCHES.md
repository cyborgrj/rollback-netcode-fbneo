# Local patches to vendored libggpo

Kept small and listed here so a `git subtree pull` conflict is easy to resolve.

## 1. `src/lib/ggpo/types.h` — build with MinGW-w64

pond3r/ggpo's `types.h` selects the platform header with `#if defined(_WINDOWS)`
— a define set by MSVC / Visual Studio, **not** by MinGW-w64 (which sets
`_WIN32`). On MinGW it therefore fell through to `platform_linux.h`
(`getpid()`, no `GetConfigBool`, …) and failed to compile.

Change:

```c
#if defined(_WINDOWS) || defined(_WIN32)
#  include "platform_windows.h"
```

This project builds FBNeo (and this lib) with MinGW-w64, so `_WIN32` must work.

## 2. `src/lib/ggpo/network/udp_proto.h` — GCC rejects a redundant ctor qualifier

Line 56 declared the nested `Event` constructor as `UdpProtocol::Event(Type t = Unknown)`.
MSVC tolerates the extra `UdpProtocol::` qualifier; GCC fails to parse it while
`UdpProtocol` is still an incomplete type (`expected ')' before 't'`). Dropped the
qualifier:

```c
Event(Type t = Unknown) : type(t) { }
```

