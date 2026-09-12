# RBF Launcher

Desktop lobby for Rollback FinalBurn Neo created by CyborgRJ. Lists the supported games, shows which
ROMs you have, downloads missing ones, and boots the **patched `fbneo.exe`** on a
chosen game.

It is a **separate app** from the emulator — it does not build with it. It just
spawns `fbneo.exe <shortname>` as a child process.

## Why C# / WPF on .NET Framework 4.8

`.NET Framework 4.8` ships inside every Windows 7 SP1 … 11, so **end users install
nothing** — no .NET download, no DirectX/VC++ redist. WPF renders in software when
there is no GPU (fine for VMs). Light enough for DDR2-era machines for a static
lobby.

## Build

Needs only the **.NET SDK** on your dev box (not the Framework Developer Pack, not
Visual Studio):

```bash
winget install Microsoft.DotNet.SDK.8
```

```bash
cd rollback_netcode/launcher
dotnet build  -c Release
dotnet run    --project RbfLauncher            # dev run
dotnet publish -c Release -o out               # -> out\RbfLauncher.exe + a few DLLs
```

`Microsoft.NETFramework.ReferenceAssemblies` (a NuGet package, pulled
automatically) is what lets the SDK target net48 with no Developer Pack.

## Runtime folder

```
RBF\
  RbfLauncher.exe
  *.dll                 (System.Text.Json + deps, ~1 MB)
  src\  vsav.png kof98.png sfa2.jpg sf2ce.png     (you provide the art)
  fbneo.exe             (copy the patched emulator build here)
  roms\arcade\*.zip     (you provide the ROMs, however you obtain them)
  json_roms\*.json      (optional; enables the in-app download button)
  rbf-launcher.json     (created on first run)
```

## Game art

Lives in the project-wide [`rollback_netcode/src/`](../src/) folder — two images
per game: a ~4:3 **screenshot** for the library grid (`vampire.png`, `kof98.png`,
`sfa2.jpg`, `sf2ce.png`) and a **portrait** `<short>box` for the room screen
(`vsavbox`, `kof98box`, `sfa2box`, `sf2cebox`; `.png`/`.jpg` auto-detected). Both
are shown whole, never cropped. The csproj copies the folder next to the exe as
`src\`. Missing art → placeholder / thumbnail fallback. Details in
[`src/README.md`](../src/README.md).

## Entrar: a conta vem antes de tudo

O launcher **abre na tela de login**, não na biblioteca. Quem você é no lobby é
a sua conta do Frame Perfect, então não há nada de sensato para mostrar antes
disso — e deixar digitar um nome livre aqui daria duas identidades para a mesma
pessoa.

```
LoginWindow ──POST /api/auth/login/──> {access, refresh}
            ──GET  /api/auth/me/ ────> {id, username, nickname, ranking}
                 │
                 └─> UserSession.Current ──> MainWindow ──> entra no lobby gRPC
                                                            com session.Username
```

A API fica em `apiBaseUrl` (padrão `http://localhost:8000`), editável na própria
tela de login, em Configurações, ou no `rbf-launcher.json`. O lobby gRPC é outro
endereço e outra porta — os dois não vão morar sempre na mesma máquina.

O que fica guardado na `UserSession`: `Id`, `Username`, `DisplayName` (o
`nickname`, ou o `username` quando ele é nulo — que é o caso normal, não a
exceção), `Ranking`, `AccessToken` e `RefreshToken`.

**Os tokens só existem em memória.** Refresh token em arquivo ao lado do exe é
senha em arquivo ao lado do exe, e este launcher roda em máquina compartilhada.
Fechar o launcher desloga; é a troca pretendida.

Quando uma chamada autenticada leva `401`, o `AuthApi` troca o refresh token por
um access novo e repete a chamada **uma vez**. Se o refresh também for recusado,
a sessão acabou e o jogador volta para o login. Sem laço: um refresh que produz
um token que o servidor continua recusando é problema para avisar, não para
martelar.

O `Hello` do lobby leva o `access_token`, e o `RbfServer` pergunta ao Django se
ele vale antes de aceitar a conexão — **a identidade que vale é a que o Django
devolve**, não o `username` que o cliente declara. Detalhes em
[net/README.md](../net/README.md).

O token expira em 60 minutos e o lobby o confere na hora de conectar, então um
launcher aberto há mais de uma hora renovaria em vão na tela: antes de conectar,
se o access já passou de 40 minutos, ele é trocado por um novo.

⚠️ **O lobby é h2c, sem TLS.** O token atravessa a rede em claro. Quem estiver no
caminho consegue se passar pelo jogador até ele expirar — ver `PENDENCIAS.md`.

## Estatísticas do jogador

`GET /api/players/<username>/stats/` — rota pública, sem token. Vale para
**qualquer** jogador, não só para quem está logado: saber contra quem você vai
jogar (rank, horas naquele jogo, aproveitamento) é a maior parte do motivo de um
lobby guardar estatística.

Dois caminhos para abrir:

- **clicar no seu nome** no cabeçalho → o seu perfil;
- **clicar no nome de alguém** na lista da sala → o perfil dele, antes de
  desafiar.

A janela mostra o ranking geral, o resumo (partidas, V/D/E, aproveitamento,
tempo de jogo) e um cartão por jogo com o rank daquele jogo e uma barra de
aproveitamento — a barra existe porque um número é exato, mas quatro números não
se comparam de relance e quatro barras sim.

Duas conversões acontecem aqui e não no servidor: **os totais** (o Django manda
por jogo; somar é nosso) e o **tempo**. `0.05` horas na tela não diz nada a
ninguém; `3 min` diz.

A chamada vai **sem** o Bearer de propósito. É a única que precisa funcionar com
a sessão vencida, porque olhar o perfil de alguém é exatamente o que se faz
depois de deixar o launcher aberto a tarde inteira.

Um `404` significa "essa conta ainda não jogou" — estado normal de conta nova, e
a tela diz isso em vez de tratar como erro.

### Testar o fluxo sem Django

```bash
cd rollback_netcode/launcher
dotnet run --project AuthTest
```

33 checagens contra uma API de mentira: senha errada, API fora do ar, `500`,
token expirado no meio da sessão, refresh recusado, refresh rotacionado, e o
perfil de estatísticas (com o corpo que o Django de verdade respondeu). São
justamente os caminhos que só rodam em dia ruim — a pior hora para descobrir
que nunca estiveram certos.

## Config (`rbf-launcher.json`, next to the exe)

```json
{
  "emulatorPath": "fbneo.exe",
  "emulatorArgs": "-w",
  "romsDir": "roms\\arcade",
  "playerName": "you",
  "apiBaseUrl": "http://localhost:8000",
  "serverHost": "18.228.40.244",
  "serverPort": 50051
}
```

`playerName` agora é só o último usuário digitado no login, para a tela já vir
preenchida. Quem manda é a conta.

Relative paths resolve against the launcher folder. `romsDir` defaults to
FBNeo's own arcade default; if you point it elsewhere you must also tell FBNeo
(future: pass a rom path / write `fbneo.ini`).

**`emulatorArgs`** is appended after the game name. FBNeo boots **fullscreen**
when given only a game name, which fails on some setups; the default `-w` forces
a window. FBNeo remembers the window size / blitter you pick from its own menu in
`config\fbneo.ini` next to `fbneo.exe`. For a low fullscreen mode instead, use
`-r 800 x 600 x 32`.

## ROMs

The launcher **carries no ROM URLs**. Put the ZIP sets in `romsDir`
(`roms\arcade` by default) however you obtain them.

Optionally, if one or more rom-database JSON files are present in `json_roms\`,
the room screen's **Baixar ROM** button becomes active: it resolves the set plus
any `require` dependencies and fetches each from the URL in that file (to
`<name>.zip.part`, then moved into place). Format read:

```json
{ "<setname>": { "download": "<url>", "require": ["<dep>", "..."] } }
```

No such file → the button stays disabled and the room screen says so. The repo
ships none and `json_roms\*.json` is git-ignored.

## Online (gRPC lobby)

Header **Conectar** → server IP/port + player name (saved to `rbf-launcher.json`
as `serverHost` / `serverPort`). Run the server from
[`../net`](../net) (`dotnet run --project RbfServer`).

Once connected: entering a game room joins that room on the server; the room
screen lists other players there with a **Desafiar** button. An incoming
challenge pops a dialog; on accept both sides get a **Partida pronta** dialog
whose **Jogar** spawns `fbneo.exe <game> -w -rbfnet player=..,localport=..,peerip=..,peerport=..,delay=..`
and reports match status back. Contract + server details in
[`../net/README.md`](../net/README.md).

Networking uses `Grpc.Core` (the only gRPC stack that runs on .NET Framework on
old Windows). `RbfProtocol` (the shared proto project) is a `ProjectReference`.

## Scope now / next

- **Now:** 4 games, ROM presence + download, launch emulator, settings, gRPC
  lobby (username-only login, per-game rooms, challenge → match launch).
- **Next:** the fbneo cmdline patch is done (`patches/fbneo/0003`); still need an
  end-to-end run on two machines, then persistence (DB) + real auth.

Featured catalogue lives in `Core/Model.cs` (`GameCatalog.All`) — short name +
title + art file, no URLs. Add an entry + its art to feature another game.
