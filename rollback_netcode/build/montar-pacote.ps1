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
#     Abrir Frame Perfect.cmd     cria os atalhos (area de trabalho e raiz) e abre
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
$zip      = Join-Path $dist "Frame Perfect.zip"
$emu      = Join-Path $repo "fbneo\fbneo64d.exe"
$launcher = Join-Path $rbn "launcher\publish"

foreach ($need in @($emu, (Join-Path $launcher "RbfLauncher.exe"),
                    (Join-Path $Assets "d3dx9_43.dll"),
                    (Join-Path $Assets "fonte_metricas.ttf"),
                    (Join-Path $Assets "fonte_placar.otf"))) {
    if (-not (Test-Path $need)) { Write-Host "!! falta: $need" -ForegroundColor Red; exit 1 }
}

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

Write-Host ":: atalho da primeira vez"
# Cria "Frame Perfect.lnk" na area de trabalho e nesta pasta, com o caminho de
# onde o zip foi extraido, e abre o launcher. A pasta vai numa variavel e nao
# colada no comando: um caminho com espaco, acento ou apostrofo quebraria as
# aspas. GetFolderPath('Desktop') acha a area de trabalho mesmo quando o
# OneDrive a redirecionou. FP_DESKTOP / FP_TESTE existem so para testar o .cmd
# sem mexer na area de trabalho de ninguem nem abrir o launcher.
$cmd = @'
@echo off
setlocal
rem Frame Perfect - cria o atalho "Frame Perfect" na area de trabalho e nesta
rem pasta, e abre o launcher. Pode rodar de novo a qualquer momento (por
rem exemplo, depois de mover a pasta para outro lugar).
set "FP_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = $env:FP_DIR; $exe = Join-Path $d 'launcher\RbfLauncher.exe'; $desk = if ($env:FP_DESKTOP) { $env:FP_DESKTOP } else { [Environment]::GetFolderPath('Desktop') }; $sh = New-Object -ComObject WScript.Shell; foreach ($p in @((Join-Path $desk 'Frame Perfect.lnk'), (Join-Path $d 'Frame Perfect.lnk'))) { $s = $sh.CreateShortcut($p); $s.TargetPath = $exe; $s.WorkingDirectory = (Join-Path $d 'launcher'); $s.IconLocation = $exe + ',0'; $s.Description = 'Frame Perfect'; $s.Save() }"
if not defined FP_TESTE start "" "%~dp0launcher\RbfLauncher.exe"
'@
Set-Content -Path (Join-Path $pkg "Abrir Frame Perfect.cmd") -Value $cmd -Encoding ASCII

Write-Host ":: zip"
# Pastas vazias nao entram num Compress-Archive; o tar do Windows as mantem.
Push-Location $dist
try { tar -a -cf "Frame Perfect.zip" "Frame Perfect" } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { Write-Host "!! o zip falhou" -ForegroundColor Red; exit 1 }

$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host ":: pronto: $zip  ($mb MB)"
Write-Host "   lobby $Lobby`:$Port   api $Api   site $Site"
Write-Host ""
Write-Host "Subir para o site (download em /home/ubuntu/FramePerfect/downloads/):"
Write-Host "  scp -i `"`$env:USERPROFILE\.ssh\LightsailDefaultKey-sa-east-1.pem`" `"$zip`" ubuntu@56.126.42.71:/home/ubuntu/FramePerfect/downloads/FramePerfect-Launcher.zip"
