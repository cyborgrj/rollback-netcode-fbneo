from django.urls import path
from rest_framework_simplejwt.views import TokenObtainPairView, TokenRefreshView

from . import views

urlpatterns = [
    path("auth/register", views.registrar, name="registrar"),
    path("auth/login", TokenObtainPairView.as_view(), name="login"),
    path("auth/refresh", TokenRefreshView.as_view(), name="refresh"),
    path("auth/confirmar/<str:codigo>", views.confirmar_email, name="confirmar"),
    path("me", views.eu, name="eu"),
    path("me/excluir", views.excluir_conta, name="excluir_conta"),

    # So o .NET chama esta, e so de localhost. Ver accounts/permissions.py.
    path("internal/verify-user", views.verificar_usuario, name="verificar_usuario"),
]
