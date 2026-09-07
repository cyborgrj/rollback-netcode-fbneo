# Patches do FBNeo

Diffs pequenos e revisáveis contra a árvore do FBNeo upstream fixada
(`fbneo/`, commit em [`../../../FBNEO_PIN.txt`](../../../FBNEO_PIN.txt)).

Mantenha cada patch mínimo — um ponto de chamada, um `#include`, um hook de
build — com a lógica de verdade morando em `rollback_netcode/`. Isso deixa o
`git subtree pull` / os rebases de upstream baratos.

| patch | mexe em | propósito |
|-------|---------|-----------|
| `0001-run-cpp-ggpo-tick.diff` | `src/burner/win32/run.cpp` | rotear o passo de emulação por frame via `FbnHostRunFrame()` quando uma sessão de rollback está ativa |

## Aplicando

```bash
cd fbneo
git apply ../rollback_netcode/patches/fbneo/0001-run-cpp-ggpo-tick.diff
```

(ou `git apply -R` para reverter). A árvore de trabalho já está com ele aplicado
se você seguiu a sessão que o criou.

## Ainda falta (não está nestes patches)

- **Ligação na build**: adicionar `rollback_netcode/core/*.cpp`,
  `rollback_netcode/fbneo/*.cpp` e os fontes do libggpo ao
  `makefile.vc` / `meson.build`, com os include paths do comentário de
  cabeçalho do `fbneo_host.cpp`.
- **Setup de sessão / config**: algo precisa chamar `FbnHostStart()` com um
  `FbnHostConfig` preenchido (item de menu, linha de comando, ou o agente gRPC).
- **Pacing de áudio**: o tick ao vivo hoje emite som no buffer que o
  `RunGetNextSound()` apontou em `pBurnSoundOut`. A cadência do tick do GGPO vs o
  tamanho do segmento do DirectSound ainda precisa ser conciliada (drivar o tick
  a partir do `RunGetNextSound`, ou rodar com timing de vídeo e um ring de
  reamostragem).
