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
| `0003-cmdline-rbfnet-session.diff` | `src/burner/win32/main.cpp`, `drv.cpp` | parse de `-rbfnet player=..,localport=..,peerip=..,peerport=..,delay=..` na linha de comando → `FbnHostStart()` após o `DrvInit`; `FbnHostStop()` no `DrvExit` |

## Aplicando

```bash
cd fbneo
git apply ../rollback_netcode/patches/fbneo/0001-run-cpp-ggpo-tick.diff
git apply ../rollback_netcode/patches/fbneo/0002-mingw-link-rollback.diff
git apply ../rollback_netcode/patches/fbneo/0003-cmdline-rbfnet-session.diff
```

(ou `git apply -R` para reverter). O script
[`../../build/setup-fbneo.sh`](../../build/setup-fbneo.sh) aplica os dois de forma
idempotente e já compila a lib. A árvore de trabalho já está com eles aplicados
se você seguiu a sessão que os criou.

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
