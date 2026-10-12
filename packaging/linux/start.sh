#!/usr/bin/env bash
set -euo pipefail

for command_name in readlink dirname; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'start.sh: required command not found: %s\n' "$command_name" >&2
        exit 1
    }
done

script_path="$(readlink -f -- "${BASH_SOURCE[0]}")"
script_dir="$(cd -- "$(dirname -- "$script_path")" && pwd -P)"
program="$script_dir/OpenCodeAccountManager"
if [[ ! -f "$program" || ! -x "$program" ]]; then
    printf 'start.sh: adjacent OpenCodeAccountManager is missing or not executable.\n' >&2
    exit 1
fi

exec "$program" "$@"
