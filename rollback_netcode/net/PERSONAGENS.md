# Personagens — relação para o backend (Django) e o frontend

Atualizado em 13/09/2026. Fonte da verdade: `rollback_netcode/net/RbfServer/Characters.cs`.
Este documento é para quem cuida do Django e do site: o que o servidor do lobby
manda, o que mudou, e o que precisa ser corrigido do lado de lá.

## Como o personagem chega ao Django

- O emulador lê um **id numérico** da memória do jogo; o servidor do lobby
  transforma em **código** e manda no `POST /api/internal/matches/report/`.
- Campos: `player1_character` / `player2_character` (no topo e em cada item de
  `fights[]`). Nos jogos de time (kof98) também `player1_characters` /
  `player2_characters`, com os três códigos.
- **Regra do código**: nome em minúsculas, pontos removidos, espaço, `/` e `-`
  viram `_`, sem `_` duplo. Ex.: `M. Bison` → `m_bison`, `Q-Bee` → `q_bee`,
  `B.B. Hood` → `bb_hood`.
- **Id sem nome** vai como o próprio número em texto (`"16"`). Não é erro nem
  placeholder: é o valor lido. Quando o nome for descoberto, o número pode ser
  trocado pelo código no banco (tabelas abaixo).
- **kof98**: `player1_characters` vem na **ordem de escolha** do time, não na
  ordem em que lutaram (o jogador reordena depois de escolher). O
  `player1_character` é o primeiro escolhido — pode não ser quem abriu a luta.

---

## Correções de dados já gravados

Só se houver partidas desses jogos no banco de antes de 13/09/2026.

1. **kof98 — Chang e Choi estavam trocados.** Antes: 19 = `choi`, 20 = `chang`.
   Certo: **19 = `chang`, 20 = `choi`**. Em partidas de kof98 anteriores,
   trocar `choi` ↔ `chang` em `player*_character` e dentro das listas
   `player*_characters` (em `Match` e `MatchFight`).
2. **kof98 — ids que iam como número.** Antes só 9 tinham nome. Números
   gravados (`"3"`, `"25"`…) podem ser convertidos pela tabela do kof98 abaixo.
3. **vsav — ids que iam como número**, e o lado P2 não era lido. Números
   gravados podem ser convertidos pela tabela do vsav. Dois valores especiais:
   `"20"` → `j_talbain` e `"35"` → `lilith`. O byte do personagem no vsav
   "pisca" para id+1 durante alguns golpes; 20 e 35 não são personagem nenhum,
   eram essas leituras. (Outros números fora da tabela no vsav são ambíguos —
   deixe como estão.)
4. **sfa2 online**: numa sessão de 13/09 a partida não fechou e chegou com
   placar 0 × 0 sem `fights`. É problema do nosso lado (em investigação), nada
   a corrigir no banco além de, se quiser, apagar essa sessão de teste.

---

## Tabelas

Legenda do portrait: ✅ o site acha hoje · ⚠️ existe mas o site não acha
(nome diferente ou maiúscula) · ❌ falta o arquivo.

A pasta é `frontend/characters/<jogo>/`. O site procura
`<apelido>.gif/.png/.jpg/.jpeg/.webp` usando `CHAR_FILE_ALIASES` em
`PlayerStatsPage.jsx`, **sempre em minúsculas** — em servidor Linux, `Kyo.jpg`
não é o mesmo arquivo que `kyo.jpg`.

### `sf2ce` — Street Fighter II': Champion Edition (12/12)

| id | nome | código | portrait | |
|---:|---|---|---|:-:|
| 0 | Ryu | `ryu` | ryu.gif | ✅ |
| 1 | E. Honda | `e_honda` | e_honda.gif | ✅ |
| 2 | Blanka | `blanka` | blanka.gif | ✅ |
| 3 | Guile | `guile` | guile.gif | ✅ |
| 4 | Ken | `ken` | ken.gif | ✅ |
| 5 | Chun Li | `chun_li` | chun_li.gif | ✅ |
| 6 | Zangief | `zangief` | zangief.gif | ✅ |
| 7 | Dhalsim | `dhalsim` | dhalsim.gif | ✅ |
| 8 | M. Bison (o ditador) | `m_bison` | m_bison_dictator.gif | ✅ |
| 9 | Sagat | `sagat` | sagat.gif | ✅ |
| 10 | Balrog (o boxeador) | `balrog` | balrog_boxer.gif | ✅ |
| 11 | Vega (a garra) | `vega` | vega_claw.gif | ✅ |

Nomes ocidentais (US). No Japão Balrog/Vega/M. Bison têm os nomes trocados —
os códigos seguem o US.

### `ssf2t` — Super Street Fighter II Turbo (16/17)

Os 12 do sf2ce com os mesmos ids e códigos, mais:

| id | nome | código | portrait | |
|---:|---|---|---|:-:|
| 1 | E. Honda | `e_honda` | honda.gif | ✅ (apelido) |
| 12 | Cammy | `cammy` | cammy.gif | ✅ |
| 13 | T. Hawk | `t_hawk` | t_hawk.gif | ✅ |
| 14 | Fei Long | `fei_long` | fei_long.gif | ✅ |
| 15 | Dee Jay | `dee_jay` | dee_jay.gif | ✅ |
| 16? | Akuma | chega como `"16"` | — | ❌ não mapeado |

As versões "old" dos personagens usam o mesmo id da nova.

### `sfa2` — Street Fighter Alpha 2 (18/18)

| id | nome | código | portrait | |
|---:|---|---|---|:-:|
| 0 | Ryu | `ryu` | ryu.png | ✅ |
| 1 | Ken | `ken` | ken.png | ✅ |
| 2 | Akuma | `akuma` | gouki.png | ✅ (apelido) |
| 3 | Nash (Charlie) | `nash` | nash.png | ✅ |
| 4 | Chun Li | `chun_li` | chun_li.png | ✅ |
| 5 | Adon | `adon` | adon.png | ✅ |
| 6 | Sodom | `sodom` | sodom.png | ✅ |
| 7 | Guy | `guy` | guy.png | ✅ |
| 8 | Birdie | `birdie` | birdie.png | ✅ |
| 9 | Rose | `rose` | rose.png | ✅ |
| 10 | M. Bison | `m_bison` | m_bison_dictator.png | ✅ |
| 11 | Sagat | `sagat` | sagat.png | ✅ |
| 12 | Dan | `dan` | dan.png | ✅ |
| 13 | Sakura | `sakura` | — | ❌ |
| 14 | Rolento | `rolento` | — | ❌ |
| 15 | Dhalsim | `dhalsim` | — | ❌ |
| 16 | Zangief | `zangief` | — | ❌ |
| 17 | Gen | `gen` | — | ❌ |

⚠️ O site tem a ficha (nome/estilo/cor) do Nash com a chave `charlie`, mas o
lobby manda **`nash`** — acrescentar `nash` (ou um apelido) para o nome
aparecer direito.

### `vsav` — Vampire Savior (15/15)

Os ids não são contíguos (20, 25, 26, 32, 35 não são personagens).

| id | nome (US) | nome (JP) | código | portrait usado | |
|---:|---|---|---|---|:-:|
| 17 | B.B. Hood | Bulleta | `bb_hood` | bulleta.gif | ✅ |
| 18 | Demitri | Demitri | `demitri` | demitri.gif | ✅ |
| 19 | J. Talbain | Gallon | `j_talbain` | gallon.gif | ✅ |
| 21 | Victor | Victor | `victor` | victor.gif | ✅ |
| 22 | L. Raptor | Zabel | `l_raptor` | zabel.gif | ✅ |
| 23 | Morrigan | Morrigan | `morrigan` | morrigan.gif | ✅ |
| 24 | Anakaris | Anakaris | `anakaris` | anakaris.gif | ✅ |
| 27 | Felicia | Felicia | `felicia` | felicia.gif | ✅ |
| 28 | Bishamon | Bishamon | `bishamon` | bishammon.gif | ✅ |
| 29 | Rikuo | Aulbath | `rikuo` | aulbath.gif | ✅ |
| 30 | Sasquatch | Sasquatch | `sasquatch` | sasquatch.gif | ✅ |
| 31 | Q-Bee | Q-Bee | `q_bee` | q-bee.gif | ✅ |
| 33 | Hsien-Ko | Lei-Lei | `hsien_ko` | lei_lei.gif | ✅ |
| 34 | Lilith | Lilith | `lilith` | lilith.gif | ✅ |
| 36 | Jedah | Jedah | `jedah` | jedah.gif | ✅ |

A pasta tem 30 arquivos para 15 personagens (cópias com grafias diferentes:
`bb_hood`/`baby_bonnie_hood`/`bulleta`, `q_bee`/`qbee`/`q-bee`…). Pode apagar as
cópias que o site não usa — a coluna acima diz qual ele pega primeiro.
`donovan.gif` não tem id (não é selecionável).

### `kof98` — The King of Fighters '98 (38/38)

**O site não acha nenhum portrait do kof98 hoje**: não há entradas do kof98 em
`CHAR_FILE_ALIASES`, e os arquivos usam o nome completo (`terry_bogard.jpg`)
enquanto o código é curto (`terry`). Duas saídas — escolher uma:

- **A (recomendada)**: adicionar ao `CHAR_FILE_ALIASES` a coluna
  código → arquivo abaixo, e renomear os três com maiúscula.
- **B**: renomear os arquivos para o código exato (`terry.jpg`).

| id | nome | código | arquivo atual | |
|---:|---|---|---|:-:|
| 0 | Kyo Kusanagi | `kyo` | Kyo.jpg | ⚠️ maiúscula → `kyo.jpg` |
| 1 | Benimaru Nikaido | `benimaru` | Benimaru.jpg | ⚠️ maiúscula → `benimaru.jpg` |
| 2 | Goro Daimon | `daimon` | Goro_Daimon.jpg | ⚠️ → `goro_daimon.jpg` + apelido |
| 3 | Terry Bogard | `terry` | terry_bogard.jpg | ⚠️ apelido |
| 4 | Andy Bogard | `andy` | andy_bogard.jpg | ⚠️ apelido |
| 5 | Joe Higashi | `joe` | joe_higashi.jpg | ⚠️ apelido |
| 6 | Ryo Sakazaki | `ryo` | ryo_sakazaki.jpg | ⚠️ apelido |
| 7 | Robert Garcia | `robert` | robert_garcia.jpg | ⚠️ apelido |
| 8 | Yuri Sakazaki | `yuri` | yuri_sakazaki.jpg | ⚠️ apelido |
| 9 | Leona Heidern | `leona` | leona_heidern.jpg | ⚠️ apelido |
| 10 | Ralf Jones | `ralf` | ralf_jones.jpg | ⚠️ apelido |
| 11 | Clark Still | `clark` | clark_still.jpg | ⚠️ apelido |
| 12 | Athena Asamiya | `athena` | athena_asamiya.jpg | ⚠️ apelido |
| 13 | Sie Kensou | `kensou` | sie_kensou.jpg | ⚠️ apelido |
| 14 | Chin Gentsai | `chin` | chin_gentsai.jpg | ⚠️ apelido |
| 15 | Chizuru Kagura | `chizuru` | chizuru_kagura.jpg | ⚠️ apelido |
| 16 | Mai Shiranui | `mai` | mai_shiranui.jpg | ⚠️ apelido |
| 17 | King | `king` | king.jpg | ✅ |
| 18 | Kim Kaphwan | `kim` | kim_kaphwan.jpg | ⚠️ apelido |
| 19 | Chang Koehan | `chang` | chang_koehan.jpg | ⚠️ apelido |
| 20 | Choi Bounge | `choi` | choi_bounge.jpg | ⚠️ apelido |
| 21 | Yashiro Nanakase | `yashiro` | yashiro_nanakase.jpg | ⚠️ apelido |
| 22 | Shermie | `shermie` | shermie.jpg | ✅ |
| 23 | Chris | `chris` | chris.jpg | ✅ |
| 24 | Ryuji Yamazaki | `yamazaki` | ryuji_yamazaki.jpg | ⚠️ apelido |
| 25 | Blue Mary | `blue_mary` | blue_mary.jpg | ✅ |
| 26 | Billy Kane | `billy` | billy_kane.jpg | ⚠️ apelido |
| 27 | Iori Yagami | `iori` | iori_yagami.jpg | ⚠️ apelido |
| 28 | Mature | `mature` | mature.jpg | ✅ |
| 29 | Vice | `vice` | vice.jpg | ✅ |
| 30 | Heidern | `heidern` | heidern.jpg | ✅ |
| 31 | Takuma Sakazaki | `takuma` | takuma_sakazaki.jpg | ⚠️ apelido |
| 32 | Saisyu Kusanagi | `saisyu` | saisyu_kusanagi.jpg | ⚠️ apelido |
| 33 | Heavy D! | `heavy_d` | heavy_d.jpg | ✅ |
| 34 | Lucky Glauber | `lucky` | lucky_glauber.jpg | ⚠️ apelido |
| 35 | Brian Battler | `brian` | brian_battler.jpg | ⚠️ apelido |
| 36 | Rugal Bernstein | `rugal` | rugal_bernstein.jpg | ⚠️ apelido |
| 37 | Shingo Yabuki | `shingo` | shingo_yabuki.jpg | ⚠️ apelido |

`O_Chris.jpg`, `O_Shermie.jpg`, `O_Yashiro.jpg` (formas de Orochi) não têm id
próprio no jogo — ficam sem uso.

### `kof2002` — ainda não mapeado

Nada é lido ainda; nenhuma partida de kof2002 chega com placar nem personagem.

---

## Proposta (ainda não enviada): modo Advanced/Extra do kof98

O emulador já sabe onde o jogo guarda o modo de cada lado, mas isso ainda não
vai no report. Se o Django quiser guardar, a sugestão é um campo opcional em
cada item de `fights[]`:

```json
"player1_mode": "extra",
"player2_mode": "advanced"
```

Valores: `"advanced"` / `"extra"`; ausente nos jogos que não têm modo. Só
precisa aceitar e guardar — avisem quando o campo existir que o lobby passa a
mandar.
