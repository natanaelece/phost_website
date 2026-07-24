#!/bin/bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"

asaas_production_files=(
    Program.cs
    Controllers/AdminController.cs
    Controllers/CheckoutController.cs
    Controllers/WebhookController.cs
    Services/AsaasApiClient.cs
    Services/AsaasErrorSanitizer.cs
    Services/AsaasHttpClientProvider.cs
)

if rg -n \
    -e 'ServerCertificateCustomValidationCallback' \
    -e 'DangerousAcceptAnyServerCertificateValidator' \
    -e 'RemoteCertificateValidationCallback' \
    -e 'ServicePointManager.ServerCertificateValidationCallback' \
    -e 'sslPolicyErrors' \
    "${asaas_production_files[@]}"; then
    echo "Erro: bypass de validacao TLS encontrado no codigo Asaas de producao." >&2
    exit 1
fi

if rg -n -F '"access_token"' Controllers; then
    echo "Erro: controller configurando diretamente credencial HTTP do Asaas." >&2
    exit 1
fi

if rg -n -F 'new HttpClient' Controllers/CheckoutController.cs; then
    echo "Erro: CheckoutController criando HttpClient diretamente." >&2
    exit 1
fi

echo "ASAAS_HTTP_SECURITY=PASS"
