#!/usr/bin/env bash
# Phase L WP-G (#121) — Apone (DevOps).
#
# Idempotent first-run bootstrap for the default `docker compose` quickstart.
#
# The shipped image runs `ASPNETCORE_ENVIRONMENT=Production`, which refuses to
# boot without a STABLE JWT signing key (a per-process random key would
# silently invalidate every JWT on restart — see JwtSigningKeyProvider.cs /
# docs/jwt-rotation.md §7). docker-compose.yml consumes `JWT_SIGNING_KEY` from
# the auto-loaded `.env` and injects it as `Authentication__JwtSigningKeys__0`.
#
# This script guarantees `.env` exists and carries a stable `JWT_SIGNING_KEY`:
#   * First run: seeds `.env` from `.env.example` (if present) and appends a
#     freshly generated key.
#   * Subsequent runs: a NO-OP when a non-empty key already exists, so the key
#     stays stable across restarts (previously issued JWTs keep validating).
#
# It NEVER commits or bakes a secret: `.env` is gitignored and dockerignored.
# Safe to run before every `docker compose up` (idempotent by design).
#
# Usage:
#   ./scripts/compose-bootstrap.sh            # ensures ./.env has a JWT key
#   ./scripts/compose-bootstrap.sh --env-file /path/to/project.env
#   ./scripts/compose-bootstrap.sh && docker compose up -d --build

set -euo pipefail

# Resolve repo root from this script's location so it works from any CWD.
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env"
EXAMPLE_FILE="$REPO_ROOT/.env.example"
if (($#)); then
    if [[ $# -ne 2 || "$1" != --env-file || -z "$2" ]]; then
        printf 'Usage: %s [--env-file PATH]\n' "$0" >&2
        exit 2
    fi
    ENV_FILE="$2"
    [[ "$ENV_FILE" == /* ]] || ENV_FILE="$PWD/$ENV_FILE"
fi
umask 077

# 1. Seed .env from the template on first run.
if [[ ! -f "$ENV_FILE" ]]; then
    if [[ -f "$EXAMPLE_FILE" ]]; then
        cp "$EXAMPLE_FILE" "$ENV_FILE"
        echo "Created $ENV_FILE from $EXAMPLE_FILE."
    else
        : > "$ENV_FILE"
        echo "Created empty $ENV_FILE."
    fi
fi

# Classify literal emptiness, not key validity or interpolation. A leading #
# is an unquoted value, and whitespace inside quotes is still key material.
# Reject unsupported syntax rather than mistaking it for an absent/empty key.
# Only the classification leaves awk; never source or print env-file values.
if ! KEY_STATE="$(awk '
    function unsupported() {
        invalid = 1
        exit 2
    }
    BEGIN { state = "empty"; single_quote = sprintf("%c", 39) }
    {
        line = $0
        sub(/\r$/, "", line)
        sub(/^[[:space:]]+/, "", line)
        if (line == "" || line ~ /^#/) next
        sub(/^export[[:space:]]+/, "", line)
        name = line
        sub(/[[:space:]=:].*$/, "", name)
        if (name !~ /^[[:alnum:]_.-]+$/) unsupported()
        rest = substr(line, length(name) + 1)
        sub(/^[[:space:]]+/, "", rest)
        if (rest !~ /^[=:]/) {
            if (name == "JWT_SIGNING_KEY" || (rest != "" && rest !~ /^#/)) unsupported()
            next
        }
        value = substr(rest, 2)
        sub(/^[[:space:]]+/, "", value)
        empty = value == ""
        quote = substr(value, 1, 1)
        if (quote == "\"" || quote == single_quote) {
            end = 0
            for (i = 2; i <= length(value); i++) {
                char = substr(value, i, 1)
                if (char == "\\") { i++; continue }
                if (char == quote) { end = i; break }
            }
            # Multiline values could contain assignment-looking text.
            if (!end) unsupported()
            tail = substr(value, end + 1)
            sub(/^[[:space:]]+/, "", tail)
            if (tail != "" && tail !~ /^#/) unsupported()
            empty = end == 2
        }
        if (name == "JWT_SIGNING_KEY") state = empty ? "empty" : "configured"
    }
    END {
        if (invalid) exit 2
        print state
    }
' "$ENV_FILE")"; then
    printf '%s\n' 'Unsupported env-file syntax; no JWT signing key was generated. Use explicit single-line assignments.' >&2
    exit 1
fi
if [[ "$KEY_STATE" == "configured" ]]; then
    echo "JWT_SIGNING_KEY already set in $ENV_FILE — leaving it unchanged."
    printf 'Next: docker compose --env-file "%s" -f "%s/docker-compose.yml" up -d --build\n' "$ENV_FILE" "$REPO_ROOT"
    exit 0
fi

# 3. Generate a strong base64 key (~48 bytes). Prefer openssl; fall back to
#    /dev/urandom so the script works on minimal hosts.
if command -v openssl >/dev/null 2>&1; then
    KEY="$(openssl rand -base64 48 | tr -d '\n')"
else
    KEY="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
fi

# 4. Append the active key. Ensure a trailing newline separation.
[[ -s "$ENV_FILE" && -n "$(tail -c1 "$ENV_FILE")" ]] && printf '\n' >> "$ENV_FILE"
printf 'JWT_SIGNING_KEY=%s\n' "$KEY" >> "$ENV_FILE"

echo "Wrote a new stable JWT_SIGNING_KEY to $ENV_FILE (gitignored — never commit it)."
printf 'Next: docker compose --env-file "%s" -f "%s/docker-compose.yml" up -d --build\n' "$ENV_FILE" "$REPO_ROOT"
