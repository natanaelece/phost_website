#!/bin/bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

cd "$project_root"

echo "Compilando assets do frontend..."
npm run assets:build

echo "Compilando a aplicacao em Release..."
dotnet build --configuration Release --no-restore

"$project_root/restart.sh"
