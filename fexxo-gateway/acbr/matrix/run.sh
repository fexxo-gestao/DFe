#!/usr/bin/env bash
set -euo pipefail
GATEWAY="${GATEWAY:-http://127.0.0.1:8090}"
TOKEN="${TOKEN:?defina TOKEN}"
DIR="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$TMP/k.pem" -out "$TMP/c.pem" -days 2 -subj "/CN=MATRIZ FEXXO:66640025000168" 2>/dev/null
openssl pkcs12 -export -out "$TMP/c.pfx" -inkey "$TMP/k.pem" -in "$TMP/c.pem" -passout pass:matriz
PFX="$(base64 -w0 "$TMP/c.pfx")"
falhas=0
total=0
lacunas_resolvidas=0
for fixture in "$DIR"/fixtures/*.json; do
  total=$((total + 1))
  nome="$(basename "$fixture" .json)"
  corpo="$(jq --arg pfx "$PFX" '.certificado = {pfxBase64: $pfx, senha: "matriz"}' "$fixture")"
  resposta="$(curl -s -m 120 -H "Content-Type: application/json" -H "X-Fiscal-Gateway-Token: $TOKEN" -d "$corpo" "$GATEWAY/v1/nfse/municipal/validar" || echo '{}')"
  conhecida=0
  grep -qx "$nome" "$DIR/lacunas-conhecidas.txt" && conhecida=1
  if [ "$(echo "$resposta" | jq -r '.valida // false')" = "true" ]; then
    echo "OK    $nome $(echo "$resposta" | jq -r '(.ajustes // []) | join(",")')"
    [ "$conhecida" -eq 1 ] && lacunas_resolvidas=$((lacunas_resolvidas + 1)) && echo "      ^ estava em lacunas-conhecidas.txt: pode sair de la"
  elif [ "$conhecida" -eq 1 ]; then
    echo "LACUNA $nome $(echo "$resposta" | jq -r '(.erros[0].mensagem // .mensagem // "sem resposta")' | cut -c1-160)"
  else
    falhas=$((falhas + 1))
    echo "FALHA $nome $(echo "$resposta" | jq -r '(.erros[0].mensagem // .mensagem // "sem resposta")' | cut -c1-200)"
  fi
done
echo "regressoes: $falhas | lacunas resolvidas: $lacunas_resolvidas | fixtures: $total"
[ "$falhas" -eq 0 ]
