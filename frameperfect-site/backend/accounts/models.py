"""Contas do Frame Perfect.

A regra que manda aqui é a do produto: pedir o mínimo. Usuário, e-mail e senha,
e nada além disso. Cada campo a mais é um campo que precisa ser justificado, que
precisa ser protegido, e que precisa sair quando alguém pedir a exclusão.
"""

import secrets
from datetime import timedelta

from django.contrib.auth.models import AbstractUser
from django.db import models
from django.utils import timezone


class User(AbstractUser):
    """Estende o usuário do Django em vez de criar um paralelo.

    Trocar o modelo de usuário depois que o projeto tem migrations é
    notoriamente doloroso, então isso é feito no primeiro commit mesmo que hoje
    ele acrescente pouco.
    """

    # O nome que aparece no lobby, no overlay do emulador e no histórico. É o
    # mesmo `username` do Django, mas com as regras do jogo: sem espaço nas
    # pontas, e único sem diferenciar maiúsculas (dois "CyborgRJ" e "cyborgrj"
    # seriam a mesma pessoa para qualquer jogador que lesse a tela).
    email = models.EmailField("e-mail", unique=True)

    email_confirmado = models.BooleanField(default=False)

    # Uma conta só entra em partida depois de confirmar o e-mail. Isso não é
    # burocracia: sem isso, uma conta abandonada por erro de digitação no e-mail
    # fica presa para sempre, sem como recuperar a senha.
    REQUIRED_FIELDS = ["email"]

    class Meta:
        db_table = "usuario"
        constraints = [
            models.UniqueConstraint(
                models.functions.Lower("username"),
                name="usuario_username_unico_sem_caixa",
            )
        ]

    def __str__(self):
        return self.username

    @property
    def pode_jogar(self):
        return self.is_active and self.email_confirmado


class CodigoEmail(models.Model):
    """Código de confirmação de e-mail, de uso único e com validade.

    Guardado como registro próprio em vez de campo no usuário porque um código
    tem ciclo de vida: nasce, é usado ou expira, e some. Um campo no usuário não
    tem onde guardar "quando" nem "já foi usado".
    """

    VALIDADE = timedelta(hours=24)

    usuario = models.ForeignKey(User, on_delete=models.CASCADE, related_name="codigos")
    codigo = models.CharField(max_length=64, unique=True, db_index=True)
    criado_em = models.DateTimeField(auto_now_add=True)
    usado_em = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "codigo_email"

    @classmethod
    def novo(cls, usuario):
        # token_urlsafe, não uuid4: um código que vai por e-mail e volta numa
        # URL precisa ser imprevisível, e uuid4 não promete isso.
        return cls.objects.create(usuario=usuario, codigo=secrets.token_urlsafe(32))

    @property
    def expirado(self):
        return timezone.now() - self.criado_em > self.VALIDADE

    @property
    def valido(self):
        return self.usado_em is None and not self.expirado

    def consumir(self):
        self.usado_em = timezone.now()
        self.save(update_fields=["usado_em"])
