@echo off
setlocal
cd /d "%~dp0"
echo.
echo Calibragem de sf2ce - jogue uma partida inteira e feche o emulador normalmente.
echo.
fbneo64d.exe sf2ce -w -noscan -rbfprobe
echo.
echo Arquivos gerados:
dir /b rbf-probe-sf2ce-*.rbfp 2>nul || echo   (nenhum - veja rbf-netplay.log)
echo.
pause
