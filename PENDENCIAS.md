# Pendências

Coisas conhecidas que não estão feitas, para não se perderem no meio de uma
conversa. O plano das partes 4 e 5 está em [PLANO-SITE.md](PLANO-SITE.md).

## Launcher: cair do servidor deixa a sala mentindo

Ao perder a conexão, `MainWindow.Disconnect()` limpa a lista de jogadores. A
sala continua aberta e **parece vazia** — indistinguível de uma sala onde
ninguém está. O usuário não tem como saber que o vazio é ele que saiu.

Reconectar já re-entra na sala sozinho (`MainWindow.xaml.cs`, no `LoginOk`), então
o que falta é mostrar o estado, não mudar o comportamento:

- enquanto desconectado, a sala diz que está desconectada e a lista fica
  esmaecida em vez de vazia;
- um botão de voltar aos jogos, como saída, em vez de expulsar o usuário
  sozinho — uma queda de dois segundos não deveria custar o contexto.

⚠️ Verificar também se o re-entrar realmente funciona: houve um relato de
reconectar e continuar sem ver ninguém. Reproduzir parando o `rbfserver`.

## Placar sobe mesmo sem o P2 ter entrado na luta

**Baixa prioridade, mas é acabamento que o produto precisa.**

Irmão do problema da desconexão (esse já resolvido, em `b8d41c5`): se o P2
conecta mas nunca aperta start, o P1 joga contra a CPU e ganha partida após
partida, com o placar contando tudo. Ninguém precisa estar de má fé para isso
acontecer — basta sair da frente do computador.

Duas formas de checar, da mais barata para a mais precisa:

1. **Input não-nulo dos dois lados.** O emulador já recebe os inputs
   sincronizados dos dois jogadores em todo frame. Se o lado do P2 foi zero a
   sessão inteira, ninguém está lá. Não depende de jogo nenhum.
2. **O bit de Start por jogador.** O `buildInputMap` em `fbneo_host.cpp` já
   percorre os inputs do driver **por nome** (`"P2 Start"`), então dá para
   guardar o índice do bit de start de cada lado e exigir que ele tenha sido
   pressionado. Mais preciso, e continua sem tabela por jogo.

A regra deve valer para contar **partida**, não para desenhar a barra: mostrar
o nome do oponente que está conectado e parado está certo; contar vitória
contra ele não.

A barra nítida só existe no DirectX 9 (`patches/fbneo/0006`). Em DirectDraw cai
no desenho dentro da imagem do jogo, que é borrado. O padrão do build agora é
DX9 e o `d3dx9_43.dll` vai junto no zip, então na prática todo mundo cai no
caminho bom — mas quem trocar de blitter de propósito, não.

Plano: `GetDC` na superfície final da família DirectDraw e desenhar com GDI ali,
na resolução da janela. Aí não importa o blitter e não depende de DLL nenhuma.

## vsav: personagem do P2 não é legível

No Vampire Savior o endereço simétrico ao do personagem do P1 (`0xFF841D`) é um
campo de animação. Resolve-se sozinho na primeira partida entre **dois humanos**,
quando as duas structs são de jogadores de verdade — hoje as gravações foram
todas contra a CPU. Nada a fazer de propósito.

## Servidor: udp/50054 não respondeu no último teste

```
nat punch: WARNING game relay udp/50054 never answered
```

Não atrapalhou naquele teste porque as duas máquinas estavam na mesma rede e a
partida foi por LAN. Atrapalha no teste com o hotspot: sem o relay respondendo, o
rendezvous não consegue detectar NAT simétrico. Conferir se o `rbfserver` está de
pé e se a porta continua aberta no painel do Lightsail.

## Elenco de personagens incompleto

`sfa2` está completo (18 de 18). Faltam nomes em `sf2ce` (3 de 12), `kof98`
(9 de ~38) e `vsav`. O banco guarda o **id numérico** desde o começo, então os
nomes podem ser preenchidos depois e as partidas antigas passam a mostrá-los.
Cada partida de `kof98` entrega seis ids de uma vez.

## Banco: o próximo elo

O servidor já recebe a sessão inteira, partida a partida, e grava em
`resultados.jsonl` (ver [net/README.md](rollback_netcode/net/README.md)). O que
falta é o banco lendo esse arquivo — ou o servidor escrevendo direto nele.

Duas formas, e a escolha não é óbvia:

1. **O servidor escreve no Postgres.** Menos peças. Mas o lobby passa a
   depender do banco estar de pé para terminar uma partida.
2. **Um importador lê o `.jsonl`.** O lobby nunca fica de mãos atadas, o
   arquivo continua sendo o que se lê quando a importação parece errada, e dá
   para reimportar. Custa um processo a mais.

Enquanto não decidir, o `.jsonl` é o banco. Uma linha é um registro completo.

## Servidor no Lightsail está atrás do repositório

O `rbfserver` de lá precisa de `git pull` + `dotnet publish` + restart para
receber as partidas detalhadas. Um servidor antigo aceita o `MatchResult` e
ignora os campos que não conhece: o placar total chega, o detalhe por partida
não. Nada quebra, mas o `resultados.jsonl` fica sem o que interessa.
