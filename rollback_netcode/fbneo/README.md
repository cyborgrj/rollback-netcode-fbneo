# `fbneo_host` — integração FBNeo ↔ core de rollback

O único módulo autorizado a conhecer tanto o FBNeo quanto o core de rollback.
Implementa `GgpoBridgeHost` em termos concretos do FBNeo.

[`fbneo_host.h`](fbneo_host.h) / [`fbneo_host.cpp`](fbneo_host.cpp)

## O que ele faz

### 1. Mapa de input determinístico (montado no `FbnHostStart`)

Percorre a lista de inputs do driver ativo via `BurnDrvGetInputInfo` na ordem
natural. Todo input `BIT_DIGITAL` chamado `P<n> …` recebe o próximo índice de bit
do jogador `n`. Resultado: um bitmask compacto (little-endian) por jogador,
`ceil(maxBits/8)` bytes de largura — uniforme entre jogadores, porque o libggpo
usa um único `input_size`. A ordem da lista é idêntica nos dois peers para um
dado build, então o mapeamento concorda implicitamente — nada é negociado.

- **Inputs analógicos / constantes** — logados uma vez, deixados sem sincronizar
  (o v1 mira jogos de luta digitais).
- **Inputs comuns de verdade** (Reset, Service, Diagnostic) — não transmitidos.
- **DIP switches** — estáticos na partida; vivem no save state, não no input por
  frame. Os dois peers precisam escolher os mesmos DIPs antes do `FbnHostStart`.

### 2. Callbacks do `GgpoBridgeHost`

| callback | faz |
|----------|-----|
| `poll_local_input` | empacota a fatia do jogador local dos bytes do driver (já preenchidos por `GetInput(true)`) no bitmask |
| `step_frame` | escreve os inputs sincronizados do libggpo (de **todos** os jogadores, corrigidos por previsão) de volta nos bytes do driver, depois `BurnDrvFrame()`. Quando `bRollback`: zera `pBurnDraw`/`pBurnSoundOut` e liga `bBurnRunAheadFrame` antes — a mesma receita do frame silencioso do RunAhead |
| `on_event` | loga via `bprintf` (TODO: encaminhar ao agente gRPC) |

### 3. Ciclo de vida + ponto de entrada por frame

```c
FbnHostStart(&cfg);            // monta o mapa + GgpoBridgeStart  (driver já iniciado)
FbnHostStartSyncTest(&cfg, n); // verificação de determinismo offline
FbnHostRunFrame();             // uma vez por frame, do RunFrame(); 1=avançou 0=recuperando <0=fatal
FbnHostStop();
```

`StateRingInit` é **adiado** para o primeiro `FbnHostRunFrame()` — aí o driver já
executou um frame e o tamanho do estado volátil está estável para travar.

## Ligação no FBNeo

Um patch: [`../patches/fbneo/0001-run-cpp-ggpo-tick.diff`](../patches/fbneo/0001-run-cpp-ggpo-tick.diff)
roteia o passo por frame via `FbnHostRunFrame()` dentro de `RunFrame()` quando
`FbnHostIsActive()`. Todo o resto (Kaillera, replay, RunAhead, Rewind) fica
intacto e é mutuamente exclusivo com uma sessão ativa.

Ainda falta: ligar na build (adicionar `core/*.cpp`, `fbneo/*.cpp` e os fontes do
libggpo ao `makefile.vc` / `meson.build`, com os include paths do comentário de
cabeçalho do `fbneo_host.cpp`), um chamador para `FbnHostStart` (menu / CLI /
agente gRPC) e a conciliação do pacing de áudio.

## Fronteira

`fbneo_host.cpp` inclui `burnint.h` e nossos dois headers de core — nada do
burner win32. O único símbolo de nível burner que ele usa é `bDrvOkay`,
re-declarado localmente (igual ao que `burn/cheat.cpp` faz). Isso o deixa perto
de compilável para o burner SDL também.

## O leitor de netcode (`hud.cpp`)

Canto superior direito, ligado no **Backspace**: uma vez mostra as duas linhas,
de novo deixa só a primeira, de novo esconde. Começa escondido.

```
                          ping 13ms | delay 3f | rollback 2f
                                  run ahead off | 59,9 fps
```

Nada aqui é estimado:

| campo | de onde vem |
|-------|-------------|
| `ping` | `ggpo_get_network_stats` do próprio libggpo, contra o adversário — não é o ping do lobby |
| `delay` | o delay com que a **sessão** abriu, que o servidor decidiu pela média dos dois lados — não o que este lado pediu |
| `rollback` | quantos frames o libggpo re-simulou, **pior rajada do último segundo** |
| `run ahead` | a opção do próprio FBNeo |
| `fps` | frames apresentados, medidos aqui em janelas de meio segundo |

Duas decisões que valem explicação.

**O rollback é um pico, não o valor do frame.** Por frame ele é 0 quase sempre e
pula para 4 num único frame, sessenta vezes por segundo — um número piscando
assim não dá para ler. O pico de um segundo segura o valor tempo suficiente para
significar alguma coisa e cai sozinho quando a linha acalma.

**`run ahead` sempre lê `off` durante uma partida, e isso é a resposta, não uma
falha.** O run ahead do FBNeo não tem passo 1/2/3 — é um frame, ligado ou
desligado. E durante uma sessão o nosso caminho de frame nem passa pelo ramo de
run ahead do `run.cpp`: o rollback faz o mesmo trabalho, melhor, e rodar os dois
significaria salvar e carregar estado duas vezes por frame. Offline o campo
mostra a opção de verdade.

Sem sessão, os três campos de rede mostram `--` em vez de sumir, para a linha
não mudar de forma quando uma partida começa.

Desenho: o mesmo de `overlay.cpp` — GDI dentro da imagem do jogo como base, e o
DirectX 9 desenhando na resolução da janela quando `OverlayClaim()` está de pé.
Uma reivindicação só cobre as duas coisas: é o mesmo blitter no mesmo frame.
