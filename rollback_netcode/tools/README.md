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
ProbeAnalyze <arq.rbfp>                     resumo + candidatos a contador de round
ProbeAnalyze <arq.rbfp> --activity          bytes que mudaram por amostra (acha os fins de round)
ProbeAnalyze <arq.rbfp> --health            bytes que se comportam como barra de vida
ProbeAnalyze <arq.rbfp> --trace <end>       todos os valores que o endereço teve
ProbeAnalyze <arq.rbfp> --dump <end> [n]    os bytes em volta, a cada mudança
ProbeAnalyze <arq.rbfp> --steps f1,f2 [tol] bytes cuja história termina exatamente nesses instantes
ProbeAnalyze <arq.rbfp> --ends <n>          endereços que terminaram nesse valor (um por linha)
ProbeAnalyze <arq.rbfp> --constin <a>-<b>   bytes parados entre dois frames (`0xEND VALOR`)
ProbeAnalyze <a.rbfp> <b.rbfp> --chars      id de personagem, comparando duas gravações
```

Os endereços são endereços de CPU — `0xFF8xxx` no CPS1/CPS2, `0x10xxxx` no
Neo Geo — os mesmos que apareceriam num arquivo de cheat ou num disassembly.

### 3. O caminho que funcionou de verdade

A busca por "contador de round" é a primeira ideia e **não** foi a que resolveu:
num jogo real ela devolve dezenas de candidatos e o placar não está entre eles.
O que resolveu foi:

1. `--activity` para achar os fins de round. Uma troca de round revira muito
   mais RAM que o meio de um round, então os picos são os rounds. Confirmação
   grátis: uma partida 2x0 tem dois picos grandes, uma 2x1 tem três.
2. `--health` para achar a barra de vida. É um formato muito mais rígido que o
   de um contador — enche de uma vez só, nunca sobe fora disso, e chega ao fim.
3. `--constin` nas duas gravações, comparando com `join`, para o personagem.

E a barra de vida é melhor que o contador também para o leitor ao vivo: ela diz
**quem** perdeu o round e **quando**, sem depender de como cada jogo guarda o
placar.

### 4. A pegadinha dos bytes trocados

O FBNeo guarda a memória do 68000 **com os dois bytes de cada palavra de 16 bits
invertidos**. Está em `cpu/m68000_intf.cpp`, no `ReadByte()`: para memória
mapeada ele faz `a ^= 1` antes de indexar. Ou seja, o índice no buffer **não é**
o endereço da CPU.

Isso não é detalhe cosmético: sem desfazer, um campo de três bytes (o time do
KOF, por exemplo) volta embaralhado e nada bate com endereço nenhum publicado.
O `ram_probe` e o `ProbeAnalyze` desfazem a troca, então tudo aqui — entrada e
saída — está em **endereço de CPU**. Vale para o Neo Geo e para o CPS.

### 5. O que já está mapeado

Endereços de CPU. `vida` é um byte; cheio é o valor da esquerda, e o round
acabou quando ele passa a valer mais que isso (estourou para negativo).

| jogo | vida P1 | vida P2 | personagem P1 | personagem P2 | cheio | morreu |
|------|---------|---------|----------------|----------------|-------|--------|
| `sf2ce` | `0xFF83E9` | `0xFF86E9` | `0xFF83D9` | `0xFF86D9` | `0x90` (144) | `0xFF` |
| `kof98` | `0x108239` | `0x108439` | `0x10A84E`+3 | `0x10A85F`+3 | `0x67` (103) | `> 0x67` (`0xFA`…`0xFF`) |

No `sf2ce` os dois jogadores ficam a **0x300** um do outro; no `kof98`, a
**0x200**. Em nenhum dos dois o KO é vida zero — a vida estoura para negativo
(`0xFF`, `0xFA`, `0xFC`…) e só depois a struct é limpa, e é isso que distingue
"perdeu o round" de "a partida acabou".

O `kof98` é 3x3, então o personagem não é um byte e sim **três em sequência**, na
ordem em que entram na luta.

Personagens confirmados:

- `sf2ce`: 4 = Ryu, 5 = E.Honda, 6 = Ken.
- `kof98`: 0 = Kyo, 1 = Benimaru, 2 = Daimon, 18 = Kim, 19 = Choi, 20 = Chang,
  27 = Iori, 28 = Mature, 29 = Vice. (19 e 20 saíram da ordem em que o time da
  CPU entrou na luta — vale reconfirmar.)

O resto sai com uma gravação por personagem: o byte é escrito no instante em que
a luta começa, então basta escolher, esperar o "Fight!" e fechar.

`vsav` e `sfa2` ainda faltam.

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
