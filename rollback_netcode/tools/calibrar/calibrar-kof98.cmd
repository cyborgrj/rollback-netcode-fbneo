@echo off
setlocal
cd /d "%~dp0"

echo.
echo   Calibragem de kof98 (The King of Fighters '98)
echo   ==============================================
echo.
echo   A vida e o placar do kof98 ja estao mapeados, e o time de 3 e lido dos
echo   dois lados. Falta o ELENCO: so 9 de uns 38 personagens tem nome - os
echo   outros vao para o banco como numero.
echo.
echo     1. Luta contra a CPU    - so se quiser conferir o placar de novo
echo     2. Passeio na selecao   - acha o elenco (anote a ordem que voce seguir)
echo     3. Luta 2 humanos       - confirma os TIMES dos dois lados
echo.
echo   Para o kof98, faca a 2 e a 3.
echo.
set /p OPCAO=  Qual (1/2/3)?

if "%OPCAO%"=="1" goto luta
if "%OPCAO%"=="2" goto selecao
if "%OPCAO%"=="3" goto dois
echo   Opcao invalida.
goto fim

:luta
echo.
echo   Jogue uma partida INTEIRA (ate um time inteiro cair) e feche o emulador
echo   normalmente. Contra a CPU serve.
echo.
pause
goto roda

:selecao
echo.
echo   Entre na tela de selecao com o P1 e passe o cursor por TODOS os
echo   personagens, devagar, parando um instante em cada um. Nao escolha
echo   ninguem ate o fim do passeio.
echo.
echo   ANOTE a ordem que voce seguiu - sem ela a gravacao nao diz nome nenhum.
echo   A grade do kof98 e grande: siga uma linha inteira de cada vez, da
echo   esquerda para a direita, e anote onde cada linha comeca. Se o tempo
echo   acabar antes, tudo bem - anote ate onde chegou e grave de novo
echo   comecando dali.
echo.
echo   Os personagens escondidos (Shingo, os chefes, as versoes alternativas)
echo   so aparecem com codigo; se aparecerem no passeio, anote tambem.
echo.
pause
goto roda

:dois
echo.
echo   Dois jogadores no mesmo teclado, cada um monta um time de 3.
echo   Use personagens DIFERENTES nos dois times, e de preferencia alguns que
echo   ja tem nome (Kyo, Benimaru, Daimon, Kim, Choi, Chang, Iori, Mature, Vice)
echo   junto com outros que ainda nao tem.
echo.
echo   ANOTE, dos dois lados (P1 e P2), os 3 personagens NA ORDEM DE LUTA
echo   (1o, 2o, 3o) e quem venceu. Jogue ate um time inteiro cair.
echo.
echo   Se der, grave uma segunda partida com os times trocados de lado.
echo.
pause
goto roda

:roda
echo.
fbneo64d.exe kof98 -w -noscan -rbfprobe
echo.
echo   Arquivos gerados:
dir /b rbf-probe-kof98-*.rbfp 2>nul || echo     (nenhum - veja rbf-netplay.log)
echo.

:fim
pause
