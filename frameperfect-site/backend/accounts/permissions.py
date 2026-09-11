"""Quem pode falar com a rota interna."""

import hmac

from django.conf import settings
from rest_framework.permissions import BasePermission


class ServicoInterno(BasePermission):
    """So o servidor de matchmaking (.NET), rodando na mesma maquina.

    Duas barreiras, de proposito. A chave sozinha vaza em log, em print de tela,
    em variavel de ambiente copiada por engano. O localhost sozinho cai no dia
    em que alguem publicar a porta do gunicorn sem pensar. As duas juntas
    exigem os dois erros ao mesmo tempo.
    """

    message = "Rota interna."

    def has_permission(self, request, view):
        chave = request.headers.get("X-Frame-Perfect-Key", "")
        esperada = settings.INTERNAL_API_KEY

        # Sem chave configurada a rota fica fechada, e nao aberta. O modo
        # inseguro nunca deve ser o que acontece quando alguem esquece de
        # configurar algo.
        if not esperada:
            return False

        # compare_digest: comparar com == vaza o tamanho do prefixo correto
        # pelo tempo de resposta.
        if not hmac.compare_digest(chave, esperada):
            return False

        return request.META.get("REMOTE_ADDR") in ("127.0.0.1", "::1")
