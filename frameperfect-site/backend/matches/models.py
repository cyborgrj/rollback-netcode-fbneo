"""Histórico de partidas.

O que chega aqui foi lido da memória do arcade pelo emulador, não digitado por
ninguém. O servidor de matchmaking (.NET) é quem entrega, e ele só entrega o que
recebeu dos dois jogadores da partida.
"""

from django.conf import settings
from django.db import models


class Jogo(models.Model):
    """Um jogo suportado. Existe como tabela porque o nome do personagem depende
    dele, e porque a lista cresce sem precisar de deploy."""

    short_name = models.CharField(max_length=32, unique=True)   # sf2ce, kof98...
    titulo = models.CharField(max_length=120)
    ano = models.PositiveSmallIntegerField(null=True, blank=True)
    fabricante = models.CharField(max_length=60, blank=True)

    class Meta:
        db_table = "jogo"
        ordering = ["titulo"]

    def __str__(self):
        return self.titulo


class Personagem(models.Model):
    """O id numérico que o emulador leu, e o nome que a gente foi descobrindo.

    O id vem primeiro e o nome depois, de propósito: o emulador grava o número
    desde a primeira partida, e conforme os nomes vão sendo identificados as
    partidas antigas passam a mostrá-los sem precisar reprocessar nada.
    """

    jogo = models.ForeignKey(Jogo, on_delete=models.CASCADE, related_name="personagens")
    id_no_jogo = models.PositiveSmallIntegerField()
    nome = models.CharField(max_length=60, blank=True)
    imagem = models.ImageField(upload_to="personagens/", blank=True)

    class Meta:
        db_table = "personagem"
        unique_together = [("jogo", "id_no_jogo")]
        ordering = ["jogo", "id_no_jogo"]

    def __str__(self):
        return self.nome or f"{self.jogo.short_name} #{self.id_no_jogo}"


class Partida(models.Model):
    """Uma sessão entre dois jogadores, do jeito que ela acabou."""

    class Fim(models.TextChoices):
        LIMITE = "limit", "atingiu o FT combinado"
        QUEDA = "disconnect", "adversário caiu"
        FECHOU = "closed", "encerrada pelos jogadores"

    # O id vem do servidor de matchmaking, e é ele que amarra os dois relatos da
    # mesma partida. Único, porque a segunda chegada é conferência, não um novo
    # registro.
    match_id = models.CharField(max_length=40, unique=True, db_index=True)

    jogo = models.ForeignKey(Jogo, on_delete=models.PROTECT, related_name="partidas")
    # SET_NULL e nao PROTECT: quando alguem exclui a conta, a partida continua
    # existindo porque ela tambem e do adversario - mas sem ligacao com a pessoa.
    # Mesmo raciocinio de um placar de fliperama: o resultado aconteceu, quem
    # jogou some.
    p1 = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.SET_NULL,
                           null=True, related_name="partidas_p1")
    p2 = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.SET_NULL,
                           null=True, related_name="partidas_p2")

    p1_vitorias = models.PositiveSmallIntegerField(default=0)
    p2_vitorias = models.PositiveSmallIntegerField(default=0)
    total_partidas = models.PositiveSmallIntegerField(default=0)   # inclui empates

    first_to = models.PositiveSmallIntegerField(default=0)         # 0 = livre
    motivo_fim = models.CharField(max_length=16, choices=Fim.choices, default=Fim.FECHOU)

    # Caminho do replay no disco do servidor, quando existir. O replay é o save
    # state do frame 0 mais os inputs confirmados - a partida inteira, não um
    # vídeo dela.
    replay = models.FileField(upload_to="replays/", blank=True)

    comecou_em = models.DateTimeField()
    terminou_em = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "partida"
        ordering = ["-terminou_em"]
        indexes = [
            models.Index(fields=["p1", "-terminou_em"]),
            models.Index(fields=["p2", "-terminou_em"]),
        ]

    def __str__(self):
        return f"{self.p1} {self.p1_vitorias} x {self.p2_vitorias} {self.p2}"

    @property
    def vencedor(self):
        if self.p1_vitorias > self.p2_vitorias:
            return self.p1
        if self.p2_vitorias > self.p1_vitorias:
            return self.p2
        return None   # empate é um resultado, não um erro


class EscolhaPersonagem(models.Model):
    """Quem cada jogador usou. Lista, e não campo, porque no KOF são três por
    lado e a ordem em que entram importa."""

    partida = models.ForeignKey(Partida, on_delete=models.CASCADE, related_name="escolhas")
    lado = models.PositiveSmallIntegerField()        # 1 ou 2
    ordem = models.PositiveSmallIntegerField()       # 0 = primeiro a entrar
    id_no_jogo = models.PositiveSmallIntegerField()

    class Meta:
        db_table = "escolha_personagem"
        unique_together = [("partida", "lado", "ordem")]
        ordering = ["lado", "ordem"]

    @property
    def personagem(self):
        return Personagem.objects.filter(
            jogo=self.partida.jogo, id_no_jogo=self.id_no_jogo
        ).first()
