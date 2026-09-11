from django.contrib.auth import get_user_model
from django.contrib.auth.password_validation import validate_password
from rest_framework import serializers

User = get_user_model()


class CadastroSerializer(serializers.ModelSerializer):
    password = serializers.CharField(write_only=True, style={"input_type": "password"})

    class Meta:
        model = User
        fields = ["username", "email", "password"]

    def validate_username(self, valor):
        valor = valor.strip()
        if len(valor) < 3 or len(valor) > 24:
            raise serializers.ValidationError("O nome precisa ter de 3 a 24 caracteres.")
        # Sem diferenciar caixa: dois nomes que so diferem em maiusculas sao a
        # mesma pessoa para quem le a tela no meio de uma luta.
        if User.objects.filter(username__iexact=valor).exists():
            raise serializers.ValidationError("Esse nome ja esta em uso.")
        return valor

    def validate_email(self, valor):
        valor = valor.strip().lower()
        if User.objects.filter(email__iexact=valor).exists():
            raise serializers.ValidationError("Esse e-mail ja tem conta.")
        return valor

    def validate_password(self, valor):
        validate_password(valor)
        return valor

    def create(self, dados):
        # create_user, nunca create: e ele que passa a senha pelo hasher.
        return User.objects.create_user(
            username=dados["username"],
            email=dados["email"],
            password=dados["password"],
            is_active=True,
        )


class UsuarioSerializer(serializers.ModelSerializer):
    class Meta:
        model = User
        fields = ["id", "username", "email", "email_confirmado", "date_joined"]
        read_only_fields = fields
