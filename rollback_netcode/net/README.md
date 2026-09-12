# net/ — gRPC lobby (agente de controle)

Matchmaking between two patched-FBNeo machines. The server tracks who is online
and in which game room, relays challenges, and — when a challenge is accepted —
tells both peers how to start the GGPO session (`MatchStart`). The launcher then
spawns `fbneo.exe <game> -w -rbfnet …` on each side and GGPO runs peer-to-peer
over UDP.

```
RbfLauncher (VM 1) ──┐                       ┌── RbfLauncher (VM 2)
                     │   gRPC bidi stream    │
                     └──►  RbfServer  ◄──────┘      (your machine now, cloud later)
                          in-memory
                     MatchStart ──► both sides
                          │
   fbneo.exe :7000 ◄──────┴──────► fbneo.exe :7000     GGPO / UDP, peer-to-peer
```

## Projects

| project | TFM | role |
|---------|-----|------|
| `RbfProtocol` | netstandard2.0 | the `rbf.proto` contract + generated stubs, shared by server and launcher |
| `RbfServer`   | net8.0 console | the lobby service |

### Why Grpc.Core (and not Grpc.Net.Client)

The launcher is **.NET Framework 4.8** for old-Windows reach. `Grpc.Net.Client`
needs HTTP/2 via `WinHttpHandler`, which isn't available on Windows 7 / older 10.
`Grpc.Core` (the C-core library) works there and on modern .NET alike, so the
whole repo is pinned to the **Grpc.Core 2.46.6** cohort
(`Grpc.Core.Api` 2.46.6, `Grpc.Tools` 2.46.6, `Google.Protobuf` 3.21.12). It is
EOL but stable; fine for this. Transport is plain h2c (no TLS) for now.

## Build & run

```bash
winget install Microsoft.DotNet.SDK.8      # if not already

cd rollback_netcode/net
dotnet run --project RbfServer -- --port 50051
#   --bind 0.0.0.0   (default)   --port 50051 (default)
```

Clients connect to `<the server machine's LAN/public IP>:50051`. Open that port
in the firewall. For the two Win10 test VMs, run the server on the host (or a
third VM) and point both launchers at its IP.

## Protocol shape (`rbf.proto`)

One bidirectional stream per client: `rpc Connect(stream ClientMsg) returns (stream ServerMsg)`.

- Client's first message is `Hello{username}` → `Welcome{userId}` or `LoginRejected`.
- `JoinRoom{game}` / `LeaveRoom` — a "room" is just everyone with the same game short name.
- `Roster` is pushed on every change (who's online, their room, their state).
- `Challenge{targetUserId}` → target gets `ChallengeIn`; replies `ChallengeReply{accept}`.
- On accept the server sends **`MatchStart`** to both — each side's own
  `player_num` / `local_port` / `peer_ip` / `peer_port` / `frame_delay`, ready to
  hand to `fbneo.exe`. Peer IP is the address the server sees for that client's
  connection (correct on a LAN; NAT traversal is a later problem).
- `MatchStatus{phase}` from a client ends/aborts the match server-side.

## Contas: o lobby confere o token com o Django

O `Hello` leva o `access_token` que o launcher pegou no login, e o servidor
pergunta ao Django se ele vale antes de aceitar a conexão:

```
POST {api}/api/internal/verify-token/
X-API-KEY: <segredo compartilhado>
{"token": "..."}

200 {"valid":true,"user":{"id":1,"username":"PlayerOne","nickname":"ArcadeKing","ranking":1500}}
401 {"valid":false,"error":"Token is invalid or expired."}
403  a chave é nossa e está errada
```

**A identidade vem da resposta do Django, e o `username` do `Hello` é ignorado.**
Aceitar o nome que o cliente declara ao lado de um token faria do token
decoração: qualquer um logaria como si mesmo e anunciaria outro nome.

Os três "não" são coisas diferentes e continuam diferentes:

| resposta | o que é | o que o jogador lê |
|----------|---------|--------------------|
| `401` | token expirado ou falso | "Sua sessão expirou. Entre de novo no launcher." |
| `403` | **nossa** `X-API-KEY` está errada | "O servidor de contas não respondeu." |
| silêncio / `5xx` | Django fora do ar | idem |

Juntar `403` com `401` diria a todo mundo que o login expirou no dia em que
alguém trocasse a chave — e o log concordaria com eles.

### Configurar

A chave vem do **ambiente**, nunca da linha de comando: qualquer coisa em
`argv` é legível por outro usuário da máquina via `ps`.

```ini
# /etc/systemd/system/rbfserver.service
[Service]
Environment=RBF_INTERNAL_API_KEY=<o mesmo INTERNAL_API_KEY do Django>
ExecStart=/home/ubuntu/rbfserver/RbfServer --bind 0.0.0.0 --port 50051
```

`--api-url` muda o endereço do Django (padrão `http://localhost:8000`, que é o
caso em produção — os dois moram na mesma instância). `--api-key-file <arquivo>`
existe para quem preferir um arquivo com permissão fechada em vez da variável.

**Sem a chave configurada, o lobby roda ABERTO** — qualquer cliente entra com o
nome que quiser, como era antes das contas. O servidor grita isso no boot:

```
!! LOBBY ABERTO: sem RBF_INTERNAL_API_KEY, nenhum token e verificado.
```

⚠️ **O lobby ainda é h2c, sem TLS.** Agora que o token trafega por ele, quem
estiver no caminho consegue lê-lo e se passar pelo jogador até ele expirar. Isso
é o que falta para a autenticação valer de ponta a ponta — ver `PENDENCIAS.md`.

## Onde o resultado da partida vai parar

A cadeia inteira, do jogo até o disco. Ninguém digita nada em lugar nenhum: o
placar e os personagens saem da RAM do jogo.

```
  emulador            launcher              servidor            disco
  --------            --------              --------            -----
  lê a RAM  ──> rbf-result-<id>.txt ──> ClientMsg.match_result ──> resultados.jsonl
  a cada frame        (arquivo,            (gRPC, no stream       (uma linha
                       some ao ser          que já existe)         por sessão)
                       lido)
```

**1. O emulador.** `fbneo/match_score.cpp` lê a vida dos dois lados a cada
frame. Cada **partida** terminada (melhor de três na máquina) vira uma linha
com os personagens daquela partida e o placar de rounds dela. Isso importa
porque **os dois lados escolhem de novo entre uma partida e outra**: guardar só
o total daria a todas as partidas os personagens da última.

**2. O arquivo.** Ao fim da sessão o emulador escreve, ao lado do executável:

```
match=7f3a91c204bb
game=sf2ce
p1games=3
p2games=2
games=5
firstto=3
reason=limit
chars=1
partida1=p1chars=4 p2chars=9 rounds=2-1 vencedor=p1 frames=4210
partida2=p1chars=4 p2chars=11 rounds=2-1 vencedor=p1 frames=3980
partida3=p1chars=15 p2chars=4 rounds=0-2 vencedor=p2 frames=2940
partida4=p1chars=10 p2chars=4 rounds=1-2 vencedor=p2 frames=5110
partida5=p1chars=4 p2chars=1 rounds=2-0 vencedor=p1 frames=3300
```

Arquivo, e não um cano entre os dois processos, porque os casos interessantes
são os feios: a janela fechada no X, a sessão terminando por desconexão, o
launcher ocupado. Um arquivo sobrevive a todos, e se algo der errado ele está
lá para uma pessoa ler. Quem lê, apaga.

`reason` é `limit` (alguém bateu o FT), `disconnect` ou `closed`. Um lado que
não deu para ler não inventa número: o campo simplesmente não aparece.

**3. O launcher** lê o arquivo quando o processo do emulador morre e manda um
`MatchResult` pelo stream que já está aberto. **O resultado vai antes do
`Phase.Ended`** — o `Ended` aposenta a partida no servidor, e resultado de
partida aposentada era resultado no lixo.

**4. O servidor** aceita o resultado só de quem jogou aquela partida. A
**primeira leitura que chegar é gravada** e pronto — não se espera confirmação
do outro lado, porque um launcher que travou não pode custar o registro de uma
sessão que aconteceu. A segunda leitura só é comparada: se discordar, sai um
aviso no log (dessync ou cliente adulterado valem saber), mas o que ficou
gravado fica. No console:

```
# resultado 7f3a91c204bb sf2ce: cyborgrj 3 x 2 fulano (5 partidas, limit, FT3)  [de cyborgrj]
     1. Ryu            2 x 1 Rose           -> cyborgrj
     2. Ryu            2 x 1 Sagat          -> cyborgrj
     3. Dhalsim        0 x 2 Chun Li        -> fulano
     4. M. Bison       1 x 2 Chun Li        -> fulano
     5. Ryu            2 x 0 Ken            -> cyborgrj
```

O **id** do personagem é o que fica gravado; o nome é enfeite de leitura
(`Characters.cs`). Essa ordem é de propósito: um elenco completado depois deixa
partidas antigas legíveis sem mexer em nenhuma linha já gravada.

**5. O disco.** `--results <arquivo>` (padrão `resultados.jsonl`, `-` desliga).
Um objeto JSON por linha, gravado na hora em que a primeira leitura chega.

```json
{"v":1,"match_id":"7f3a91c204bb","game":"sf2ce","started":"…","ended":"…",
 "first_to":3,"reason":"limit","p1":{"user_id":"…","username":"cyborgrj"},
 "p2":{"user_id":"…","username":"fulano"},"p1_games":3,"p2_games":2,"games":5,
 "winner":1,"games_played":[{"index":1,"p1_chars":[4],"p1_names":["Ryu"],
 "p2_chars":[9],"p2_names":["Rose"],"p1_rounds":2,"p2_rounds":1,"winner":1,
 "frames":4210,"seconds":70}],"reported_by":"cyborgrj"}
```

Uma linha é um registro completo: importar isso para o Postgres depois é um
laço sobre linhas, e uma queda no meio custa a última partida em vez de todas.
O arquivo continua sendo útil depois do banco existir — é o que se lê quando a
importação parece errada.

## Cada partida vira uma linha no Django

Quando o resultado da sessão chega, o servidor manda **um POST por partida
terminada**, na ordem em que foram jogadas:

```
POST {api}/api/internal/matches/report/
X-API-KEY: <segredo compartilhado>

{"game_code":"sf2ce","player1_id":1,"player2_id":2,
 "player1_character":"ryu","player2_character":"guile",
 "player1_score":2,"player2_score":1,"winner_id":1,"duration_seconds":185}

201 {"status":"recorded","match_id":42,
     "elo_update":{"player1":{"before":1000,"after":1025,"diff":25}, ...}}
```

Uma sessão de cinco partidas são **cinco linhas**, não uma. Os dois lados
escolhem de novo entre uma partida e outra, então resumir jogaria fora
exatamente aquilo de que as estatísticas tratam.

Três decisões que valem dizer em voz alta:

**Empate manda `winner_id: null`.** Um duplo KO no match point não é de ninguém,
e inventar um vencedor ali corromperia a ficha dos dois jogadores em silêncio.

**KOF é 3x3 e não cabe no formato.** `player1_character` leva o personagem de
**ponta** — o que entrou primeiro — para o matchup continuar legível, e o time
inteiro vai num campo extra `player1_characters` / `player2_characters`. Esse
campo só aparece nos jogos de time; todos os outros mandam exatamente os nove
campos combinados. ⚠️ Se o serializer do Django recusar campo desconhecido, é só
avisar que eu tiro.

**O ELO da resposta é aplicado na hora.** O rank que aparece na sala vira o que
o Django acabou de escrever, em vez do que veio do login uma hora atrás.

Quando o Django não responde, a partida **não se perde**: ela já está no
`resultados.jsonl` antes deste POST sair. O custo é uma reimportação, não uma
sessão.

Se o lobby estiver rodando aberto (sem `RBF_INTERNAL_API_KEY`) não há id de
conta de ninguém, e o servidor diz isso e não reporta.

### Código de personagem

O `player1_character` é o nome em minúsculas, sem pontos, com espaço virando
`_`. Um id que ainda não tem nome vai como **número** — que é o mesmo valor que
o emulador leu, então a linha fica certa e só o rótulo melhora quando o elenco
for completado. Django precisa da mesma lista:

| jogo | código |
|------|--------|
| `sf2ce` | 4 `ryu` · 5 `e_honda` · 6 `ken` — o resto vai como número |
| `sfa2` | 0 `ryu` · 1 `ken` · 2 `akuma` · 3 `nash` · 4 `chun_li` · 5 `adon` · 6 `sodom` · 7 `guy` · 8 `birdie` · 9 `rose` · 10 `m_bison` · 11 `sagat` · 12 `dan` · 13 `sakura` · 14 `rolento` · 15 `dhalsim` · 16 `zangief` · 17 `gen` |
| `kof98` | 0 `kyo` · 1 `benimaru` · 2 `daimon` · 18 `kim` · 19 `choi` · 20 `chang` · 27 `iori` · 28 `mature` · 29 `vice` |
| `vsav` | 22 `l_raptor` · 36 `jedah` — e o lado do P2 ainda não é legível |

`ssf2t` e `kof2002` ainda não têm endereços mapeados: o emulador não lê placar
nem personagem neles, então não há o que reportar. Entram quando passarem pelo
mesmo trabalho que os quatro atuais (ver `tools/README.md`).

## Testes

```bash
dotnet run --project VerifyTest
```

42 checagens do `TokenVerifier` e do `MatchReporter` contra um Django de
mentira: token válido, expirado, `X-API-KEY` errada, Django mudo, um `200` que
diz `valid:false`, resposta sem `user`, o corpo do report campo a campo, empate
sem vencedor, time de KOF e código de personagem. Não sobe rede nenhuma.

```bash
# um terminal
RBF_INTERNAL_API_KEY=chave-de-teste \
  dotnet run --project RbfServer -- --port 50061 --api-url http://localhost:8099 --results teste.jsonl

# outro
dotnet run --project RbfProtoTest -- 127.0.0.1:50061 teste.jsonl
```

40 checagens contra o servidor **de verdade**. O próprio `RbfProtoTest` sobe um
Django de mentira em `localhost:8099`, então dá para ver o servidor recusar um
token falso, aceitar um bom, entrar no lobby com o nome que o Django devolveu em
vez do que o cliente declarou, e mandar as três partidas da sessão para
`/api/internal/matches/report/` com placar, duração e código de personagem. Sem
a variável de ambiente o servidor roda aberto e essas partes se dizem não
verificadas em vez de falhar.

## Scope now / next

- **Now:** in-memory, username-only login (no password), one server.
- **Next:** launcher client + login UI + room player list + challenge dialog;
  the `fbneo.exe` cmdline patch that calls `FbnHostStart`; then persistence (DB)
  and auth.
