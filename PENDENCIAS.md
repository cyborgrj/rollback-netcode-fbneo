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

## Emulador: a barra depende do blitter

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
