#!/bin/bash
set -u

operation="${1:-}"
job_id="${2:-}"
project_root="/var/www/premierhost/PremierAPI"
state_dir="/run/premierapi-maintenance"
dotnet_cli_home="/var/cache/premierapi-dotnet"
status_file="${state_dir}/${job_id}.json"
log_file="${state_dir}/${job_id}.log"
lock_file="${state_dir}/operation.lock"
warnings=0

umask 077
install -d -m 700 "$state_dir"
install -d -m 700 "$dotnet_cli_home"
export DOTNET_CLI_HOME="$dotnet_cli_home"

write_status() {
    phase="$1"
    message="$2"
    updated_at="$(date -u +'%Y-%m-%dT%H:%M:%S%:z')"
    temporary_status="${status_file}.tmp"
    printf '{"jobId":"%s","operation":"%s","phase":"%s","message":"%s","warnings":%s,"updatedAt":"%s","logTail":null}\n' \
        "$job_id" "$operation" "$phase" "$message" "$warnings" "$updated_at" > "$temporary_status"
    mv "$temporary_status" "$status_file"
}

if [[ ! "$job_id" =~ ^[a-f0-9]{32}$ ]] || [[ "$operation" != "publish" && "$operation" != "restart" ]]; then
    exit 2
fi

exec 9>"$lock_file"
if ! flock -n 9; then
    write_status "failed" "Outra manutencao ja esta em andamento."
    exit 3
fi

started_at="$(date --iso-8601=seconds)"
: > "$log_file"
sleep 2

if [[ "$operation" == "publish" ]]; then
    write_status "building" "Compilando os assets e a aplicacao em Release..."
    if ! (cd "$project_root" && npm run assets:build && dotnet build --configuration Release --no-restore) >> "$log_file" 2>&1; then
        write_status "failed" "A compilacao falhou. O servico atual foi mantido."
        exit 4
    fi
    warnings="$(sed -nE 's/^[[:space:]]*([0-9]+) Warning\(s\).*$/\1/p' "$log_file" | tail -n 1)"
    warnings="${warnings:-0}"
fi

write_status "restarting" "Reiniciando o servico premierapi..."
if ! systemctl restart premierapi >> "$log_file" 2>&1; then
    write_status "failed" "O servico nao pode ser reiniciado."
    exit 5
fi

write_status "waiting" "Aguardando a API voltar a responder..."
for _ in $(seq 1 90); do
    if systemctl is-active --quiet premierapi && curl -fsS --max-time 2 http://127.0.0.1:5000/api/checkout/pricing-rules > /dev/null; then
        journalctl -u premierapi --since "$started_at" --no-pager -n 80 >> "$log_file" 2>&1 || true
        if (( warnings > 0 )); then
            write_status "warning" "Aplicacao reiniciada, mas a compilacao apresentou avisos."
        elif [[ "$operation" == "publish" ]]; then
            write_status "success" "Aplicacao compilada e reiniciada com sucesso."
        else
            write_status "success" "Servico premierapi reiniciado com sucesso."
        fi
        exit 0
    fi
    sleep 1
done

journalctl -u premierapi --since "$started_at" --no-pager -n 80 >> "$log_file" 2>&1 || true
write_status "failed" "A API nao voltou a responder dentro do tempo esperado."
exit 6
