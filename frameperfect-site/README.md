# Frame Perfect — site

Django (API) + React (site) + PostgreSQL, para rodar na mesma Lightsail que já
hospeda o lobby .NET.

> ⚠️ **Este código ainda não foi executado.** A máquina onde ele foi escrito não
> tem Node, Python, Postgres nem Docker, então nada aqui foi compilado, migrado
> ou aberto no navegador — ao contrário do emulador e do servidor de lobby, que
> são compilados e testados a cada mudança. Trate como um esqueleto revisável, não
> como algo que já rodou. O primeiro `migrate` e o primeiro `npm run dev` vão
> achar coisas.

Para ver a cara do site sem instalar nada, há uma versão da página inicial
publicada como artefato — mesma paleta, mesma tipografia, mesmo gabinete 3D.

## O que tem aqui

```
backend/     Django + DRF + JWT
  accounts/  usuário, confirmação de e-mail, login, a rota que o .NET chama
  matches/   histórico de partidas, personagens, e a entrada do resultado
frontend/    React + Vite + Tailwind
  src/components/Cabinet.jsx    o gabinete 3D que gira com o scroll
  src/components/Timeline.jsx   o rollback acontecendo, frame a frame
deploy/      Nginx, systemd
```

## Rodar em desenvolvimento

Precisa de **Python 3.11+**, **Node 18+** e **PostgreSQL 14+**.

```bash
# banco
createdb frameperfect

# backend
cd backend
python -m venv ../venv && ../venv/bin/activate      # Windows: ..\venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env                                 # e preencha
python manage.py makemigrations accounts matches
python manage.py migrate
python manage.py createsuperuser
python manage.py runserver                           # 127.0.0.1:8000
```

```bash
# frontend, em outro terminal
cd frontend
npm install
npm run dev                                          # 127.0.0.1:5173
```

O Vite repassa `/api` para o Django, então o front usa caminho relativo e não
precisa saber o endereço do backend — igualzinho ao que o Nginx faz em produção.

Em desenvolvimento o e-mail de confirmação **é impresso no terminal** em vez de
enviado, o que evita depender de SMTP só para testar o cadastro.

## Ver os dados de produção sem expor o banco

O Postgres escuta só em `localhost`, e é assim que tem que ficar. Para
desenvolver contra os dados reais, abra um túnel:

```bash
ssh -L 5432:localhost:5432 ubuntu@SEU_IP -N
```

Nenhuma porta nova no firewall. Banco com dado pessoal não vai para a internet.

## As três decisões que valem explicação

**O emulador nunca fala com o banco.** Ele fala com o lobby .NET, e o .NET
pergunta ao Django em `POST /api/internal/verify-user`. Essa rota tem três
barreiras, não uma: a API key, o Django conferindo que o `REMOTE_ADDR` é
localhost, e o Nginx negando `/api/internal/` para quem vem de fora. A chave
sozinha vaza em log; o localhost sozinho cai no dia em que alguém publicar a
porta do gunicorn sem pensar.

**O `id` do personagem vem antes do nome.** O emulador grava o número que leu da
memória desde a primeira partida. Conforme os nomes vão sendo identificados,
as partidas antigas passam a mostrá-los sem reprocessar nada.

**Excluir a conta apaga a pessoa, não a partida.** A partida também é do
adversário, então ela fica com o campo do jogador nulo (`SET_NULL`). É o mesmo
raciocínio de um placar de fliperama: o resultado aconteceu, quem jogou some.

## LGPD

O que o código faz: só usuário, e-mail e senha; Argon2 no hash; HTTPS e cookies
`Secure` em produção; nenhum dado pessoal em URL; banco sem porta exposta; e os
dois endpoints que realmente contam — `GET /api/me` para levar os dados embora e
`POST /api/me/excluir` para sair de verdade.

Conferir se isso atende à LGPD no caso concreto é trabalho de advogado. O que
está aqui é a implementação técnica, não um parecer.

## Produção

```bash
# frontend: o React não roda no servidor, vira arquivo estático
cd frontend && npm run build
scp -r dist/* ubuntu@SEU_IP:/usr/share/nginx/frameperfect/

# backend
cd backend && python manage.py collectstatic --noinput
sudo cp ../deploy/frameperfect-web.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now frameperfect-web

# nginx e certificado
sudo cp ../deploy/nginx-frameperfect.conf /etc/nginx/sites-available/frameperfect
sudo ln -s /etc/nginx/sites-available/frameperfect /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d frameperfect.net -d www.frameperfect.net
```

⚠️ A instância é pequena — foi ela que travou no `dotnet publish` sem swap.
Postgres, gunicorn, Nginx e o lobby .NET juntos vão ficar apertados. Viável,
mas provavelmente vai pedir um plano maior.

## O que ainda não existe

- O `POST /api/internal/resultado` está escrito, mas o lobby .NET ainda não o
  chama — hoje ele só registra o resultado no log. É o próximo elo.
- Página de recuperação de senha (o Django tem o fluxo pronto, falta a tela).
- Download do replay: o modelo tem o campo, falta o relay gravar o arquivo.
- Nenhum teste. Assim que der para rodar, o primeiro é o do cadastro e
  confirmação, que é onde um erro custa uma conta presa.
