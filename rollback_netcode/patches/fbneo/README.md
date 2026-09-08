# Patches do FBNeo

Diffs pequenos e revisáveis contra a árvore do FBNeo upstream fixada
(`fbneo/`, commit em [`../../../FBNEO_PIN.txt`](../../../FBNEO_PIN.txt)).

Mantenha cada patch mínimo — um ponto de chamada, um `#include`, um hook de
build — com a lógica de verdade morando em `rollback_netcode/`. Isso deixa o
`git subtree pull` / os rebases de upstream baratos.

| patch | mexe em | propósito |
|-------|---------|-----------|
| `0001-run-cpp-ggpo-tick.diff` | `src/burner/win32/run.cpp` | rotear o passo de emulação por frame via `FbnHostRunFrame()` quando uma sessão de rollback está ativa |
| `0002-mingw-link-rollback.diff` | `makefile.mingw` | `-I` para achar `fbneo_host.h` + linkar `../rollback_netcode/build/librollbackfbneo.a -lws2_32` |
| `0003-cmdline-rbfnet-session.diff` | `src/burner/win32/main.cpp`, `drv.cpp` | parse de `-rbfnet player=..,localport=..,peerip=..,peerport=..,delay=..` → `FbnHostStart()` após `DrvInit`; `FbnHostStop()` no `DrvExit`; `-rbfnet` ou `-noscan` desliga o audit de ROMs no startup (`bSkipStartupCheck`) |

## Aplicando

```bash
cd fbneo
git apply ../rollback_netcode/patches/fbneo/0001-run-cpp-ggpo-tick.diff
git apply ../rollback_netcode/patches/fbneo/0002-mingw-link-rollback.diff
git apply ../rollback_netcode/patches/fbneo/0003-cmdline-rbfnet-session.diff
```

Os fontes do FBNeo são CRLF e estes `.diff` são LF — sempre passe
**`--ignore-whitespace`** ao `git apply`, senão o git do MSYS2 recusa:

```bash
cd fbneo
for p in ../rollback_netcode/patches/fbneo/*.diff; do git apply --ignore-whitespace "$p"; done
```

(`git apply -R --ignore-whitespace` para reverter). O script
[`../../build/setup-fbneo.sh`](../../build/setup-fbneo.sh) já faz isso, de forma
idempotente, e compila a lib. A árvore de trabalho já está com eles aplicados se
você seguiu a sessão que os criou — nesse caso pule a aplicação e vá direto ao
`make`.

## Ainda falta (não está nestes patches)

- **Build MSVC / meson**: só o `makefile.mingw` está ligado. Falta o
  `makefile.vc` / `meson.build`.
- **Setup de sessão / config**: algo precisa chamar `FbnHostStart()` com um
  `FbnHostConfig` preenchido (item de menu, linha de comando, ou o agente gRPC).
- **Pacing de áudio**: o tick ao vivo hoje emite som no buffer que o
  `RunGetNextSound()` apontou em `pBurnSoundOut`. A cadência do tick do GGPO vs o
  tamanho do segmento do DirectSound ainda precisa ser conciliada (drivar o tick
  a partir do `RunGetNextSound`, ou rodar com timing de vídeo e um ring de
  reamostragem).
