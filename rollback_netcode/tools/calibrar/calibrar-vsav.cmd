@echo off
setlocal
cd /d "%~dp0"
echo.
echo Calibragem de vsav - jogue uma partida inteira e feche o emulador normalmente.
echo.
fbneo64d.exe vsav -w -noscan -rbfprobe
echo.
echo Arquivos gerados:
dir /b rbf-probe-vsav-*.rbfp 2>nul || echo   (nenhum - veja rbf-netplay.log)
echo.
pause
