#!/bin/bash
set -euo pipefail

service_name="premierapi"
health_url="http://127.0.0.1:5000/api/checkout/pricing-rules"

echo "Reiniciando ${service_name}..."
systemctl restart "$service_name"

for _ in $(seq 1 90); do
    if systemctl is-active --quiet "$service_name" && curl -fsS --max-time 2 "$health_url" > /dev/null; then
        echo "${service_name} reiniciado e respondendo normalmente."
        echo "Acompanhando o journal; pressione Ctrl+C para sair."
        exec journalctl -u "$service_name" -f
    fi

    sleep 1
done

echo "Erro: ${service_name} nao ficou pronto dentro de 90 segundos." >&2
exit 1
