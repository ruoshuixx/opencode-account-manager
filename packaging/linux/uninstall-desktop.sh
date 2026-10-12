#!/usr/bin/env bash
set -euo pipefail

fail() {
    printf 'uninstall-desktop.sh: %s\n' "$*" >&2
    exit 1
}

for command_name in readlink dirname sha256sum rm; do
    command -v "$command_name" >/dev/null 2>&1 || fail "Required command not found: $command_name"
done

if [[ -n "${XDG_DATA_HOME:-}" ]]; then
    [[ "$XDG_DATA_HOME" == /* ]] || fail 'XDG_DATA_HOME must be an absolute path.'
    data_home="$XDG_DATA_HOME"
else
    [[ -n "${HOME:-}" && "$HOME" == /* ]] || fail 'HOME must be an absolute path when XDG_DATA_HOME is unset.'
    data_home="$HOME/.local/share"
fi

script_path="$(readlink -f -- "${BASH_SOURCE[0]}")"
script_dir="$(cd -- "$(dirname -- "$script_path")" && pwd -P)"
[[ -f "$script_dir/opencode-account-manager.desktop.in" ]] || fail 'Run this script from an intact extracted package.'

desktop_path="$data_home/applications/opencode-account-manager.desktop"
icon_path="$data_home/icons/hicolor/256x256/apps/opencode-account-manager.png"
marker_path="$data_home/opencode-account-manager/desktop-install.marker"
sha256_of() {
    local line
    line="$(sha256sum < "$1")" || return 1
    printf '%s\n' "${line%% *}"
}

if [[ ! -e "$marker_path" && ! -L "$marker_path" ]]; then
    if [[ -e "$desktop_path" || -L "$desktop_path" || -e "$icon_path" || -L "$icon_path" ]]; then
        fail 'No valid ownership marker exists; leaving the existing desktop entry and icon untouched.'
    fi
    printf 'No owned desktop entry is installed.\n'
    exit 0
fi
[[ -f "$marker_path" && ! -L "$marker_path" ]] || fail 'Ownership marker is not a regular file; nothing was removed.'

marker_format=''
marker_format_count=0
owned_desktop_hash=''
owned_icon_hash=''
marker_desktop_count=0
marker_icon_count=0
while IFS='=' read -r key value || [[ -n "${key:-}" ]]; do
    case "$key" in
        format)
            ((marker_format_count == 0)) || fail 'Ownership marker has duplicate fields.'
            marker_format="$value"
            marker_format_count=1
            ;;
        desktop_sha256)
            ((marker_desktop_count == 0)) || fail 'Ownership marker has duplicate fields.'
            owned_desktop_hash="$value"
            marker_desktop_count=1
            ;;
        icon_sha256)
            ((marker_icon_count == 0)) || fail 'Ownership marker has duplicate fields.'
            owned_icon_hash="$value"
            marker_icon_count=1
            ;;
        *) fail 'Ownership marker contains an unknown field.' ;;
    esac
done < "$marker_path"
[[ "$marker_format" == '1' && "$marker_format_count" -eq 1 && "$marker_desktop_count" -eq 1 && "$marker_icon_count" -eq 1 ]] || fail 'Ownership marker is incomplete or unsupported; nothing was removed.'
[[ "$owned_desktop_hash" =~ ^[0-9a-f]{64}$ && "$owned_icon_hash" =~ ^[0-9a-f]{64}$ ]] || fail 'Ownership marker contains an invalid hash; nothing was removed.'

for owned_pair in "$desktop_path:$owned_desktop_hash" "$icon_path:$owned_icon_hash"; do
    owned_path="${owned_pair%:*}"
    expected_hash="${owned_pair##*:}"
    if [[ -e "$owned_path" || -L "$owned_path" ]]; then
        [[ -f "$owned_path" && ! -L "$owned_path" ]] || fail "Entry/icon is no longer a regular owned file; nothing was removed: $owned_path"
        [[ "$(sha256_of "$owned_path")" == "$expected_hash" ]] || fail "Entry/icon was changed after installation; nothing was removed: $owned_path"
    fi
done

[[ ! -e "$desktop_path" && ! -L "$desktop_path" ]] || rm -- "$desktop_path"
[[ ! -e "$icon_path" && ! -L "$icon_path" ]] || rm -- "$icon_path"
rm -- "$marker_path"
printf 'Removed only the unchanged user desktop entry and icon owned by OpenCode Account Manager.\n'
