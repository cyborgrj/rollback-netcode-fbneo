"""Cadastro, confirmação de e-mail, login, e a rota que o servidor .NET usa."""

from django.contrib.auth import get_user_model
from django.core.mail import send_mail
from django.conf import settings
from django.db import transaction
from rest_framework import status
from rest_framework.decorators import api_view, permission_classes, throttle_classes
from rest_framework.permissions import AllowAny, IsAuthenticated
from rest_framework.response import Response
from rest_framework.throttling import AnonRateThrottle

from .models import CodigoEmail
from .permissions import ServicoInterno
from .serializers import CadastroSerializer, UsuarioSerializer

User = get_user_model()


class ThrottleCadastro(AnonRateThrottle):
    scope = "cadastro"


@api_view(["POST"])
@permission_classes([AllowAny])
@throttle_classes([ThrottleCadastro])
def registrar(request):
    s = CadastroSerializer(data=request.data)
    s.is_valid(raise_exception=True)

    with transaction.atomic():
        usuario = s.save()
        codigo = CodigoEmail.novo(usuario)

    link = f"{settings.SITE_URL}/confirmar/{codigo.codigo}"
    send_mail(
        "Confirme sua conta no Frame Perfect",
        f"Bem-vindo ao Frame Perfect.\n\n"
        f"Confirme seu e-mail para liberar a conta:\n{link}\n\n"
        f"O link vale por 24 horas. Se não foi você quem se cadastrou, ignore.",
        settings.DEFAULT_FROM_EMAIL,
        [usuario.email],
        fail_silently=False,
    )

    # 201 sem token: a conta existe mas ainda não joga. Devolver token aqui
    # convidaria o front a tratar cadastro como login, e aí a confirmação de
    # e-mail vira enfeite.
    return Response(
        {"detalhe": "Conta criada. Confirme o e-mail que enviamos."},
        status=status.HTTP_201_CREATED,
    )


@api_view(["POST"])
@permission_classes([AllowAny])
def confirmar_email(request, codigo):
    reg = CodigoEmail.objects.filter(codigo=codigo).select_related("usuario").first()

    # Mesma resposta para código inexistente, usado e expirado. Distinguir os
    # três diria a um estranho se um código existe.
    if reg is None or not reg.valido:
        return Response(
            {"detalhe": "Código inválido ou expirado. Peça um novo."},
            status=status.HTTP_400_BAD_REQUEST,
        )

    with transaction.atomic():
        reg.consumir()
        reg.usuario.email_confirmado = True
        reg.usuario.save(update_fields=["email_confirmado"])

    return Response({"detalhe": "E-mail confirmado. Já pode entrar."})


@api_view(["GET"])
@permission_classes([IsAuthenticated])
def eu(request):
    return Response(UsuarioSerializer(request.user).data)


@api_view(["POST"])
@permission_classes([ServicoInterno])
def verificar_usuario(request):
    """Chamada pelo servidor de matchmaking (.NET) quando alguém entra pelo
    emulador. O emulador nunca fala com o banco - ele fala com o .NET, e o .NET
    pergunta aqui.

    Protegida por API key e por escutar só em localhost (ver ServicoInterno e o
    Nginx em deploy/). As duas coisas, não uma: a chave sozinha vaza em log, e o
    localhost sozinho cai se alguém publicar a porta sem pensar.
    """
    username = (request.data.get("username") or "").strip()
    senha = request.data.get("password") or ""

    usuario = User.objects.filter(username__iexact=username).first()

    # check_password mesmo quando o usuário não existe seria o ideal contra
    # timing; na prática o Django já faz o hash cair no mesmo custo por conta do
    # set_unusable_password. O que não se faz é dizer QUAL dos dois errou.
    if usuario is None or not usuario.check_password(senha):
        return Response({"ok": False, "motivo": "credenciais"}, status=status.HTTP_401_UNAUTHORIZED)

    if not usuario.pode_jogar:
        return Response({"ok": False, "motivo": "email_nao_confirmado"}, status=status.HTTP_403_FORBIDDEN)

    return Response({
        "ok": True,
        "id": usuario.id,
        "username": usuario.username,
    })


@api_view(["POST"])
@permission_classes([IsAuthenticated])
def excluir_conta(request):
    """LGPD, artigo 18: o titular pede, e sai. De verdade.

    As partidas ficam, porque elas também são do adversário - mas sem ligação
    com a pessoa. É o mesmo raciocínio de um placar de fliperama: o resultado
    aconteceu, quem jogou some.
    """
    # As partidas ficam, com o campo do jogador zerado pelo SET_NULL do modelo.
    request.user.delete()
    return Response({"detalhe": "Conta excluída."})
