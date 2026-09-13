@echo off
setlocal
cd /d "%~dp0"

echo.
echo   Calibragem de vsav (Vampire Savior)
echo   ===================================
echo.
echo   A vida e os fins de round do vsav ja estao mapeados. Falta o ELENCO
echo   (so L. Raptor e Jedah tem nome) e o personagem do lado P2, que nunca
echo   foi lido porque todas as gravacoes ate hoje foram contra a CPU.
echo.
echo     1. Luta contra a CPU    - so se quiser conferir a vida de novo
echo     2. Passeio na selecao   - acha o elenco (anote a ordem que voce seguir)
echo     3. Luta 2 humanos       - acha o personagem DOS DOIS lados
echo.
echo   Para o vsav, faca a 2 e a 3.
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
echo   No vsav o cursor da a volta na grade - siga uma linha de cada vez e
echo   anote tambem se passou por algum escondido (Oboro, Shadow, Marionette).
echo.
pause
goto roda

:dois
echo.
echo   Dois jogadores no mesmo teclado, personagens DIFERENTES, um round basta.
echo   Anote quem escolheu o que, e de que lado (P1 / P2).
echo.
echo   Se der, grave DUAS lutas com duplas diferentes (ex.: Morrigan x Demitri,
echo   depois Felicia x Bulleta): com dois pares, um endereco que acerta por
echo   acidente nao passa. Foi o que enganou a gente no sf2ce.
echo.
pause
goto roda

:roda
echo.
fbneo64d.exe vsav -w -noscan -rbfprobe
echo.
echo   Arquivos gerados:
dir /b rbf-probe-vsav-*.rbfp 2>nul || echo     (nenhum - veja rbf-netplay.log)
echo.

:fim
pause
