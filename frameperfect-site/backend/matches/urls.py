from django.urls import path

from . import views

urlpatterns = [
    path("partidas", views.minhas_partidas, name="minhas_partidas"),
    path("rivais", views.meus_rivais, name="meus_rivais"),

    # So o .NET chama esta, e so de localhost.
    path("internal/resultado", views.registrar_resultado, name="registrar_resultado"),
]
