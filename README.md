# Rollback Netcode para o FinalBurn Neo

Integração do rollback netcode do [libggpo](https://github.com/pond3r/ggpo) ao
core do FinalBurn Neo, orquestrada por um agente de controle gRPC.

Prioridades de projeto: **determinismo**, **gerência estrita de memória de save
state** (buffers circulares fixos, zero alocação de heap por frame) e uso seguro
da API de estado do FBNeo (`BurnStateSave` / `BurnStateLoad`, `BurnAreaScan`).

## Organização

```
rollback_netcode/
  core/                    nosso código
    state_ring.{h,cpp}     pool circular de save states, N slots fixos
    ggpo_bridge.{h,cpp}    GGPOSession + os 7 callbacks do GGPO
    README.md              docs do módulo
  fbneo/
    fbneo_host.{h,cpp}     implementação concreta de GgpoBridgeHost p/ o FBNeo
  build/                   Makefile de librollbackfbneo.a + setup-fbneo.sh
  patches/fbneo/           diffs mínimos contra o FBNeo upstream
  third_party/
    ggpo/                  libggpo, vendorizado via `git subtree` (ver abaixo)

fbneo/                     checkout do FBNeo upstream — NÃO versionado aqui
                           (repo git próprio; commit fixado em FBNEO_PIN.txt)
```

## FBNeo

`fbneo/` é um checkout upstream separado, no `.gitignore`. O commit exato contra
o qual a build deve rodar está em [`FBNEO_PIN.txt`](FBNEO_PIN.txt).

## libggpo vendorizado

`rollback_netcode/third_party/ggpo/` entra via `git subtree`, então um
`git clone` simples deste repo já traz tudo, e dá para aplicar patches locais no
libggpo.

Para atualizar depois:

```bash
git subtree pull --prefix rollback_netcode/third_party/ggpo \
  https://github.com/pond3r/ggpo master --squash
```

## Licença

O código próprio deste projeto é MIT — ver [`LICENSE`](LICENSE). O libggpo
vendorizado em `rollback_netcode/third_party/ggpo/` é MIT
([LICENSE dele](rollback_netcode/third_party/ggpo/LICENSE)). O FBNeo em si não é
redistribuído aqui — apenas um patch pequeno em `rollback_netcode/patches/fbneo/`;
compilar exige o seu próprio checkout do FBNeo, sob a licença dele.

## Situação

- [x] `state_ring` — anel determinístico de save states
- [x] `ggpo_bridge` — sessão libggpo + cola dos callbacks
- [x] libggpo vendorizado
- [x] `fbneo_host` — implementa `GgpoBridgeHost` (mapa de input, step_frame, ciclo de vida)
- [x] patch no `run.cpp` — roteia o passo por frame via `FbnHostRunFrame()`
- [x] build MinGW — `librollbackfbneo.a` ([build/](rollback_netcode/build/)) + patch no `makefile.mingw` (ainda não compilado ponta a ponta)
- [ ] build MSVC / meson
- [ ] um chamador para `FbnHostStart` (menu / CLI / agente gRPC)
- [ ] conciliação do pacing de áudio (tick do GGPO vs segmentos do DirectSound)
- [ ] agente de controle gRPC + fila de comandos
