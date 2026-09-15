# ---------------------------------------------------------------------------
# montar-pacote.ps1 - monta a pasta "Frame Perfect" e o zip para o site.
#
#   & "D:\Rollback Netcode\rollback_netcode\build\montar-pacote.ps1"
#
# Padrao: API e site em https://frameperfect.cc, lobby em lobby.frameperfect.cc:50051.
# Outros enderecos: -Api "..." -Site "..." -Lobby "..."
#
# Resultado em rollback_netcode\dist\:
#
#   Frame Perfect\
#     README.md                   o que e, primeira vez, problemas comuns
#     versao.txt                  "Frame Perfect alpha_test_vX.Y.Z"
#     fbneo64d.exe
#     d3dx9_43.dll
#     fonte_metricas.ttf
#     fonte_placar.otf
#     launcher\                   o launcher publicado, com json_roms e src
#       rbf-launcher.json         enderecos de producao, emulador em ..\
#     roms\arcade, roms\snes ...  vazias - o launcher baixa as ROMs
#   Frame Perfect.zip
#
# Por que nao vai um "Frame Perfect.lnk" dentro do zip: atalho do Windows guarda
# caminho ABSOLUTO, e cada jogador extrai numa pasta diferente. O launcher cria
# o atalho na raiz ao abrir (Core\Shortcuts.cs), com o caminho de verdade; o
# .cmd so existe para essa primeira vez.
#
# O que NAO entra: zzBurnDebug.html e rbf-netplay.log (o emulador cria sozinho,
# e os daqui teriam o historico desta maquina), ROMs, .pdb.
#
# Antes: compile o emulador (compilar-emulador.sh) e publique o launcher
# (publicar-launcher.ps1) - este script so junta o que ja esta pronto.
# ---------------------------------------------------------------------------
param(
    [string]$Api   = "https://frameperfect.cc",
    [string]$Site  = "https://frameperfect.cc",
    # Um nome e nao o IP, e um subdominio proprio: o site passa pelo proxy da
    # Cloudflare (nuvem laranja), que so repassa HTTP/HTTPS - o lobby e gRPC na
    # 50051 e o jogo usa UDP, entao lobby.frameperfect.cc precisa ser um registro
    # A "somente DNS" (nuvem cinza) apontando para o IP estatico da Lightsail.
    [string]$Lobby = "lobby.frameperfect.cc",
    [int]$Port     = 50051,
    # De onde vem o que nao e compilado aqui (dll do DirectX e as fontes).
    [string]$Assets = "D:\RBF"
)

$ErrorActionPreference = "Stop"

$rbn      = Split-Path -Parent $PSScriptRoot           # rollback_netcode
$repo     = Split-Path -Parent $rbn
$dist     = Join-Path $rbn "dist"
$pkg      = Join-Path $dist "Frame Perfect"
$emu      = Join-Path $repo "fbneo\fbneo64d.exe"
$launcher = Join-Path $rbn "launcher\publish"

foreach ($need in @($emu, (Join-Path $launcher "RbfLauncher.exe"),
                    (Join-Path $Assets "d3dx9_43.dll"),
                    (Join-Path $Assets "fonte_metricas.ttf"),
                    (Join-Path $Assets "fonte_placar.otf"))) {
    if (-not (Test-Path $need)) { Write-Host "!! falta: $need" -ForegroundColor Red; exit 1 }
}

# A versao vem do proprio launcher publicado ("alpha_test_v1.0.0", definida no
# RbfLauncher.csproj) - o zip nunca pode dizer uma versao que o exe nao tem.
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $launcher "RbfLauncher.exe")).ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { Write-Host "!! o launcher publicado nao tem versao" -ForegroundColor Red; exit 1 }
# Nome fixo: a versao fica no versao.txt e no titulo das janelas, nao no nome
# do arquivo (um nome por versao mudaria o link do site e acumularia zips).
$zipName = "Frame Perfect.zip"
$zip     = Join-Path $dist $zipName

if (Test-Path $pkg) { Remove-Item -LiteralPath $pkg -Recurse -Force }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

Write-Host ":: raiz"
Copy-Item $emu $pkg
foreach ($f in "d3dx9_43.dll", "fonte_metricas.ttf", "fonte_placar.otf") {
    Copy-Item (Join-Path $Assets $f) $pkg
}

Write-Host ":: launcher"
$ld = Join-Path $pkg "launcher"
New-Item -ItemType Directory -Force $ld | Out-Null
Copy-Item -Path (Join-Path $launcher "*") -Destination $ld -Recurse -Force
Get-ChildItem $ld -Recurse -File | Where-Object {
    $_.Extension -eq ".pdb" -or $_.Name -eq "rbf-launcher.log" -or $_.Name -eq "rbf-launcher.json"
} | Remove-Item -Force

# Configuracao inicial. Os nomes batem com AppConfig (Core\Model.cs); o jogador
# muda o que quiser em Configuracoes depois.
$cfg = [ordered]@{
    emulatorPath = "..\fbneo64d.exe"
    romsDir      = ""
    emulatorArgs = "-w -noscan"
    playerName   = ""
    serverHost   = $Lobby
    serverPort   = $Port
    apiBaseUrl   = $Api
    siteBaseUrl  = $Site
}
$cfg | ConvertTo-Json | Set-Content -Path (Join-Path $ld "rbf-launcher.json") -Encoding UTF8

Write-Host ":: roms (pastas vazias)"
# As mesmas pastas que o FBNeo cria na primeira execucao.
foreach ($d in "arcade", "astrocade", "channelf", "coleco", "fds", "gamegear", "gba",
               "megadrive", "msx", "nes", "ngp", "pce", "romdata", "sg1000", "sgx",
               "sms", "snes", "spectrum", "tg16") {
    New-Item -ItemType Directory -Force (Join-Path $pkg "roms\$d") | Out-Null
}

# Sem .cmd no pacote. Os atalhos sao criados pelo proprio launcher ao abrir
# (Core\Shortcuts.cs). Duas vezes o .cmd deu problema em 14/09:
#   - com powershell dentro, o Defender marcou o zip como Trojan:Script/Sabsik
#     e o Chrome bloqueou o download;
#   - so com "start", o Smart App Control do Windows 11 ainda bloqueou o
#     cmd.exe: ele barra QUALQUER script baixado da internet sem assinatura
#     (Code Integrity 3033 no log), e "executar como administrador" nao libera.
# Na primeira vez o jogador abre launcher\RbfLauncher.exe - esta no README.

# Quem abre a pasta sabe qual versao tem, sem abrir o launcher.
Set-Content -Path (Join-Path $pkg "versao.txt") -Value "Frame Perfect $version" -Encoding ASCII

Write-Host ":: README.md"
$readme = @'
# Frame Perfect

Jogos de luta clássicos online, com rollback netcode: a partida responde como
se os dois jogadores estivessem no mesmo fliperama, mesmo com a distância.

Versão: **{VERSAO}** · site: {SITE}

## Primeira vez

1. Extraia o zip numa pasta sua, por exemplo `Documentos\Frame Perfect`.
   Não rode direto de dentro do zip.
2. Abra a pasta **`launcher`** e dê dois cliques em **`RbfLauncher.exe`**.
   Nessa primeira vez ele cria o atalho **Frame Perfect** na área de trabalho
   e nesta pasta. Daí em diante, use o atalho.
3. Entre com a conta criada no site ({SITE}).

Se mover a pasta de lugar, abra o `launcher\RbfLauncher.exe` uma vez: ele
corrige os atalhos sozinho.

## Jogando

- Escolha um jogo na biblioteca para entrar na sala dele. Quem estiver na
  mesma sala aparece na lista; clique em **Desafiar**.
- Na hora do desafio se combina o atraso de entrada (delay) e o limite de
  partidas (FT). Quando alguém chega ao limite, o emulador mostra o placar e
  fecha sozinho.
- O launcher baixa o jogo da sala na primeira vez que for preciso.
- Clique no nome de um jogador para ver as estatísticas dele.
- Durante a partida, **Backspace** mostra/esconde o painel de ping, atraso,
  rollback e fps.

## Requisitos

- Windows 10 ou 11, 64 bits.
- .NET Framework 4.8 (já vem no Windows 10 e 11).

## O que tem nesta pasta

| arquivo / pasta | para que serve |
|---|---|
| `launcher\` | o launcher (login, salas, desafios) |
| `fbneo64d.exe` | o emulador, aberto pelo launcher na hora da partida |
| `roms\` | onde os jogos baixados ficam |
| `versao.txt` | a versão deste pacote |

## Problemas

- **"O Windows protegeu o computador"** (tela azul do SmartScreen): clique em
  **Mais informações** e depois em **Executar assim mesmo**.
- **"O Controle de Aplicativo Inteligente bloqueou um aplicativo"** (Windows
  11): esse bloqueio não tem botão para liberar, nem como administrador. O
  Frame Perfect ainda não tem assinatura digital, e o Windows desconfia de
  programas novos sem ela; costuma passar depois de algumas horas, quando o
  arquivo fica conhecido. Avise o suporte com a versão (`versao.txt`).
- **Não conecta ao lobby**: em Configurações, o lobby deve ser
  `{LOBBY}`, porta `{PORTA}` — sem `https://`.
- **Qualquer outro erro**: mande para o suporte a versão (`versao.txt`) e estes
  dois arquivos, que dizem o que aconteceu:
  - `launcher\rbf-launcher.log`
  - `rbf-netplay.log` (aparece depois da primeira partida)

Frame Perfect por CyborgRJ. Emulação por FinalBurn Neo.
'@
$readme = $readme.Replace("{VERSAO}", $version).Replace("{SITE}", $Site).Replace("{LOBBY}", $Lobby).Replace("{PORTA}", "$Port")
[IO.File]::WriteAllText((Join-Path $pkg "README.md"), $readme, (New-Object Text.UTF8Encoding($false)))

Write-Host ":: zip"
# Pastas vazias nao entram num Compress-Archive; o tar do Windows as mantem.
Push-Location $dist
try { tar -a -cf $zipName "Frame Perfect" } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { Write-Host "!! o zip falhou" -ForegroundColor Red; exit 1 }

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host ":: pronto: $zip  ($mb MB, $version)"
Write-Host "   lobby $Lobby`:$Port   api $Api   site $Site"
Write-Host ""
Write-Host "Subir para o site (substitui o anterior; o link de download nao muda):"
Write-Host "  scp -i `"`$env:USERPROFILE\.ssh\LightsailDefaultKey-sa-east-1.pem`" `"$zip`" ubuntu@56.126.42.71:/home/ubuntu/FramePerfect/downloads/FramePerfect-Launcher.zip"
