# ---------------------------------------------------------------------------
# publicar-launcher.ps1 - publica o launcher e copia para D:\RBF\launcher,
# guardando o anterior, numerado.
#
# Rode no PowerShell, de qualquer pasta:
#
#   & "D:\Rollback Netcode\rollback_netcode\build\publicar-launcher.ps1"
#
# O que ele faz, nesta ordem:
#
#   1. guarda o launcher que esta em D:\RBF\launcher num .zip numerado em
#      rollback_netcode\launcher\backups\ (nunca na pasta do produto);
#   2. dotnet publish para rollback_netcode\launcher\publish;
#   3. copia o publish inteiro por cima de D:\RBF\launcher;
#   4. anota o build novo em launcher\backups\builds.txt.
#
# O que NAO e tocado no destino: rbf-launcher.json (os enderecos e o nome de
# quem usa - o publish nem gera esse arquivo) e roms\. Nada e apagado.
#
# builds.txt, igual ao do emulador:
#
#   b002  2026-09-13  13:25  3F0A12BC  590efde  (limpo)
#
# "(+alteracoes-locais)" = havia mudanca nao commitada; o commit sozinho nao
# reproduz aquele build.
#
# Outro destino:  ...\publicar-launcher.ps1 -Destino "E:\outra\pasta\launcher"
# ---------------------------------------------------------------------------
param(
    [string]$Destino = "D:\RBF\launcher"
)

$ErrorActionPreference = "Stop"

$rbn      = Split-Path -Parent $PSScriptRoot          # rollback_netcode
$repo     = Split-Path -Parent $rbn                   # raiz do repositorio
$launcher = Join-Path $rbn "launcher"
$publish  = Join-Path $launcher "publish"
$bak      = Join-Path $launcher "backups"
$log      = Join-Path $bak "builds.txt"
$exeName  = "RbfLauncher.exe"

New-Item -ItemType Directory -Force $bak | Out-Null
if (-not (Test-Path $log)) { New-Item -ItemType File $log | Out-Null }

function Hash8([string]$f) { (Get-FileHash -Algorithm SHA256 $f).Hash.Substring(0, 8) }

# O numero de um executavel e a linha do builds.txt com o mesmo hash.
function NumeroDe([string]$f) {
    $h = Hash8 $f
    $n = $null
    foreach ($l in Get-Content $log) {
        $c = $l -split '\s+'
        if ($c.Count -ge 4 -and $c[3] -eq $h) { $n = $c[0] }
    }
    return $n
}

function ProximoNumero {
    $m = 0
    foreach ($l in Get-Content $log) {
        if ($l -match '^b(\d+)') { $v = [int]$Matches[1]; if ($v -gt $m) { $m = $v } }
    }
    return ('b{0:D3}' -f ($m + 1))
}

function Anotar([string]$num, [string]$f, [string]$commit, [string]$estado) {
    $t = (Get-Item $f).LastWriteTime
    $linha = '{0}  {1}  {2}  {3}  {4}  {5}' -f $num, $t.ToString('yyyy-MM-dd'), $t.ToString('HH:mm'), (Hash8 $f), $commit, $estado
    Add-Content -Path $log -Value $linha -Encoding ASCII
}

# ---- 0. o launcher nao pode estar aberto --------------------------------------
# Com ele aberto o RbfLauncher.exe fica travado, a copia sai pela metade e o
# destino fica com o exe velho e DLLs novas.
if (Get-Process -Name "RbfLauncher" -ErrorAction SilentlyContinue) {
    Write-Host "!! O launcher esta aberto. Feche e rode de novo." -ForegroundColor Red
    exit 1
}

# ---- 1. backup do atual -------------------------------------------------------
$destExe = Join-Path $Destino $exeName
if (Test-Path $destExe) {
    $num = NumeroDe $destExe
    if (-not $num) {
        # Publicado antes deste script existir: ganha um numero agora.
        $num = ProximoNumero
        Anotar $num $destExe "-------" "(anterior-ao-script)"
    }
    $t = (Get-Item $destExe).LastWriteTime.ToString('yyyyMMdd-HHmm')
    $zip = Join-Path $bak ("RbfLauncher-{0}-{1}-{2}.zip" -f $num, $t, (Hash8 $destExe))
    if (Test-Path $zip) {
        Write-Host ":: backup do $num ja existe: $(Split-Path -Leaf $zip)"
    } else {
        # So os arquivos do programa. roms\, src\ e json_roms\ nao mudam com o
        # codigo, e a configuracao de quem usa nao e parte do build.
        $arquivos = Get-ChildItem $Destino -File | Where-Object { $_.Name -ne "rbf-launcher.json" }
        Compress-Archive -Path $arquivos.FullName -DestinationPath $zip
        Write-Host ":: backup: $(Split-Path -Leaf $zip)"
    }
}

# ---- 2. publish ---------------------------------------------------------------
Write-Host ":: dotnet publish"
dotnet publish (Join-Path $launcher "RbfLauncher\RbfLauncher.csproj") -c Release -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "!! o publish falhou - nada foi copiado para $Destino" -ForegroundColor Red
    exit 1
}

# ---- 3. copiar ----------------------------------------------------------------
New-Item -ItemType Directory -Force $Destino | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $Destino -Recurse -Force
Write-Host ":: copiado para $Destino"

# ---- 4. anotar o novo ---------------------------------------------------------
$novoExe = Join-Path $publish $exeName
$ja = NumeroDe $novoExe
if ($ja) {
    Write-Host ""
    Write-Host ":: o launcher nao mudou (mesmo hash do $ja) - nada novo a anotar."
    exit 0
}

$num = ProximoNumero
$commit = (git -C $repo rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = "-------" }
$estado = "(limpo)"
if (git -C $repo status --porcelain 2>$null) { $estado = "(+alteracoes-locais)" }
Anotar $num $novoExe $commit $estado

Write-Host ""
Write-Host ":: build $num do launcher pronto em $Destino"
Write-Host ("   hash {0}   commit {1} {2}" -f (Hash8 $novoExe), $commit, $estado)
