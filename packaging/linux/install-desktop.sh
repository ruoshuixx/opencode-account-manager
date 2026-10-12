#!/usr/bin/env bash
set -euo pipefail

fail() {
    printf 'install-desktop.sh: %s\n' "$*" >&2
    exit 1
}

for command_name in readlink dirname mkdir mktemp cp chmod mv rm sha256sum grep; do
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
template="$script_dir/opencode-account-manager.desktop.in"
icon_source="$script_dir/opencode-account-manager.png"
[[ -f "$template" && ! -L "$template" ]] || fail 'Adjacent desktop template is missing or linked.'
[[ -f "$icon_source" && ! -L "$icon_source" ]] || fail 'Adjacent PNG icon is missing or linked.'

exec_path="$script_dir/start.sh"
[[ -f "$exec_path" && -x "$exec_path" ]] || fail 'Adjacent start.sh is missing or not executable.'
escape_desktop_exec_arg() {
    local value="$1"
    local escaped=''
    local character
    local index
    [[ "$value" != *'='* ]] || fail 'Desktop Entry Exec cannot represent paths containing "=".'
    [[ "$value" != *$'\n'* && "$value" != *$'\r'* && "$value" != *$'\t'* ]] || fail 'Install path contains a control character unsupported by Desktop Entry Exec.'
    for ((index = 0; index < ${#value}; index++)); do
        character="${value:index:1}"
        case "$character" in
            \\) escaped+='\\\\' ;;
            '"') escaped+='\\'; escaped+='"' ;;
            '$') escaped+='\\$' ;;
            '`') escaped+='\\`' ;;
            '%') escaped+='%%' ;;
            *) escaped+="$character" ;;
        esac
    done
    printf '"%s"' "$escaped"
}
exec_value="$(escape_desktop_exec_arg "$exec_path")"

desktop_dir="$data_home/applications"
icon_dir="$data_home/icons/hicolor/256x256/apps"
owner_dir="$data_home/opencode-account-manager"
desktop_path="$desktop_dir/opencode-account-manager.desktop"
icon_path="$icon_dir/opencode-account-manager.png"
marker_path="$owner_dir/desktop-install.marker"
mkdir -p -- "$desktop_dir" "$icon_dir" "$owner_dir"
[[ ! -L "$marker_path" ]] || fail 'Ownership marker is a symlink; refusing to use it.'

sha256_of() {
    local line
    line="$(sha256sum < "$1")" || return 1
    printf '%s\n' "${line%% *}"
}

marker_exists=0
owned_desktop_hash=''
owned_icon_hash=''
if [[ -e "$marker_path" || -L "$marker_path" ]]; then
    [[ -f "$marker_path" && ! -L "$marker_path" ]] || fail 'Ownership marker is not a regular file.'
    marker_exists=1
    marker_format=''
    marker_format_count=0
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
    [[ "$marker_format" == '1' && "$marker_format_count" -eq 1 && "$marker_desktop_count" -eq 1 && "$marker_icon_count" -eq 1 ]] || fail 'Ownership marker is incomplete or unsupported.'
    [[ "$owned_desktop_hash" =~ ^[0-9a-f]{64}$ && "$owned_icon_hash" =~ ^[0-9a-f]{64}$ ]] || fail 'Ownership marker contains an invalid hash.'
    for owned_pair in "$desktop_path:$owned_desktop_hash" "$icon_path:$owned_icon_hash"; do
        owned_path="${owned_pair%:*}"
        expected_hash="${owned_pair##*:}"
        [[ -f "$owned_path" && ! -L "$owned_path" ]] || fail "Previously owned desktop entry/icon is missing or changed: $owned_path"
        [[ "$(sha256_of "$owned_path")" == "$expected_hash" ]] || fail "Previously owned desktop entry/icon was modified: $owned_path"
    done
else
    for target_path in "$desktop_path" "$icon_path"; do
        [[ ! -e "$target_path" && ! -L "$target_path" ]] || fail "Refusing to overwrite an existing unowned entry or icon: $target_path"
    done
fi

desktop_tmp=''
icon_tmp=''
marker_tmp=''
created_desktop=0
created_icon=0
created_marker=0
rollback_install=1
desktop_hash=''
icon_hash=''
marker_hash=''

remove_if_still_ours() {
    local path="$1"
    local expected="$2"
    if [[ -f "$path" && ! -L "$path" ]] && [[ "$(sha256_of "$path" 2>/dev/null || true)" == "$expected" ]]; then
        rm -- "$path"
    fi
}

cleanup() {
    local status=$?
    trap - EXIT
    [[ -z "${desktop_tmp:-}" || ! -e "$desktop_tmp" ]] || rm -f -- "$desktop_tmp"
    [[ -z "${icon_tmp:-}" || ! -e "$icon_tmp" ]] || rm -f -- "$icon_tmp"
    [[ -z "${marker_tmp:-}" || ! -e "$marker_tmp" ]] || rm -f -- "$marker_tmp"
    if ((rollback_install)); then
        if ((created_marker)); then remove_if_still_ours "$marker_path" "$marker_hash"; fi
        if ((created_icon)); then remove_if_still_ours "$icon_path" "$icon_hash"; fi
        if ((created_desktop)); then remove_if_still_ours "$desktop_path" "$desktop_hash"; fi
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

desktop_tmp="$(mktemp --suffix=.desktop "$desktop_dir/.opencode-account-manager.XXXXXXXX")"
icon_tmp="$(mktemp "$icon_dir/.opencode-account-manager.png.XXXXXXXX")"

exec_lines=0
while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == 'Exec=@OCAM_EXEC@' ]]; then
        exec_lines=$((exec_lines + 1))
        printf 'Exec=%s\n' "$exec_value"
    else
        printf '%s\n' "$line"
    fi
done < "$template" > "$desktop_tmp"
[[ "$exec_lines" -eq 1 ]] || fail 'Desktop template must contain exactly one Exec=@OCAM_EXEC@ placeholder.'
cp -- "$icon_source" "$icon_tmp"
chmod 0644 -- "$desktop_tmp" "$icon_tmp"
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$desktop_tmp" || fail 'desktop-file-validate rejected the generated desktop entry.'
fi
desktop_hash="$(sha256_of "$desktop_tmp")"
icon_hash="$(sha256_of "$icon_tmp")"

if ((marker_exists)); then
    if [[ "$desktop_hash" == "$owned_desktop_hash" && "$icon_hash" == "$owned_icon_hash" ]]; then
        printf 'Desktop entry is already installed for this extraction directory.\n'
        rollback_install=0
        exit 0
    fi
    fail 'An owned entry already points elsewhere or has a different icon; run its uninstall script before installing this extraction.'
fi

marker_tmp="$(mktemp "$owner_dir/.desktop-install.marker.XXXXXXXX")"
printf 'format=1\ndesktop_sha256=%s\nicon_sha256=%s\n' "$desktop_hash" "$icon_hash" > "$marker_tmp"
chmod 0600 -- "$marker_tmp"

mv -nT -- "$desktop_tmp" "$desktop_path"
[[ ! -e "$desktop_tmp" ]] || fail 'Desktop entry destination appeared during installation; leaving it untouched.'
created_desktop=1
mv -nT -- "$icon_tmp" "$icon_path"
[[ ! -e "$icon_tmp" ]] || fail 'Icon destination appeared during installation; leaving it untouched.'
created_icon=1
marker_hash="$(sha256_of "$marker_tmp")"
mv -nT -- "$marker_tmp" "$marker_path"
[[ ! -e "$marker_tmp" ]] || fail 'Ownership marker destination appeared during installation; leaving it untouched.'
created_marker=1
rollback_install=0
printf 'Installed user desktop entry: %s\n' "$desktop_path"
