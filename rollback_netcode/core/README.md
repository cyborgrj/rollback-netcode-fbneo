# `state_ring` — pool circular de save states, memória fixa

A camada determinística de memória por baixo da integração com o libggpo.

## Por que existe

O rollback netcode re-simula o passado recente toda vez que um input remoto
chega atrasado. Para isso, na thread de emulação, ele precisa:

1. **Salvar** o estado volátil completo da máquina uma vez por frame simulado.
2. **Carregar** qualquer um dos últimos ~8 frames instantaneamente e então
   avançar rápido (fast-forward).

Fazer isso com `malloc`/`free` por frame (o que o `BurnStateCompress` do FBNeo e
o exemplo `vectorwar` do libggpo ambos fazem) causa fragmentação de heap e
picos no tempo de frame. O `state_ring` aloca **um único** pool contíguo na
inicialização e nunca mais toca no heap enquanto a partida roda.

```
pool  = um malloc:  [ slot 0 ][ slot 1 ][ slot 2 ] ... [ slot N-1 ]
                       cada slot = estado volátil completo, tamanho fixo
slot(frame) = frame & (N - 1)          // N é potência de dois (padrão 16)
```

Como o libggpo nunca faz rollback além da janela de previsão dele (8) e nunca
guarda dois estados para o mesmo número de frame, `frame & (N-1)` só sobrescreve
frames que já são antigos demais para importar.

## O que ele captura

Exatamente o conjunto volátil do RunAhead do FBNeo:
`BurnAreaScan(ACB_FULLSCAN | ACB_RUNAHEAD, …)`. Esse conjunto já é comprovadamente
re-simulável de forma determinística — o RunAhead faz isso todo frame hoje. O
`state_ring` é uma generalização direta, para N slots, de `StateRunAheadSave` /
`StateRunAheadLoad` em `fbneo/src/burn/burn.cpp`.

## API

Ver [`state_ring.h`](state_ring.h). Encaixa nos `GGPOSessionCallbacks` do libggpo:

| callback do libggpo | mapeia para           |
|---------------------|-----------------------|
| `save_game_state`   | `StateRingSave`       |
| `load_game_state`   | `StateRingLoad`       |
| `free_buffer`       | `StateRingFree` (no-op) |

`StateRingInit` precisa ser chamado **depois** que o driver rodou o primeiro
frame, para o tamanho medido do estado estar estável; o tamanho fica então
travado para a sessão inteira (uma mudança no meio retorna
`STATE_RING_ERR_SIZE_DRIFT` em vez de realocar, o que corromperia rollbacks em
andamento).

Todas as chamadas rodam só na thread de emulação — elas pegam emprestado o
ponteiro global `BurnAcb`, exatamente como RunAhead / Rewind / `BurnStateSave`.

## Ainda falta

- Não foi adicionado a nenhum makefile / build meson do FBNeo.
- Nenhum ponto de chamada de `StateRingInit` ligado ao loop de frame (isso vem
  do `fbneo_host`).

---

# `ggpo_bridge` — sessão libggpo + cola dos callbacks

[`ggpo_bridge.h`](ggpo_bridge.h) / [`ggpo_bridge.cpp`](ggpo_bridge.cpp).

Dono do `GGPOSession`, dos handles de jogador e dos 7 `GGPOSessionCallbacks`.
Depende do `state_ring` para save/load/free, e de uma pequena **interface de
host** (`GgpoBridgeHost`) para que o arquivo nunca cite um símbolo do FBNeo —
assim ele fica testável isolado e reutilizável pelo agente gRPC.

```
                 GgpoBridgeTick()  (uma vez por frame renderizado, thread de emu)
                         |
   ggpo_idle -> poll_local_input -> ggpo_add_local_input
                         |
                    stepOnce(bRollback=0)
                         |
   ggpo_synchronize_input -> host.step_frame(..., bRollback=0) -> ggpo_advance_frame
                         |
        (se um input atrasado chegou, o libggpo chama de volta, síncrono:)
   cb_load_game_state  -> StateRingLoad
   cb_advance_frame    -> stepOnce(bRollback=1)   x (frames a repetir)
   cb_save_game_state  -> StateRingSave           (re-salva cada frame repetido)
```

Mapeamento callback do libggpo -> bridge:

| callback           | bridge                                              |
|--------------------|-----------------------------------------------------|
| `begin_game`       | `return true`                                       |
| `save_game_state`  | `StateRingSave`                                     |
| `load_game_state`  | `StateRingLoad`                                     |
| `free_buffer`      | `StateRingFree` (no-op)                             |
| `advance_frame`    | `stepOnce(bRollback=1)` — re-simulação silenciosa   |
| `on_event`         | traduzido p/ `GgpoBridgeEvent` -> `host.on_event`   |
| `log_game_state`   | `host.log_state` se fornecido, senão `return true`  |

### Contrato do host

- `poll_local_input(out, nBytes, user)` — o input desta máquina para o frame.
- `step_frame(syncInputs, nPlayers, disconnectFlags, bRollback, user)` — roda
  **exatamente um** frame determinístico do core. Quando `bRollback == 1`: **sem
  desenho, sem áudio**. Retorna 0 em sucesso.
- `StateRingInit()` precisa ter rodado antes de `GgpoBridgeStart()` (o host
  aquece o driver um frame antes). O bridge tem um fallback de init tardio
  barulhento, mas esse caminho arrisca sondar o tamanho antes de o driver
  estabilizar.

### Verificação de determinismo offline

`GgpoBridgeStartSyncTest(cfg, host, nCheckDistance)` roda o synctest do libggpo:
sem sockets, salva todo frame, faz rollback de `nCheckDistance` e compara os
checksums do `state_ring`. Rode isso na CI, por driver, antes de confiar num jogo.

### Dependências de build

Precisa do `ggponet.h` do libggpo no include path e da lib do libggpo linkada.
O libggpo está vendorizado em `rollback_netcode/third_party/ggpo/` (git subtree).

### Threading

Tudo aqui roda na **thread de emulação**. O agente gRPC enfileira comandos que a
thread de emulação drena entre os ticks; ele não pode chamar isto direto.
