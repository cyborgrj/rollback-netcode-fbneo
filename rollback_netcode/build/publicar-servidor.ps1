# ---------------------------------------------------------------------------
# publicar-servidor.ps1 - gera o RbfServer para a Lightsail (Linux) e empacota.
#
#   & "D:\Rollback Netcode\rollback_netcode\build\publicar-servidor.ps1"
#
# Compila AQUI, no Windows, e nao na instancia: a Lightsail e pequena e ja
# travou no dotnet publish. Sai self-contained (o runtime .NET vai junto), entao
# o servidor nao precisa ter .NET instalado.
#
# Resultado: rollback_netcode\net\publish-server\rbfserver-linux.tar.gz
# O pacote NAO leva .env nem resultados.jsonl - ao extrair por cima da pasta do
# servidor, a chave e o historico de partidas de la ficam intactos.
#
# No fim ele imprime os comandos para mandar e instalar.
# ---------------------------------------------------------------------------
param(
    [string]$Ip  = "SEU_IP",
    [string]$Pem = "LightsailDefaultKey.pem"
)

$ErrorActionPreference = "Stop"

$rbn  = Split-Path -Parent $PSScriptRoot           # rollback_netcode
$net  = Join-Path $rbn "net"
$out  = Join-Path $net "publish-server"
$bin  = Join-Path $out "rbfserver"
$tgz  = Join-Path $out "rbfserver-linux.tar.gz"

if (Test-Path $bin) { Remove-Item -Recurse -Force $bin }
New-Item -ItemType Directory -Force $bin | Out-Null

Write-Host ":: dotnet publish (linux-x64, self-contained)"
dotnet publish (Join-Path $net "RbfServer\RbfServer.csproj") -c Release -r linux-x64 --self-contained true -o $bin -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "!! o publish falhou" -ForegroundColor Red; exit 1 }

# Nada que seja do servidor de la pode ir no pacote.
foreach ($f in "resultados.jsonl", ".env") {
    $p = Join-Path $bin $f
    if (Test-Path $p) { Remove-Item -Force $p }
}

Write-Host ":: empacotando"
if (Test-Path $tgz) { Remove-Item -Force $tgz }
tar -czf $tgz -C $bin .
if ($LASTEXITCODE -ne 0) { Write-Host "!! o tar falhou" -ForegroundColor Red; exit 1 }

$mb = [math]::Round((Get-Item $tgz).Length / 1MB, 1)
$commit = (git -C (Split-Path -Parent $rbn) rev-parse --short HEAD 2>$null)

Write-Host ""
Write-Host ":: pronto: $tgz  ($mb MB, commit $commit)"
Write-Host ""
Write-Host "Mandar para a Lightsail (PowerShell, aqui):"
Write-Host "  scp -i `"$Pem`" `"$tgz`" ubuntu@${Ip}:/home/ubuntu/"
Write-Host ""
Write-Host "Instalar (na Lightsail, via ssh):"
Write-Host "  sudo systemctl stop rbfserver 2>/dev/null; mkdir -p ~/rbfserver && tar -xzf ~/rbfserver-linux.tar.gz -C ~/rbfserver && chmod +x ~/rbfserver/RbfServer && sudo systemctl start rbfserver && sudo systemctl status rbfserver --no-pager"
