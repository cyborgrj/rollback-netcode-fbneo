@echo off
setlocal
cd /d "%~dp0"

echo.
echo   Calibragem de kof2002
echo   =====================
echo.
echo   O kof2002 ainda nao tem endereco de vida nem de personagem mapeado,
echo   entao partida nele nao e pontuada. Estas tres gravacoes fecham isso.
echo   Faca as tres - uma so nao basta, foi o que enganou a gente no sf2ce.
echo.
echo     1. Luta contra a CPU    - acha a barra de vida e os fins de round
echo     2. Passeio na selecao   - acha o elenco (anote a ordem que voce seguir)
echo     3. Luta 2 humanos       - confirma o personagem DOS DOIS lados
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
echo   devagar, parando um instante em cada um. ANOTE a ordem que voce seguiu:
echo   sem ela a gravacao nao diz nome nenhum. Depois feche o emulador.
echo.
pause
goto roda

:dois
echo.
echo   Dois jogadores no mesmo teclado, personagens DIFERENTES, um round basta.
echo   Anote quem escolheu o que, e de que lado (P1 / P2).
echo.
echo   Esta e a gravacao que pega o erro que as outras nao pegam: no sf2ce o
echo   lado P1 lia o endereco certo por acidente e o P2 lia outro campo.
echo.
pause
goto roda

:roda
echo.
fbneo64d.exe kof2002 -w -noscan -rbfprobe
echo.
echo   Arquivos gerados:
dir /b rbf-probe-kof2002-*.rbfp 2>nul || echo     (nenhum - veja rbf-netplay.log)
echo.

:fim
pause
