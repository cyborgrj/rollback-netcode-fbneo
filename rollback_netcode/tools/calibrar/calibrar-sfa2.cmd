@echo off
setlocal
cd /d "%~dp0"

echo.
echo   Calibragem de sfa2 (Street Fighter Alpha 2)
echo   ===========================================
echo.
echo   Contra a CPU o placar do sfa2 funciona. Entre dois jogadores (online)
echo   ele conta os rounds mas nunca fecha a partida - fica 0 x 0. Falta ver
echo   o que o jogo faz na memoria quando uma partida VERSUS termina.
echo.
echo     1. Luta contra a CPU    - ja esta ok, so para conferir
echo     2. Passeio na selecao   - elenco (ja esta completo)
echo     3. Luta 2 humanos       - a que falta: fim de partida no modo versus
echo.
echo   Para o sfa2, faca a 3.
echo.
set /p OPCAO=  Qual (1/2/3)?

if "%OPCAO%"=="1" goto luta
if "%OPCAO%"=="2" goto selecao
if "%OPCAO%"=="3" goto dois
echo   Opcao invalida.
goto fim

:luta
echo.
echo   Jogue uma partida INTEIRA (ate alguem vencer o set) e feche o emulador
echo   normalmente. Contra a CPU serve.
echo.
pause
goto roda

:selecao
echo.
echo   Entre na tela de selecao e passe o cursor por TODOS os personagens,
echo   devagar, parando um instante em cada um. ANOTE a ordem que voce seguiu.
echo   Depois feche o emulador.
echo.
pause
goto roda

:dois
echo.
echo   Dois jogadores no mesmo teclado, personagens DIFERENTES.
echo.
echo   Joga DUAS partidas COMPLETAS em seguida (ate alguem vencer os rounds),
echo   do jeito que acontece online: acabou uma, os dois escolhem de novo e
echo   jogam a segunda. So feche o emulador depois que a segunda terminar e a
echo   tela seguinte aparecer.
echo.
echo   Anote de cada partida: quem pegou quem, de que lado (P1 / P2), e o
echo   placar de rounds.
echo.
pause
goto roda

:roda
echo.
fbneo64d.exe sfa2 -w -noscan -rbfprobe
echo.
echo   Arquivos gerados:
dir /b rbf-probe-sfa2-*.rbfp 2>nul || echo     (nenhum - veja rbf-netplay.log)
echo.

:fim
pause
