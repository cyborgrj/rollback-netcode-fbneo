@echo off
setlocal
cd /d "%~dp0"
echo.
echo Calibragem de kof98 - jogue uma partida inteira e feche o emulador normalmente.
echo.
fbneo64d.exe kof98 -w -noscan -rbfprobe
echo.
echo Arquivos gerados:
dir /b rbf-probe-kof98-*.rbfp 2>nul || echo   (nenhum - veja rbf-netplay.log)
echo.
pause
