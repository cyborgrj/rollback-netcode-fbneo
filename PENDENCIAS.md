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

`sfa2` (18 de 18) e `sf2ce` (12 de 12) estão completos. Faltam nomes em `kof98`
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


## O lobby não tem TLS, e agora carrega o token

Desde 12/09 o `Hello` leva o `access_token` do jogador, e o `RbfServer` confere
com o Django antes de aceitar. O portão fechou — mas o transporte não: o gRPC
do lobby é **h2c, sem TLS**.

Ou seja, quem estiver no caminho (Wi-Fi de café, provedor, qualquer trecho entre
o jogador e o Lightsail) lê o token e se passa por ele até expirar, em até 60
minutos. Antes disso só trafegava um nome, que não valia nada; agora trafega
uma credencial.

O que resolve: TLS no lobby. Certificado do mesmo `certbot` que atende o site,
`ServerCredentials` em vez de `Insecure` no servidor, e `ChannelCredentials`
correspondente no launcher.

⚠️ Cuidado com o launcher: ele é **net48 e usa Grpc.Core** justamente porque
`Grpc.Net.Client` não funciona no Windows velho. O Grpc.Core faz TLS pelo
BoringSSL que vem embutido, então isso deve funcionar — mas é o tipo de coisa
que só se sabe testando na VM do Windows 7/10 antigo.

## Report ao Django só sai no fim da sessão, não a cada partida

O servidor manda uma linha por partida, mas **todas de uma vez**, quando a
sessão acaba — porque é aí que o emulador escreve o `rbf-result-*.txt` e o
launcher o lê. Se o emulador morrer no meio de uma sessão de dez partidas, as
dez se perdem.

Para reportar partida a partida, ao vivo, falta um canal entre emulador e
launcher **enquanto o jogo roda** — hoje o único caminho é o arquivo na saída.
Um pipe nomeado ou um arquivo append-only que o launcher acompanhe resolveria.

Não é urgente: o dado é o mesmo nos dois casos, muda só a resistência a crash.

## `kof2002`: no menu, mas sem placar

Entrou na biblioteca em 13/09 (arte e link de ROM prontos), e dá para jogar
online nele normalmente — o rollback não depende de ler RAM nenhuma.

O que NÃO funciona: placar, personagem, ELO, estatística. Não tem endereço de
vida mapeado, então a sessão não é pontuada e nada é reportado ao Django. A
barra mostra `-` no lugar do placar, em vez de um `0 x 0` que pareceria placar
de verdade.

O `ssf2t` foi mapeado no mesmo dia com as três gravações de `tools/calibrar/` e
conferido nas duas lutas. Os dois jogos saíram por algumas horas por causa do
bloqueio do Smart App Control e voltaram quando o bloqueio passou (ver a seção
dele abaixo) — o bloqueio nunca teve relação com o código. Falta só o Akuma no
elenco do `ssf2t`.

## Armadilha: lobby em 127.0.0.1 mata a partida

Custou uma noite de teste em 12/09. O launcher descobre o proprio IP abrindo um
socket UDP na direcao do lobby e lendo o endereco de origem que o SO escolheu.
Com o lobby em `127.0.0.1`, a rota sai pelo loopback e o launcher se anuncia como
`127.0.0.1` — e o **adversario** recebe isso como destino dos pacotes do jogo.

Sintoma: tudo funciona (sala, desafio, o emulador abre nos dois lados) e cerca de
**15 segundos depois os dois emuladores fecham**, porque o GGPO nunca sincroniza e
o watchdog derruba a sessao. Nada no log aponta para a causa.

Resolvido dos dois lados:

- `LobbyClient.GuessLanIp` nunca devolve loopback quando existe placa de rede —
  cai para a enumeracao de interfaces, que esta certa nos dois casos (pacote para
  o proprio IP de LAN faz loopback local de qualquer jeito);
- o servidor avisa em voz alta quando cria uma partida com um lado em loopback e
  o outro nao, com a causa provavel e o que fazer.

⚠️ Continua valendo a regra geral: **use o endereco de rede da maquina, nao
`127.0.0.1`**, sempre que o adversario estiver em outra maquina.

## Smart App Control: executavel sem assinatura pode ser barrado

Em 13/09 o Windows 11 recusou abrir um build novo do `fbneo64d.exe` com "uma
politica de controle de aplicativo bloqueou esse arquivo". O que os registros
mostraram:

- o Smart App Control esta ligado nesta maquina desde 09/08 — nao mudou;
- nenhum build anterior tinha sido barrado;
- o evento do bloqueio (Code Integrity, id 3118) diz `IsUnfriendlyFile = false`
  e nenhuma ameaca: **o Windows nao achou o arquivo perigoso**;
- ele pediu a checagem de reputacao na nuvem e ela **nao aconteceu**
  (`DefenderMadeCloudCall = false`), com a protecao na nuvem ligada e normal.

Ou seja: todo build e um arquivo que o Windows nunca viu; sem assinatura ele
depende dessa checagem funcionar e dizer "seguro" naquele momento. Ate entao
funcionava; dessa vez nao. **Nao foi o codigo** — foi ser um arquivo novo.

O que isso significa para quem baixar o Frame Perfect: na primeira vez que abrir
cada versao, a mesma checagem. Na maioria das vezes passa; quando nao passa, o
usuario ve o bloqueio e nao tem o que fazer sem mexer no sistema.

### A unica solucao sem o usuario mexer em nada: assinar

O FBNeo do Fightcade abre sempre porque e assinado. So tres arquivos nossos nao
tem assinatura — todo o resto (DLLs da Microsoft, Google, .NET) ja vem assinado:

```
fbneo64d.exe    launcher\RbfLauncher.exe    launcher\RbfProtocol.dll
```

O `signtool.exe` ja esta instalado. Com um certificado, a assinatura entra no
build e todo build sai assinado.

Opcoes, para pessoa fisica no Brasil (pesquisado em 13/09/2026):

| opcao | serve? |
|-------|--------|
| Azure Artifact Signing | nao — pessoa fisica so EUA e Canada |
| SignPath Foundation (gratis) | nao — exige licenca OSI em tudo, e a do FBNeo e nao-comercial |
| Certum Open Source (nuvem) | talvez — mais barato, mas pode recusar por causa da licenca do FBNeo |
| OV individual na nuvem (ex.: Certum) | sim, ~€189/ano |

**Nao usar o certificado do trabalho.** Ele poe o nome do empregador como
editor do Frame Perfect, e assinar software pessoal com ele quase certamente
fere a politica da empresa.

Decisao em 13/09: **nao comprar agora**.

### O experimento que fechou a questao (13/09, 01:05)

A suspeita natural era "foi algo no codigo novo". Testado direto:

| teste | resultado |
|-------|-----------|
| build de 12/09 10:11, o arquivo original | **abre** |
| o MESMO build com 1 byte a mais no fim (codigo identico, arquivo novo) | **bloqueado** — nuvem consultada, nada suspeito |
| build de 13/09 00:41, aberto de novo sem mudar nada | **bloqueado** de novo |

O veredito vai para o **arquivo**, nao para o codigo. E tentar de novo **nao
adianta** — a primeira versao desta secao dizia que sim, e estava errada.

**Atualizacao, 13/09 01:17 — voltou ao normal sozinho.** Um build novo (hash
`A71D1956…`), arquivo que o Windows nunca tinha visto, abriu na primeira
tentativa, sem nenhum evento de bloqueio no Code Integrity. O que falhava de
madrugada (arquivo novo, mesmo com codigo identico) deixou de falhar. Tudo indica
uma condicao temporaria do lado da Microsoft; a conclusao escrita antes aqui,
"qualquer build novo sera barrado nesta maquina", nao se sustentou.

Sobre abrir o emulador sem argumentos antes do launcher: nao foi isso. O build de
01:17 nao tem evento de bloqueio nem na primeira abertura, e o Windows decide pelo
arquivo (hash, assinatura, reputacao), nao pelos argumentos da linha de comando.

Sem assinatura, isso pode voltar a acontecer, com voce ou com um usuario. Se
acontecer, os caminhos sem gastar e sem mexer no sistema:

- testar builds novos numa maquina sem o Smart App Control ligado (as VMs de
  teste, ou a `.16`) — compilar aqui e rodar la;
- continuar usando nesta maquina o build de 12/09 10:11, que o Windows ja aceita.

⚠️ **Mas esse build de 10:11 tem dois defeitos que sujam o banco**, porque e
anterior a duas correcoes:

- le o personagem do `sf2ce` no endereco errado (`0xFF83D9` / `0xFF86D9`,
  corrigido em `6db8f1e`), entao partida de `sf2ce` gravada com ele vai para o
  Django com **personagem errado**;
- conta desistencia 0 x 0 como partida empatada (corrigido em `8f6eb66`).

Para teste de rede e de interface ele serve. Para partida que vai valer no
ranking, nao.
