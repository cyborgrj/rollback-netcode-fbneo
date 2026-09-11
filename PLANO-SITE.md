# Plano — banco de dados e site (partes 4 e 5)

Combinado em 2026-09-10. Este arquivo existe para o plano não se perder no meio
de uma conversa, como já aconteceu com as portas do servidor.

## Arquitetura

O **Django é o dono do banco**: migrations, models, admin. O servidor gRPC não
fala SQL — quando uma partida acaba, ele faz um `POST` autenticado por token
para o Django.

```
emulador --(resultado)--> launcher --(gRPC)--> RbfServer --(POST)--> Django --> Postgres
```

Uma dona do schema só, no framework que o usuário já domina, e o site poderia
mudar de máquina depois sem mexer no resto.

### Desenvolver local, ver dados de produção

Túnel SSH. O Postgres escuta **só em `localhost`** no VPS; nenhuma porta nova no
firewall, que também é o certo para LGPD.

```bash
ssh -L 5432:localhost:5432 ubuntu@18.228.40.244 -N
```

O `settings.py` local aponta para `127.0.0.1:5432`.

### Capacidade

A instância Lightsail é pequena — foi ela que travou no `dotnet publish` sem
swap. Postgres + gunicorn + nginx + RbfServer juntos vão ficar apertados.
Viável, mas provavelmente vai pedir um plano maior em algum momento.

## Contas

Hoje o lobby faz login **só com nome, sem senha**. Isso muda: o Django passa a
ser dono dos usuários e o launcher autentica contra ele — senão não há como
amarrar uma partida a uma pessoa, e qualquer um digita o nome de qualquer um.
É trabalho real no launcher e no RbfServer, e é pré-requisito do histórico.

Cadastro pede o mínimo: **usuário, e-mail, senha**. E-mail confirmado por
código; clicar na confirmação ativa a conta.

## LGPD

O que o código faz:

- só usuário, e-mail e senha — nada além disso;
- senha com o hasher do Django (nunca reversível, nunca em log);
- HTTPS obrigatório, cookie de sessão `Secure` + `HttpOnly`;
- nenhum dado pessoal em URL ou query string;
- banco sem porta exposta (ver o túnel acima);
- **"baixar meus dados"** e **"excluir minha conta"** — o segundo apagando de
  verdade, replays inclusive. É esta parte que realmente conta.

A política de privacidade precisa dizer que o histórico mostra o nome de usuário
de quem você enfrentou. É normal em jogo online, mas tem que estar escrito.

> Conferir se isso atende à LGPD no caso concreto é trabalho de advogado. O que
> está aqui é a implementação técnica, não um parecer.

## Replays — já existem, só não são salvos

O que o relay de espectador transmite **é** um replay: o save state do frame 0
mais todos os inputs confirmados, em ordem e sem buraco. É a definição de replay
determinístico, e o emulador já sabe reproduzir isso (`-rbfwatch`).

Faltam duas coisas pequenas:

1. `RelayServer` gravar o stream num arquivo em vez de só repassar;
2. `-rbfreplay <arquivo>` no emulador — o `-rbfwatch` lendo de arquivo em vez de
   socket.

Custo: ~460 KB por partida (o estado do KOF98 sozinho são 415 KB), ou uns dois
mil replays por GB.

## O site

- histórico de partidas do usuário logado: contra quem, com qual personagem,
  resultado, data;
- personagem **por nome** (imagens depois, quando o usuário conseguir a arte);
- baixar o replay da partida (abrir direto no emulador fica para depois);
- **contra quem mais perdi** (quem tem mais vitórias sobre mim) e **quem eu mais
  venci**;
- botão de download do zip do emulador.

## Ordem

1. ✅ leitor de placar dentro do emulador (`fbneo/match_score.cpp`)
2. ✅ o resultado chegar ao servidor pelo gRPC (emulador escreve `rbf-result-<id>.txt`,
   o launcher lê ao fechar e manda `MatchResult`; o servidor valida que quem
   reportou jogou a partida)
3. ⬜ Django + Postgres + contas + o POST do resultado
4. ⬜ relay salvando replay + `-rbfreplay`
5. ⬜ o site em si

## Pendência conhecida

O personagem do **P2 no `vsav`** ainda não é legível: naquele jogo o endereço
simétrico é um campo de animação. Resolve-se sozinho na primeira partida entre
dois humanos, quando as duas structs são de jogadores de verdade. Até lá o campo
vai gravado como desconhecido — o banco guarda o **id numérico** desde o começo,
então quando o nome aparecer as partidas antigas passam a mostrá-lo também.
