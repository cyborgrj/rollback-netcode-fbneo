"""Histórico do jogador, e a porta por onde o resultado entra."""

from django.contrib.auth import get_user_model
from django.db import transaction
from django.db.models import Count, Q
from django.shortcuts import get_object_or_404
from rest_framework import status
from rest_framework.decorators import api_view, permission_classes
from rest_framework.permissions import IsAuthenticated
from rest_framework.response import Response

from accounts.permissions import ServicoInterno

from .models import EscolhaPersonagem, Jogo, Partida
from .serializers import PartidaSerializer

User = get_user_model()


@api_view(["GET"])
@permission_classes([IsAuthenticated])
def minhas_partidas(request):
    """As partidas de quem está logado, das mais novas para as mais velhas."""
    qs = (
        Partida.objects
        .filter(Q(p1=request.user) | Q(p2=request.user))
        .select_related("jogo", "p1", "p2")
        .prefetch_related("escolhas")
    )

    limite = min(int(request.query_params.get("limite", 50)), 200)
    return Response(PartidaSerializer(qs[:limite], many=True).data)


@api_view(["GET"])
@permission_classes([IsAuthenticated])
def meus_rivais(request):
    """Contra quem eu mais perdi, e quem eu mais venci.

    As duas contas que todo jogador faz de cabeça. Feitas no banco porque fazer
    no front exigiria baixar o histórico inteiro só para somar.
    """
    eu = request.user
    vitorias = {}    # adversário -> quantas vezes eu ganhei
    derrotas = {}

    qs = (
        Partida.objects
        .filter(Q(p1=eu) | Q(p2=eu))
        .select_related("p1", "p2")
        .only("p1", "p2", "p1_vitorias", "p2_vitorias")
    )

    for p in qs.iterator():
        sou_p1 = p.p1_id == eu.id
        outro = p.p2 if sou_p1 else p.p1
        if outro is None:
            continue   # conta excluída: a partida fica, a pessoa não
        meus = p.p1_vitorias if sou_p1 else p.p2_vitorias
        dele = p.p2_vitorias if sou_p1 else p.p1_vitorias
        if meus > dele:
            vitorias[outro.username] = vitorias.get(outro.username, 0) + 1
        elif dele > meus:
            derrotas[outro.username] = derrotas.get(outro.username, 0) + 1

    def topo(d):
        return [{"jogador": k, "partidas": v}
                for k, v in sorted(d.items(), key=lambda kv: -kv[1])[:10]]

    return Response({
        "mais_venci": topo(vitorias),
        "mais_perdi": topo(derrotas),
    })


@api_view(["POST"])
@permission_classes([ServicoInterno])
def registrar_resultado(request):
    """O servidor de matchmaking entregando o que os emuladores leram.

    Idempotente pelo match_id: os dois jogadores reportam a mesma partida, e o
    .NET já concilia os dois relatos antes de chegar aqui. Se mesmo assim vier
    duas vezes, a segunda não duplica nada.
    """
    d = request.data
    match_id = (d.get("match_id") or "").strip()
    if not match_id:
        return Response({"detalhe": "match_id obrigatório."}, status=status.HTTP_400_BAD_REQUEST)

    if Partida.objects.filter(match_id=match_id).exists():
        return Response({"detalhe": "já registrada."}, status=status.HTTP_200_OK)

    jogo, _ = Jogo.objects.get_or_create(
        short_name=(d.get("game") or "").strip(),
        defaults={"titulo": (d.get("game") or "desconhecido")},
    )

    p1 = User.objects.filter(username__iexact=(d.get("p1") or "")).first()
    p2 = User.objects.filter(username__iexact=(d.get("p2") or "")).first()

    with transaction.atomic():
        partida = Partida.objects.create(
            match_id=match_id,
            jogo=jogo,
            p1=p1,
            p2=p2,
            p1_vitorias=int(d.get("p1_games") or 0),
            p2_vitorias=int(d.get("p2_games") or 0),
            total_partidas=int(d.get("games") or 0),
            first_to=int(d.get("first_to") or 0),
            motivo_fim=(d.get("reason") or Partida.Fim.FECHOU),
            comecou_em=d.get("started_at"),
        )

        escolhas = []
        for lado, chave in ((1, "p1_chars"), (2, "p2_chars")):
            for ordem, ident in enumerate(d.get(chave) or []):
                escolhas.append(EscolhaPersonagem(
                    partida=partida, lado=lado, ordem=ordem, id_no_jogo=int(ident)
                ))
        EscolhaPersonagem.objects.bulk_create(escolhas)

    return Response({"detalhe": "registrada.", "id": partida.id},
                    status=status.HTTP_201_CREATED)
