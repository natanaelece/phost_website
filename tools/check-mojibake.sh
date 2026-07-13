#!/usr/bin/env bash
set -euo pipefail

# Safe mojibake scanner for PremierAPI.
# Read-only by default: it never rewrites files.
#
# Usage:
#   bash tools/check-mojibake.sh
#   bash tools/check-mojibake.sh /path/to/project

ROOT="${1:-$(pwd)}"

if ! command -v python3 >/dev/null 2>&1; then
  echo "Erro: python3 não encontrado. Instale python3 no LXC para rodar este scanner." >&2
  exit 2
fi

python3 - "$ROOT" <<'PY'
import os
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()

SKIP_DIRS = {
    ".git", ".agents", ".codex", "bin", "obj", "node_modules",
    "dump_db", ".vs", ".vscode"
}

TEXT_EXTS = {
    ".cs", ".cshtml", ".html", ".css", ".js", ".json", ".md",
    ".sh", ".ps1", ".txt", ".xml", ".yml", ".yaml", ".http",
    ".config", ".csproj", ".sln"
}

# Strong mojibake signatures. This avoids flagging normal PT-BR accents,
# markdown tree characters like "├──", and emojis that are already valid.
PATTERNS = [
    ("utf8_as_cp1252", re.compile(r"(?:Ã[\x80-\xBF]|Â[\x80-\xBF]|â[\x80-\xBF])")),
    ("box_drawing_mojibake", re.compile(r"(?:Ôö|Ô£|ÔÇ|Γ)")),
    ("replacement_char", re.compile(r"�")),
    ("double_encoded", re.compile(r"Ãƒ|Ã‚|Ã¢|â‚¬|â„¢|â€œ|â€\x9d|â€\x9c|â€“|â€”")),
]

def is_candidate(path: Path) -> bool:
    rel = path.relative_to(root).as_posix()
    if rel == "tools/check-mojibake.sh":
        return False
    if path.name in {".DS_Store"}:
        return False
    if path.suffix.lower() in TEXT_EXTS:
        return True
    return path.name in {"Dockerfile", "Makefile", "Caddyfile"}

def iter_files(base: Path):
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            path = Path(dirpath) / name
            if is_candidate(path):
                yield path

findings = []

for path in iter_files(root):
    rel = path.relative_to(root)
    try:
        raw = path.read_bytes()
    except OSError as exc:
        findings.append((str(rel), 0, "read_error", str(exc)))
        continue

    if raw.startswith(b"\xef\xbb\xbf"):
        findings.append((str(rel), 1, "utf8_bom", "arquivo tem BOM; prefira UTF-8 sem BOM"))

    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        findings.append((str(rel), exc.start + 1, "invalid_utf8", str(exc)))
        continue

    for line_no, line in enumerate(text.splitlines(), 1):
        for label, pattern in PATTERNS:
            if pattern.search(line):
                snippet = line.strip()
                if len(snippet) > 220:
                    snippet = snippet[:217] + "..."
                findings.append((str(rel), line_no, label, snippet))
                break

if findings:
    print("Possíveis problemas de encoding encontrados:\n")
    for file, line, label, snippet in findings:
        location = f"{file}:{line}" if line else file
        print(f"- {location} [{label}]")
        print(f"  {snippet}")
    print(f"\nTotal: {len(findings)} ocorrência(s).")
    print("Nada foi alterado. Revise os pontos acima e corrija por patch/manual.")
    sys.exit(1)

print("OK: nenhum mojibake forte, BOM ou UTF-8 inválido detectado.")
PY
