#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# compilar-emulador.sh - compila o fbneo64d.exe guardando o anterior, numerado.
#
# Rode na shell MSYS2 MINGW64, de qualquer pasta:
#
#   bash "/d/Rollback Netcode/rollback_netcode/build/compilar-emulador.sh"
#
# O que ele faz, nesta ordem:
#
#   1. guarda o executavel atual em fbneo/backups/, com o numero do build dele;
#   2. aplica os patches e compila a lib (setup-fbneo.sh);
#   3. compila o emulador;
#   4. anota o build novo em fbneo/backups/builds.txt.
#
# Os backups ficam na pasta onde o executavel e gerado - nunca na pasta do
# produto (D:\RBF). La so vai o build que voce copiar, e ela nao acumula versao.
#
# Por que numerar: em 13/09 foi preciso descobrir de que commit era um
# executavel velho so pela data do arquivo, e um executavel identico ao que o
# Windows aceitava nao existia mais em lugar nenhum. O builds.txt diz, para
# cada numero, quando foi compilado, o hash e o commit de onde saiu.
#
#   b002  2026-09-13  01:17  A71D1956  2b1bd05  (limpo)
#
# "(+alteracoes-locais)" no lugar de "(limpo)" quer dizer que havia mudanca nao
# commitada no repositorio na hora - o commit sozinho nao reproduz aquele build.
# ---------------------------------------------------------------------------
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"   # raiz do repo
fbneo="${FBNEO:-$here/fbneo}"
exe="$fbneo/fbneo64d.exe"
bak="$fbneo/backups"
log="$bak/builds.txt"

mkdir -p "$bak"
touch "$log"

hash8() { sha256sum "$1" | cut -c1-8 | tr 'a-f' 'A-F'; }

# O numero de um executavel e a linha do builds.txt com o mesmo hash.
numero_de() {
	local h
	h="$(hash8 "$1")"
	awk -v h="$h" '$4 == h { n = $1 } END { if (n) print n }' "$log"
}

proximo_numero() {
	local n
	n="$(awk '{ v = $1; sub(/^b/, "", v); if (v + 0 > m) m = v + 0 } END { print m + 0 }' "$log")"
	printf 'b%03d' $((n + 1))
}

anotar() {   # numero  arquivo  commit  estado
	printf '%s  %s  %s  %s  %s  %s\n' "$1" "$(date -r "$2" +%F)" "$(date -r "$2" +%H:%M)" \
		"$(hash8 "$2")" "$3" "$4" >> "$log"
}

# ---- 1. backup do atual ----------------------------------------------------
if [ -f "$exe" ]; then
	num="$(numero_de "$exe")"
	if [ -z "$num" ]; then
		# Compilado antes deste script existir: ganha um numero agora, para nao
		# virar mais um executavel sem historia.
		num="$(proximo_numero)"
		anotar "$num" "$exe" "-------" "(anterior-ao-script)"
	fi
	dest="$bak/fbneo64d-$num-$(date -r "$exe" +%Y%m%d-%H%M)-$(hash8 "$exe").exe"
	if [ -f "$dest" ]; then
		echo ":: backup do $num ja existe: $(basename "$dest")"
	else
		cp -p "$exe" "$dest"
		echo ":: backup: $(basename "$dest")"
	fi
fi

# ---- 2 e 3. compilar -------------------------------------------------------
if [ -n "${RBF_TESTE_BUILD:-}" ]; then
	# Teste do proprio script: finge a compilacao copiando um arquivo pronto.
	cp "$RBF_TESTE_BUILD" "$exe"
else
	echo ":: patches e lib"
	"$here/rollback_netcode/build/setup-fbneo.sh" BUILD_X64_EXE=1
	echo ":: emulador"
	( cd "$fbneo" && make mingw BUILD_X64_EXE=1 DEBUG=1 )
fi

# ---- 4. anotar o novo ------------------------------------------------------
if [ ! -f "$exe" ]; then
	echo "!! a compilacao terminou sem gerar $exe"
	exit 1
fi

if [ -n "$(numero_de "$exe")" ]; then
	echo
	echo ":: o executavel nao mudou (mesmo hash do $(numero_de "$exe")) - nada novo a anotar."
	exit 0
fi

num="$(proximo_numero)"
commit="$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo "-------")"
estado="(limpo)"
if [ -n "$(git -C "$here" status --porcelain 2>/dev/null)" ]; then
	estado="(+alteracoes-locais)"
fi
anotar "$num" "$exe" "$commit" "$estado"

echo
echo ":: build $num pronto"
echo "   $exe"
echo "   hash $(hash8 "$exe")   commit $commit $estado"
echo
echo "   para usar:  cp \"$exe\" /d/RBF/"
