#!/usr/bin/env bash
# Native-only regression fixture, invoked by Test-Linux.sh. Leave unrun on this Windows host.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "$script_dir/.." && pwd -P)"

fail() {
    printf 'packaging-fix-round1-native-fixture: %s\n' "$*" >&2
    exit 1
}

for command_name in bash readlink dirname mktemp cp chmod mkdir cmp grep desktop-file-validate find sort tar sha256sum stat; do
    command -v "$command_name" >/dev/null 2>&1 || fail "Required native test command not found: $command_name"
done

fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/ocam-packaging-fix-round1.XXXXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    rm -rf -- "$fixture_root"
    exit "$status"
}
trap cleanup EXIT

special_segment='space 中文 "quote" \backslash $dollar `tick` %percent'
package_dir="$fixture_root/portable $special_segment"
xdg_home="$fixture_root/xdg $special_segment"
mkdir -p -- "$package_dir"
for package_file in start.sh install-desktop.sh uninstall-desktop.sh opencode-account-manager.desktop.in opencode-account-manager.png; do
    cp -- "$repo_root/packaging/linux/$package_file" "$package_dir/$package_file"
done
chmod 0755 -- "$package_dir/start.sh" "$package_dir/install-desktop.sh" "$package_dir/uninstall-desktop.sh"
cat > "$package_dir/OpenCodeAccountManager" <<'FAKE_APP'
#!/usr/bin/env bash
printf '%s\0' "$@" > "$ARG_CAPTURE"
FAKE_APP
chmod 0755 -- "$package_dir/OpenCodeAccountManager"

ARG_CAPTURE="$fixture_root/start-args.bin" "$package_dir/start.sh" \
    'space arg' 'quote"arg' 'back\slash' '$dollar' '`tick`' 'percent%arg'
printf '%s\0' 'space arg' 'quote"arg' 'back\slash' '$dollar' '`tick`' 'percent%arg' > "$fixture_root/start-args.expected"
cmp -s -- "$fixture_root/start-args.bin" "$fixture_root/start-args.expected" || fail 'start.sh did not preserve the synthetic argument vector.'

XDG_DATA_HOME="$xdg_home" HOME="$fixture_root/home" "$package_dir/install-desktop.sh"
desktop_file="$xdg_home/applications/opencode-account-manager.desktop"
icon_file="$xdg_home/icons/hicolor/256x256/apps/opencode-account-manager.png"
owner_marker="$xdg_home/opencode-account-manager/desktop-install.marker"
[[ -f "$desktop_file" && -f "$icon_file" && -f "$owner_marker" ]] || fail 'Install did not create the expected user-level entry, icon, and marker.'
desktop-file-validate "$desktop_file"
grep -Fq 'Exec="' "$desktop_file" || fail 'Generated desktop Exec is not quoted as one argument.'
grep -Fq '%%percent' "$desktop_file" || fail 'Generated desktop Exec did not escape a literal percent.'

cp -- "$desktop_file" "$fixture_root/desktop-entry-original"
printf '\n# synthetic user modification\n' >> "$desktop_file"
if XDG_DATA_HOME="$xdg_home" HOME="$fixture_root/home" "$package_dir/uninstall-desktop.sh" > "$fixture_root/uninstall-changed.log" 2>&1; then
    fail 'Uninstall accepted a modified desktop entry.'
fi
grep -Fq 'changed after installation' "$fixture_root/uninstall-changed.log" || fail 'Changed-entry refusal did not explain the ownership mismatch.'
[[ -f "$desktop_file" && -f "$icon_file" && -f "$owner_marker" ]] || fail 'Changed-entry refusal removed part of the installation.'
cp -- "$fixture_root/desktop-entry-original" "$desktop_file"
XDG_DATA_HOME="$xdg_home" HOME="$fixture_root/home" "$package_dir/uninstall-desktop.sh"
[[ ! -e "$desktop_file" && ! -e "$icon_file" && ! -e "$owner_marker" ]] || fail 'Uninstall did not remove the unchanged owned desktop artifacts.'
[[ -f "$package_dir/OpenCodeAccountManager" ]] || fail 'Desktop uninstall removed the adjacent program.'

equals_package="$fixture_root/path=must-be-rejected"
equals_xdg="$fixture_root/equals-xdg"
mkdir -p -- "$equals_package"
for package_file in start.sh install-desktop.sh opencode-account-manager.desktop.in opencode-account-manager.png; do
    cp -- "$repo_root/packaging/linux/$package_file" "$equals_package/$package_file"
done
chmod 0755 -- "$equals_package/start.sh" "$equals_package/install-desktop.sh"
if XDG_DATA_HOME="$equals_xdg" HOME="$fixture_root/home" "$equals_package/install-desktop.sh" > "$fixture_root/equals-path.log" 2>&1; then
    fail 'Install accepted an executable path containing equals.'
fi
grep -Fq 'cannot represent paths containing "="' "$fixture_root/equals-path.log" || fail 'Equals-path rejection was not explicit.'
[[ ! -e "$equals_xdg" ]] || fail 'Equals-path rejection wrote user desktop state before failing.'

# This fake SDK output validates packaging inventory/checksums only. Its shell stub is not an ELF or bundle test.
fake_bin="$fixture_root/fake-bin"
mkdir -p -- "$fake_bin"
cat > "$fake_bin/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == '--version' ]]; then
    printf '8.0.406\n'
    exit 0
fi
[[ "${1:-}" == 'publish' ]] || exit 90
shift
output=''
artifacts=''
while (($#)); do
    case "$1" in
        --output) output="$2"; shift 2 ;;
        --artifacts-path) artifacts="$2"; shift 2 ;;
        *) shift ;;
    esac
done
[[ -n "$output" && -n "$artifacts" ]]
mkdir -p -- "$output" "$artifacts/obj/AccountManager.App"
cat > "$output/OpenCodeAccountManager" <<'FAKE_APPHOST'
#!/usr/bin/env bash
exit 0
FAKE_APPHOST
chmod 0755 -- "$output/OpenCodeAccountManager"
printf '%s\n' '{"libraries":{"Microsoft.NETCore.App.Runtime.linux-x64/8.0.31":{"type":"package"}}}' > "$artifacts/obj/AccountManager.App/project.assets.json"
FAKE_DOTNET
chmod 0755 -- "$fake_bin/dotnet"
publish_output="$fixture_root/published output 中文"
PATH="$fake_bin:$PATH" bash "$repo_root/scripts/Publish-Linux.sh" 9.8.7 "$publish_output"

mapfile -t published_members < <(find "$publish_output" -mindepth 1 -maxdepth 1 -printf '%f\t%y\n' | sort)
[[ ${#published_members[@]} -eq 4 ]] || fail 'Publisher output did not contain exactly four immediate members.'
expected_assets=(
    'OpenCodeAccountManager-linux-x64'
    'OpenCodeAccountManager-linux-x64.sha256'
    'OpenCodeAccountManager-linux-x64.tar.gz'
    'OpenCodeAccountManager-linux-x64.tar.gz.sha256'
)
for member in "${published_members[@]}"; do
    asset_name="${member%%$'\t'*}"
    asset_kind="${member#*$'\t'}"
    [[ "$asset_kind" == 'f' ]] || fail "Publisher emitted a non-file top-level member: $asset_name"
    expected=0
    for expected_asset in "${expected_assets[@]}"; do
        [[ "$asset_name" == "$expected_asset" ]] && expected=1
    done
    [[ "$expected" -eq 1 ]] || fail "Publisher emitted an unexpected top-level asset: $asset_name"
done
for expected_asset in "${expected_assets[@]}"; do
    [[ -f "$publish_output/$expected_asset" && ! -L "$publish_output/$expected_asset" ]] || fail "Publisher omitted a regular asset: $expected_asset"
done
(
    cd -- "$publish_output"
    sha256sum --check OpenCodeAccountManager-linux-x64.sha256
    sha256sum --check OpenCodeAccountManager-linux-x64.tar.gz.sha256
)
[[ "$(stat -c '%a' "$publish_output/OpenCodeAccountManager-linux-x64")" == '755' ]] || fail 'Standalone synthetic apphost mode is not 0755.'
[[ "$(stat -c '%a' "$publish_output/OpenCodeAccountManager-linux-x64.sha256")" == '644' ]] || fail 'Checksum mode is not 0644.'

archive="$publish_output/OpenCodeAccountManager-linux-x64.tar.gz"
archive_root='opencode-account-manager-linux-x64'
expected_members=(
    "$archive_root/"
    "$archive_root/OpenCodeAccountManager"
    "$archive_root/start.sh"
    "$archive_root/install-desktop.sh"
    "$archive_root/uninstall-desktop.sh"
    "$archive_root/opencode-account-manager.desktop.in"
    "$archive_root/opencode-account-manager.png"
    "$archive_root/README.txt"
)
mapfile -t archive_members < <(tar --list --gzip --file="$archive")
mapfile -t verbose_members < <(tar --list --verbose --gzip --file="$archive")
[[ ${#archive_members[@]} -eq 8 && ${#verbose_members[@]} -eq 8 ]] || fail 'Portable archive does not contain exactly eight entries including its fixed root.'
for expected_member in "${expected_members[@]}"; do
    found=0
    for archive_member in "${archive_members[@]}"; do
        [[ "$archive_member" == "$expected_member" ]] && found=1
    done
    [[ "$found" -eq 1 ]] || fail "Portable archive omitted expected member: $expected_member"
done
for verbose_member in "${verbose_members[@]}"; do
    mode="${verbose_member:0:10}"
    case "$verbose_member" in
        *"$archive_root/" ) expected_mode='drwxr-xr-x' ;;
        *"$archive_root/OpenCodeAccountManager" ) expected_mode='-rwxr-xr-x' ;;
        *"$archive_root/start.sh"|*"$archive_root/install-desktop.sh"|*"$archive_root/uninstall-desktop.sh" ) expected_mode='-rwxr-xr-x' ;;
        *"$archive_root/opencode-account-manager.desktop.in"|*"$archive_root/opencode-account-manager.png"|*"$archive_root/README.txt" ) expected_mode='-rw-r--r--' ;;
        *) fail "Unexpected archive member while checking modes: $verbose_member" ;;
    esac
    [[ "$mode" == "$expected_mode" ]] || fail "Archive member has mode $mode instead of $expected_mode: $verbose_member"
done

printf 'PASS: synthetic desktop/package inventory checks only; fake apphost was not executed and is not bundle evidence.\n'
