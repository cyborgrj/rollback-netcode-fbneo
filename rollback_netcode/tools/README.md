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
ProbeAnalyze <arq.rbfp>... --score          aplica a regra final: placar e personagens
```

O `--score` é a **referência** do leitor que roda dentro do emulador
([`../fbneo/match_score.cpp`](../fbneo/match_score.cpp)). Os dois têm que
concordar. Sempre que um dos dois mudar, rode:

```
ProbeAnalyze --score D:\RBF\rbf-probe-*.rbfp
```

São oito partidas cujo resultado era conhecido de antemão, e as oito batem.

Desde 12/09 o `--score` separa **partida** de **round**, igual ao emulador: uma
gravação com uma sessão inteira sai assim, uma linha por partida, com os
personagens daquela partida —

```
rbf-probe-sf2ce-...rbfp      sf2ce  2 x 1   P1 venceu   (3 partida(s))
     1. P1[4] 2 x 1 P2[5]   P1     (f901..f7771)
     2. P1[6] 2 x 0 P2[5]   P1     (f7950..f11020)
     3. P1[4] 0 x 2 P2[6]   P2     (f11300..f15880)
```

O corte entre uma partida e a seguinte é o **struct de vida sendo zerado**, que
é um evento diferente do fim de round (lá o perdedor fica negativo e recarrega).
Nas gravações antigas, de uma partida cada, isso aparece no último sample:
`00 00 00 00` no `0xFF83E8`.

Os endereços são endereços de CPU — `0xFF8xxx` no CPS1/CPS2, `0x10xxxx` no
Neo Geo — os mesmos que apareceriam num arquivo de cheat ou num disassembly.

### `ScoreSelfTest` — o leitor sem o jogo

O `--score` confere a **regra** contra gravações reais. O `ScoreSelfTest`
confere o **código que roda dentro do emulador**, alimentando
`match_score.cpp` com uma luta escrita à mão: sem ROM, sem emulador, sem
ninguém jogando. Ele troca só o `RamProbeRead8`; tudo acima disso é o código
que vai para o executável.

```bash
# no shell mingw64 do MSYS2, dentro de tools/ScoreSelfTest
g++ -std=c++11 -I. -I../../fbneo -o score_selftest.exe \
    score_selftest.cpp ../../fbneo/match_score.cpp
./score_selftest.exe
```

Ele roda uma sessão de três partidas com personagens diferentes em cada uma e
confere as três linhas, o total, e o arquivo `rbf-result-*.txt` que o launcher
vai ler. A checagem que mais vale é a chata: que o personagem da partida 1 não
virou o da partida 3 — o erro que um placar só de totais não consegue nem
notar.

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

**A regra é a mesma nos quatro jogos**, e isso não era óbvio antes de olhar: a
vida é uma **palavra de 16 bits com sinal**, big-endian (68000). Cheia vale um
positivo; o round acabou quando ela fica **negativa** (`0xFFFF`, `0xFFFA`,
`0xFFFC`…). Nunca é zero — zero é a struct sendo limpa quando a partida acaba, e
é justamente isso que separa "perdeu o round" de "acabou".

Endereços de CPU:

| jogo | vida P1 | vida P2 | passo | cheia | personagem P1 | personagem P2 |
|------|---------|---------|-------|-------|----------------|----------------|
| `sf2ce` | `0xFF83E8` | `0xFF86E8` | `0x300` | `0x0090` (144) | `0xFF83D9` | `0xFF86D9` |
| `sfa2`  | `0xFF8450` | `0xFF8850` | `0x400` | `0x0090` (144) | `0xFF8482` | `0xFF8882` |
| `vsav`  | `0xFF8450` | `0xFF8850` | `0x400` | `0x0120` (288) | `0xFF841C` (16 bits) | **falta** |
| `kof98` | `0x108238` | `0x108438` | `0x200` | `0x0067` (103) | `0x10A84E`+3 | `0x10A85F`+3 |

Dois jogos fogem do formato "melhor de três":

- **`vsav`** não recarrega a vida e não tem corte entre rounds — o `--activity`
  não acha round nenhum ali, e não é defeito. A barra vale `0x0120` = **duas**
  barras de 144. O placar sai de quantas barras cada lado perdeu: 2 quando a
  vida fica negativa, 1 quando cai a 144 ou menos, 0 acima disso. Conferido: num
  2x1 o vencedor tinha chegado a 86, e num 2x0 o vencedor não tomou um ponto de
  dano e ficou em `0x0120` do começo ao fim.
- **`kof98`** é 3x3, então o personagem não é um byte e sim **três em sequência**,
  na ordem em que entram na luta.

Personagens confirmados:

- `sf2ce`: 4 = Ryu, 5 = E.Honda, 6 = Ken.
- `sfa2`: **elenco completo.** Esse byte acompanha o cursor **ao vivo** na tela de
  seleção, então três passeios pelo grid bastaram.

  | id | | id | | id | |
  |----|--|----|--|----|--|
  | 0 | Ryu | 6 | Sodom | 12 | Dan |
  | 1 | Ken | 7 | Guy | 13 | Sakura |
  | 2 | Akuma | 8 | Birdie | 14 | Rolento |
  | 3 | Nash/Charlie | 9 | Rose | 15 | Dhalsim |
  | 4 | Chun Li | 10 | M. Bison | 16 | Zangief |
  | 5 | Adon | 11 | Sagat | 17 | Gen |

  Os 18 personagens ocupam os ids 0..17 sem buraco e sem repetição, o que é a
  própria verificação: qualquer erro de leitura teria deixado um valor de fora e
  outro em dobro.
- `kof98`: 0 = Kyo, 1 = Benimaru, 2 = Daimon, 18 = Kim, 19 = Choi, 20 = Chang,
  27 = Iori, 28 = Mature, 29 = Vice. (19 e 20 saíram da ordem em que o time da
  CPU entrou na luta — vale reconfirmar.)
- `vsav`: 36 = Jedah, 22 = L.Raptor (só o lado P1).

**O que falta:** o personagem do P2 no `vsav`. `0xFF881C`, que seria o simétrico,
oscila entre dois valores vizinhos o tempo todo — é campo de animação, não id. O
jeito barato de fechar isso é gravar uma partida **de dois jogadores humanos**,
em que as duas structs são simétricas de verdade.

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
