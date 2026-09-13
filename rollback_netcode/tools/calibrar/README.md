# Scripts de calibragem

Um `.cmd` por jogo, para rodar **ao lado do `fbneo64d.exe`** (em `D:\RBF`, na
prática). Cada um abre o emulador com `-rbfprobe` e depois lista os `.rbfp`
que saíram.

```
copy calibrar-*.cmd D:\RBF\
```

Eles estavam só no `D:\RBF` até 13/09 — ou seja, existiam numa máquina só.
Agora ficam aqui também.

## Por que três gravações, e não uma

Os quatro primeiros jogos foram mapeados com uma gravação de luta cada, e isso
**quase deu certo**: no `sf2ce` o endereço do personagem do lado P1 acertava por
acidente e o do P2 lia outro campo. Ninguém percebeu por dois meses, e o erro só
apareceu quando dois humanos jogaram e o placar disse "E. Honda" para o Guile.

Então os scripts novos (`kof2002`, `ssf2t`) pedem três:

| gravação | acha o quê | como |
|----------|-----------|------|
| **1. luta contra a CPU** | barra de vida, fins de round | `--activity` para os picos, `--health` para a barra |
| **2. passeio na seleção** | o elenco | `--walk 12`, comparado com a ordem que você anotou |
| **3. luta entre dois humanos** | o endereço do personagem **dos dois lados** | `--constin` na janela da luta, cruzando os dois valores pelo passo da struct |

A terceira é a que não dá para pular. Contra a CPU, a struct do lado P2 não é a
de um jogador de verdade — e pior, assim que a partida acaba o jogo escreve ali
o **próximo adversário**, o que faz um endereço errado parecer certo.

O caminho completo, com os comandos do `ProbeAnalyze`, está em
[`../README.md`](../README.md).

## Estado

| jogo | vida | personagem |
|------|------|-----------|
| `sf2ce` | ✅ | ✅ elenco completo |
| `sfa2` | ✅ | ✅ elenco completo |
| `kof98` | ✅ | ⚠️ 9 de ~38 nomes |
| `vsav` | ✅ | ⚠️ só o lado P1 |
| `ssf2t` | 🗄️ mapeado e guardado | 🗄️ 16 de 17, guardado |
| `kof2002` | ❌ | ❌ |

Os dois estão **fora da biblioteca por enquanto** (13/09): o build do emulador
com o mapa do `ssf2t` foi barrado pelo Smart App Control por falta de
assinatura, e a decisão foi voltar em vez de comprar certificado agora.

O mapa do `ssf2t` não se perdeu — está no commit `44c5f34`, com as três
gravações que o confirmaram. Os scripts daqui continuam valendo para quando os
dois voltarem.
