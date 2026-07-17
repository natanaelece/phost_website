#!/bin/bash
set -u

state_dir="/run/premierapi-startup-alert"
lock_file="${state_dir}/alert.lock"
last_alert_file="${state_dir}/last-alert"
minimum_interval_seconds=600
bot_token="${Telegram__BotToken:-}"
chat_id="${Telegram__ChatId:-}"
test_mode=0
[[ "${1:-}" == "--test" ]] && test_mode=1

umask 077
install -d -m 700 "$state_dir"
exec 9>"$lock_file"
flock -n 9 || exit 0

if [[ ! "$bot_token" =~ ^[0-9]+:[A-Za-z0-9_-]+$ ]] || [[ ! "$chat_id" =~ ^-?[0-9]+$ ]]; then
    echo "PremierAPI startup alert is not configured." >&2
    exit 2
fi

now="$(date +%s)"
if (( test_mode == 0 )) && [[ -f "$last_alert_file" ]]; then
    last_alert="$(stat -c %Y "$last_alert_file" 2>/dev/null || echo 0)"
    if (( now - last_alert < minimum_interval_seconds )); then
        exit 0
    fi
fi

if (( test_mode == 1 )); then
    message="PremierHost API: teste do canal independente de alertas concluído com sucesso."
else
    message="PremierHost API: o serviço premierapi falhou durante a inicialização. Consulte o journal no servidor website."
fi
if ! {
    printf 'url = "https://api.telegram.org/bot%s/sendMessage"\n' "$bot_token"
    printf 'request = "POST"\n'
    printf 'data-urlencode = "chat_id=%s"\n' "$chat_id"
    printf 'data-urlencode = "text=%s"\n' "$message"
} | curl --silent --show-error --fail --max-time 8 --config - >/dev/null; then
    echo "PremierAPI startup alert could not be delivered." >&2
    exit 3
fi

if (( test_mode == 0 )); then
    touch "$last_alert_file"
fi
