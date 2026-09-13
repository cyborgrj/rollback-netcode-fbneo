# Patches do FBNeo

Diffs pequenos e revisáveis contra a árvore do FBNeo upstream fixada
(`fbneo/`, commit em [`../../../FBNEO_PIN.txt`](../../../FBNEO_PIN.txt)).

Mantenha cada patch mínimo — um ponto de chamada, um `#include`, um hook de
build — com a lógica de verdade morando em `rollback_netcode/`. Isso deixa o
`git subtree pull` / os rebases de upstream baratos.

| patch | mexe em | propósito |
|-------|---------|-----------|
| `0001-run-cpp-ggpo-tick.diff` | `src/burner/win32/run.cpp` | rotear o passo de emulação por frame via `FbnHostRunFrame()` quando uma sessão de rollback está ativa; fechar a janela quando uma transmissão assistida acaba; uma chamada a `RamProbeFrame()` por frame emulado |
| `0002-mingw-link-rollback.diff` | `makefile.mingw` | `-I` para achar `fbneo_host.h` + linkar `../rollback_netcode/build/librollbackfbneo.a -lws2_32` |
| `0006-dx9-match-bar.diff` | `src/intf/video/win32/vid_directx9.cpp`, `vid_interface.cpp` | desenha a barra da partida **na resolução da janela**, com `ID3DXFont` e `Boxxy` — muito mais nítido que desenhar numa imagem de 384px e deixar o blitter ampliar. Chama `OverlayClaim()` para o desenho na imagem se calar. Tambem troca o blitter padrao do build 64-bit de 2 (SoftFX DDraw) para 3 (DirectX 9) - so ele desenha a barra nitida, e o Frame Perfect distribui o `d3dx9_43.dll` junto |
| `0004-netplay-lockdown.diff` | `src/burner/win32/scrn.cpp` | durante uma partida: bloqueia qualquer pausa (`SetPauseMode`, pausa por perda de foco, pausa do menu) e deixa passar só os itens de vídeo inofensivos no `OnCommand` |
| `0007-hud-ping-rollback-fps.diff` | `src/burner/win32/run.cpp`, `src/intf/video/vid_interface.cpp`, `vid_directx9.cpp` | o leitor de netcode do canto superior direito: Backspace chama `HudCycle()`, `HudFrame()` uma vez por frame apresentado, `HudDraw()` no `VidFrameCallback` (caminho borrado) e `RbfHudDraw()` no DirectX 9 (nítido). Também troca a fonte do leitor para `fonte_metricas` e diminui o corpo. Depende de `0006` |
| `0003-cmdline-rbfnet-session.diff` | `src/burner/win32/main.cpp`, `drv.cpp` | parse de `-rbfnet player=..,localport=..,peerip=..,peerport=..,delay=..,relayip=..,relayport=..,p1=..,p2=..` → `FbnHostStart()` após `DrvInit`, e de `-rbfwatch matchid=..,relayip=..,relayport=..` → `FbnHostStartWatch()`; `FbnHostStop()` no `DrvExit`; parse de `-rbfprobe [interval=N]` → `RamProbeRecordStart()` (calibragem do placar, ver [`../../tools/README.md`](../../tools/README.md)); `-rbfnet`, `-rbfwatch`, `-rbfprobe` ou `-noscan` desliga o audit de ROMs no startup (`bSkipStartupCheck`) |

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

(`git apply -R --ignore-whitespace` para reverter).

⚠️ **A ordem importa e a numeração é a ordem.** O `0006` reescreve o
`vid_interface.cpp` inteiro, incluindo o gancho que o antigo `0005` acrescentava
— por isso o `0005` foi removido em 12/09: aplicado antes, ele fazia o `0006`
falhar numa árvore limpa, e o `setup-fbneo.sh` seguia em frente deixando o
DirectX 9 sem a barra. O script
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
