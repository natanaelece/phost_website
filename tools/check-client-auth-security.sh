#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"

production_roots=(Controllers Services Program.cs)
failed=0

check_pattern() {
    local description="$1"
    local expression="$2"
    if rg -n --pcre2 "$expression" "${production_roots[@]}"; then
        echo "Erro: ${description}." >&2
        failed=1
    fi
}

check_pattern \
    "escrita de token bruto em user_sessions" \
    'INSERT\s+INTO\s+user_sessions\s*\([^)]*\btoken\b(?!_hash)'
check_pattern \
    "consulta de sessão pelo token bruto" \
    '(?:WHERE|AND)\s+(?:[a-z_]+\.)?token\s*=\s*@Token\b'
check_pattern \
    "escrita de token bruto de recuperação" \
    '\bpassword_reset_token\s*=\s*@Token\b'
check_pattern \
    "consulta de recuperação pelo token bruto" \
    'WHERE\s+password_reset_token\s*=\s*@Token\b'
check_pattern \
    "escrita de token bruto de confirmação" \
    '\bemail_confirmation_token\s*=\s*@Token\b'
check_pattern \
    "consulta de confirmação pelo token bruto" \
    'WHERE\s+email_confirmation_token\s*=\s*@Token\b'
check_pattern \
    "token bruto na tabela dedicada de confirmação" \
    'INSERT\s+INTO\s+email_confirmation_tokens\s*\([^)]*\btoken\b(?!_hash)'
check_pattern \
    "token ou hash completo em chamada de log" \
    'Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\([^;]*(?:\{(?:Token|TokenHash|Hash)\}|,\s*(?:raw)?token(?:Hash)?\b)'

if rg -n 'email_confirmation_token_hash' \
    Controllers \
    Services \
    -g '!DatabaseInitializer.cs'; then
    echo "Erro: coluna intermediária de confirmação usada fora da migration." >&2
    failed=1
fi

if rg -n '\bemail_confirmation_token\b' \
    Controllers \
    Services \
    -g '!DatabaseInitializer.cs' \
    -g '!EmailConfirmationTokenService.cs'; then
    echo "Erro: coluna legada de confirmação usada fora da migration/limpeza central." >&2
    failed=1
fi

mapfile -t confirmation_sql_consumers < <(
    rg -l '\bemail_confirmation_tokens\b' Controllers Services |
        sort
)
expected_confirmation_sql_consumers=(
    "Services/DatabaseInitializer.cs"
    "Services/EmailConfirmationTokenService.cs"
)
if [[ "${confirmation_sql_consumers[*]}" != \
      "${expected_confirmation_sql_consumers[*]}" ]]; then
    echo "Erro: SQL de confirmação fora do serviço central/migration." >&2
    printf '%s\n' "${confirmation_sql_consumers[@]}" >&2
    failed=1
fi

if rg -n 'Npgsql(Connection|Transaction)|BeginTransactionAsync|FOR UPDATE' \
    Services/EmailConfirmationReminderWorker.cs; then
    echo "Erro: worker de confirmação mantém acesso/lock PostgreSQL durante envio." >&2
    failed=1
fi

register_flow="$(
    awk '
        /public async Task<IActionResult> Register/ { capture = 1 }
        /public async Task<IActionResult> ConfirmEmail/ { capture = 0 }
        capture { print }
    ' Controllers/AuthController.cs
)"
register_commit_line="$(
    rg -n 'transaction\.CommitAsync' <<<"$register_flow" |
        head -n 1 |
        cut -d: -f1
)"
register_close_line="$(
    rg -n 'db\.CloseAsync' <<<"$register_flow" |
        head -n 1 |
        cut -d: -f1
)"
register_send_line="$(
    rg -n '_emailConfirmation\.SendAsync' <<<"$register_flow" |
        head -n 1 |
        cut -d: -f1
)"
if [[ -z "$register_commit_line" ||
      -z "$register_close_line" ||
      -z "$register_send_line" ||
      "$register_commit_line" -ge "$register_send_line" ||
      "$register_close_line" -ge "$register_send_line" ]]; then
    echo "Erro: cadastro não confirma/fecha PostgreSQL antes do SMTP." >&2
    failed=1
fi

admin_confirmation_flow="$(
    awk '
        /public async Task<IActionResult> ConfirmEmailManual/ { capture = 1 }
        /private DateTime GetEmailConfirmationLocalNow/ { capture = 0 }
        capture { print }
    ' Controllers/AdminController.cs
)"
if rg -n 'BeginTransactionAsync|NpgsqlTransaction|FOR UPDATE' \
    <<<"$admin_confirmation_flow"; then
    echo "Erro: confirmação/reenvio administrativo mantém transação durante SMTP." >&2
    failed=1
fi

token_generation_files=(
    Controllers/AuthController.cs
    Controllers/ProfileController.cs
    Services/ClientSessionService.cs
    Services/SecurityTokenService.cs
    Services/EmailConfirmationTokenService.cs
    Services/EmailConfirmationReminderWorker.cs
)
if rg -n 'Guid\.NewGuid\s*\(' "${token_generation_files[@]}"; then
    echo "Erro: Guid.NewGuid reapareceu em fluxo de credencial ou autenticação." >&2
    failed=1
fi

if (( failed != 0 )); then
    exit 1
fi

echo "CLIENT_AUTH_SECURITY=PASS"
