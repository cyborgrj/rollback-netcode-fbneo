from rest_framework import serializers

from .models import EscolhaPersonagem, Jogo, Partida


class JogoSerializer(serializers.ModelSerializer):
    class Meta:
        model = Jogo
        fields = ["short_name", "titulo", "ano", "fabricante"]


class EscolhaSerializer(serializers.ModelSerializer):
    nome = serializers.SerializerMethodField()

    class Meta:
        model = EscolhaPersonagem
        fields = ["lado", "ordem", "id_no_jogo", "nome"]

    def get_nome(self, obj):
        p = obj.personagem
        # O numero sempre existe; o nome so depois que alguem identificou. Mostrar
        # o numero e melhor que mostrar vazio - e quando o nome chegar, as
        # partidas antigas passam a mostra-lo sem reprocessar nada.
        return (p.nome if p and p.nome else None) or f"#{obj.id_no_jogo}"


class PartidaSerializer(serializers.ModelSerializer):
    jogo = serializers.CharField(source="jogo.short_name", read_only=True)
    jogo_titulo = serializers.CharField(source="jogo.titulo", read_only=True)
    p1 = serializers.CharField(source="p1.username", read_only=True, default=None)
    p2 = serializers.CharField(source="p2.username", read_only=True, default=None)
    vencedor = serializers.SerializerMethodField()
    escolhas = EscolhaSerializer(many=True, read_only=True)
    tem_replay = serializers.SerializerMethodField()

    class Meta:
        model = Partida
        fields = [
            "match_id", "jogo", "jogo_titulo", "p1", "p2",
            "p1_vitorias", "p2_vitorias", "total_partidas",
            "first_to", "motivo_fim", "vencedor", "escolhas",
            "tem_replay", "comecou_em", "terminou_em",
        ]

    def get_vencedor(self, obj):
        v = obj.vencedor
        return v.username if v else None

    def get_tem_replay(self, obj):
        return bool(obj.replay)
