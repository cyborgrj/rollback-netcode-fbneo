# Ferramentas

## `ProbeAnalyze` — descobrir onde ficam o placar e os personagens

O placar de uma partida já existe: está na RAM do jogo, escrito pelo próprio
ROM. O que não existe é um **mapa** dizendo em que endereço. Cada driver guarda
o seu, e o FBNeo não distribui essa tabela.

Então a gente descobre por observação, em duas etapas.

### 1. Gravar uma partida

O emulador com `-rbfprobe` tira uma foto da RAM de trabalho a cada N frames e
grava num arquivo `.rbfp` ao lado do executável:

```
fbneo64d.exe kof98 -w -noscan -rbfprobe
```

Pode ser offline, contra a CPU — não precisa de dois PCs, nem de internet. Jogue
uma partida inteira até o fim (2 ou 3 rounds), e feche o emulador normalmente.
Sai um `rbf-probe-kof98-<data>.rbfp` de alguns megabytes.

O intervalo padrão é 30 frames (duas amostras por segundo). Para uma partida
longa dá para afrouxar: `-rbfprobe interval=60`.

> Grava só o que `-rbfprobe` pede. Uma partida normal, online ou não, não escreve
> nada — isso aqui é ferramenta de calibragem, não é telemetria.

### 2. Ler o arquivo

```
dotnet run --project tools/ProbeAnalyze -- rbf-probe-kof98-20260910-2211.rbfp
```

A ferramenta não sabe nada sobre nenhum jogo em particular. Ela procura
**formatos**:

- **contador de rounds** — um byte que fica em 0, sobe para 1, sobe para 2 e
  para. Em três minutos de jogo de luta quase nada mais na RAM se comporta
  assim, e quando dois bytes com esse formato aparecem a uma distância fixa um
  do outro, são os dois jogadores.
- **id de personagem** — escrito uma vez na tela de seleção e nunca mais. Sozinho
  isso descreve milhares de bytes, então precisa de **duas** gravações com
  personagens diferentes: constante nas duas, com valor diferente entre elas.

```
ProbeAnalyze <arquivo.rbfp>                    resumo + candidatos a contador
ProbeAnalyze <arquivo.rbfp> --trace <end>      todos os valores que o endereço teve
ProbeAnalyze <arquivo.rbfp> --dump <end> [n]   os bytes em volta, a cada mudança
ProbeAnalyze <a.rbfp> <b.rbfp> --chars         candidatos a id de personagem
```

Os endereços são endereços de CPU — `0xFF8xxx` no CPS1/CPS2, `0x10xxxx` no
Neo Geo — os mesmos que apareceriam num arquivo de cheat ou num disassembly.

### 3. E daí?

O resultado das duas etapas é a tabela por jogo que o leitor ao vivo usa para
publicar o placar. Essa parte é a próxima.

## Formato do `.rbfp`

Little-endian, escrito por [`../fbneo/ram_probe.cpp`](../fbneo/ram_probe.cpp):

```
"RBFPROBE"        8 bytes
u32 version       1
u32 ramLen        tamanho da RAM de trabalho
u32 cpuBase       endereço em que a CPU enxerga ram[0]
u32 chunkSize     64
u32 interval      frames entre amostras
char game[32]     short name do driver
char reserved[32]
```

Depois, registros com um byte de tag:

| tag | conteúdo |
|-----|----------|
| `1` | imagem inteira: `u32 frame`, `ramLen` bytes |
| `2` | delta: `u32 frame`, `u32 nChunks`, e `nChunks` × (`u32 índice`, `chunkSize` bytes) |
| `0` | fim |

O delta só é usado enquanto for menor que a imagem inteira, então um frame em
que a RAM toda mexeu não fica *maior* que a foto que ele deveria comprimir. Um
arquivo cortado no meio (emulador fechado à força) lê normalmente até onde foi.
