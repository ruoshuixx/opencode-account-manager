#!/usr/bin/env bash
set -euo pipefail

script_path="$(readlink -f -- "${BASH_SOURCE[0]}")"
script_dir="$(cd -- "$(dirname -- "$script_path")" && pwd -P)"
root_dir="$(cd -- "$script_dir/.." && pwd -P)"
project="$root_dir/src/AccountManager.App/AccountManager.App.csproj"
requested_runtime='8.0.31'
archive_root='opencode-account-manager-linux-x64'

fail() {
    printf 'Publish-Linux: %s\n' "$*" >&2
    exit 1
}

need_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command not found: $1"
}

for command_name in readlink dirname basename dotnet mktemp mkdir cd pwd find grep sed head wc od dd cmp install chmod cp tar gzip sha256sum mv rm tr; do
    need_command "$command_name"
done

[[ -f "$project" && ! -L "$project" ]] || fail "App project not found: $project"

default_version="$(sed -nE 's@.*<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>.*@\1@p' "$project" | head -n 1)"
version="${1:-$default_version}"
if (($# > 2)); then
    fail 'Usage: Publish-Linux.sh [major.minor.patch] [new-output-directory]'
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Version must contain exactly three numeric components.'
[[ -n "$version" ]] || fail 'The App project has no numeric three-part <Version>.'

if (($# == 2)); then
    [[ -n "$2" ]] || fail 'Output directory cannot be empty.'
    output_input="$2"
else
    output_input="$root_dir/release/linux-v$version"
fi
if [[ "$output_input" != /* ]]; then
    output_input="$PWD/$output_input"
fi
output_name="$(basename -- "$output_input")"
output_parent_input="$(dirname -- "$output_input")"
[[ "$output_name" != '.' && "$output_name" != '..' && -n "$output_name" ]] || fail 'Output path must name a new directory.'
mkdir -p -- "$output_parent_input"
output_parent="$(cd -- "$output_parent_input" && pwd -P)"
output_dir="$output_parent/$output_name"
if [[ -e "$output_dir" || -L "$output_dir" ]]; then
    fail "Refusing to overwrite existing output: $output_dir"
fi

builder_sdk="$(dotnet --version)"
[[ -n "$builder_sdk" ]] || fail 'Unable to determine the selected .NET SDK.'
build_work="$(mktemp -d "${TMPDIR:-/tmp}/ocam-linux-publish.XXXXXXXX")"
package_stage=''
cleanup() {
    local status=$?
    trap - EXIT
    if [[ -n "${build_work:-}" && -d "$build_work" ]]; then
        rm -rf -- "$build_work"
    fi
    if [[ -n "${package_stage:-}" && -d "$package_stage" ]]; then
        rm -rf -- "$package_stage"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
package_stage="$(mktemp -d "$output_parent/.opencode-account-manager-stage.XXXXXXXX")"

publish_dir="$build_work/publish"
mkdir -m 0755 -- "$publish_dir"
dotnet publish "$project" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$publish_dir" \
    --artifacts-path "$build_work/artifacts" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:RuntimeFrameworkVersion=8.0.31 \
    "-p:Version=$version"

resolved_runtime=''
while IFS= read -r candidate_assets; do
    if grep -q '"Microsoft\.NETCore\.App\.Runtime\.linux-x64/' "$candidate_assets"; then
        resolved_runtime="$(grep -oE '"Microsoft\.NETCore\.App\.Runtime\.linux-x64/[0-9]+\.[0-9]+\.[0-9]+"' "$candidate_assets" | sed -n '1p' | sed -E 's@.*/([^/"]+)"@\1@')"
    elif grep -q '"name": "Microsoft\.NETCore\.App\.Runtime\.linux-x64"' "$candidate_assets"; then
        # A fresh restore records the pending runtime pack under downloadDependencies with an exact pin
        # such as "[8.0.31, 8.0.31]" instead of a libraries entry; read the pin from the line after the name.
        resolved_runtime="$(grep -A1 '"name": "Microsoft\.NETCore\.App\.Runtime\.linux-x64"' "$candidate_assets" | grep -oE '\[[0-9]+\.[0-9]+\.[0-9]+, [0-9]+\.[0-9]+\.[0-9]+\]' | sed -n '1p' | sed -E 's/^\[([0-9.]+),.*/\1/')"
    fi
    if [[ -n "$resolved_runtime" ]]; then
        break
    fi
done < <(find "$build_work/artifacts" -type f -name project.assets.json -print)
[[ -n "$resolved_runtime" ]] || fail 'Could not read the resolved Linux .NET runtime package version from isolated restore assets.'
[[ "$resolved_runtime" == "$requested_runtime" ]] || fail "Requested .NET runtime $requested_runtime but restore resolved $resolved_runtime."

program="$publish_dir/OpenCodeAccountManager"
[[ -f "$program" && -s "$program" && -x "$program" ]] || fail 'Publish did not produce an executable OpenCodeAccountManager apphost.'
pdb_file="$(find "$publish_dir" -type f -iname '*.pdb' -print -quit)"
[[ -z "$pdb_file" ]] || fail "Unexpected PDB in publish output: $pdb_file"

package_dir="$build_work/$archive_root"
mkdir -m 0755 -- "$package_dir"
install -m 0755 -- "$program" "$package_dir/OpenCodeAccountManager"

for packaged_name in start.sh install-desktop.sh uninstall-desktop.sh opencode-account-manager.desktop.in README.txt; do
    source_file="$root_dir/packaging/linux/$packaged_name"
    [[ -f "$source_file" && ! -L "$source_file" ]] || fail "Required package file is missing or linked: $source_file"
    if [[ "$packaged_name" == *.sh ]]; then
        [[ "$(head -n 1 -- "$source_file")" == '#!/usr/bin/env bash' ]] || fail "Shell script must use the Bash interpreter: $source_file"
        if grep -q $'\r' "$source_file"; then
            fail "Shell script must use LF line endings: $source_file"
        fi
        install -m 0755 -- "$source_file" "$package_dir/$packaged_name"
    else
        if grep -q $'\r' "$source_file"; then
            fail "Package text must use LF line endings: $source_file"
        fi
        install -m 0644 -- "$source_file" "$package_dir/$packaged_name"
    fi
done

icon_source="$root_dir/src/AccountManager.App/Assets/app.ico"
[[ -f "$icon_source" && ! -L "$icon_source" ]] || fail "Product ICO is missing or linked: $icon_source"
icon_size="$(wc -c < "$icon_source")"
icon_size="${icon_size//[[:space:]]/}"
read -r -a ico_header <<< "$(od -An -tu1 -N6 -- "$icon_source")"
[[ ${#ico_header[@]} -eq 6 && ${ico_header[0]} -eq 0 && ${ico_header[1]} -eq 0 && ${ico_header[2]} -eq 1 && ${ico_header[3]} -eq 0 ]] || fail 'Product app.ico has an invalid ICO header.'
frame_count=$((ico_header[4] + (ico_header[5] << 8)))
((frame_count > 0)) || fail 'Product app.ico contains no frames.'
best_offset=-1
best_length=0
for ((frame_index = 0; frame_index < frame_count; frame_index++)); do
    entry_offset=$((6 + frame_index * 16))
    read -r -a entry <<< "$(od -An -tu1 -j "$entry_offset" -N16 -- "$icon_source")"
    [[ ${#entry[@]} -eq 16 ]] || fail 'Product app.ico has a truncated frame directory.'
    frame_width=${entry[0]}
    frame_height=${entry[1]}
    ((frame_width == 0)) && frame_width=256
    ((frame_height == 0)) && frame_height=256
    frame_length=$((entry[8] + (entry[9] << 8) + (entry[10] << 16) + (entry[11] << 24)))
    frame_offset=$((entry[12] + (entry[13] << 8) + (entry[14] << 16) + (entry[15] << 24)))
    if ((frame_width == 256 && frame_height == 256 && frame_length >= 8 && frame_offset + frame_length <= icon_size)); then
        if cmp -s <(dd if="$icon_source" bs=1 skip="$frame_offset" count=8 status=none) <(printf '\211PNG\r\n\032\n'); then
            best_offset=$frame_offset
            best_length=$frame_length
        fi
    fi
done
((best_offset >= 0)) || fail 'Product app.ico has no 256x256 PNG-compressed frame.'
dd if="$icon_source" of="$package_dir/opencode-account-manager.png" bs=1 skip="$best_offset" count="$best_length" status=none
[[ "$(wc -c < "$package_dir/opencode-account-manager.png" | tr -d '[:space:]')" == "$best_length" ]] || fail 'Extracted product PNG has an unexpected length.'
cmp -s <(head -c 8 -- "$package_dir/opencode-account-manager.png") <(printf '\211PNG\r\n\032\n') || fail 'Extracted icon is not a PNG image.'
chmod 0644 -- "$package_dir/opencode-account-manager.png"

archive_name='OpenCodeAccountManager-linux-x64.tar.gz'
tar --create --gzip --file="$build_work/$archive_name" \
    --directory="$build_work" \
    --format=ustar --owner=0 --group=0 --numeric-owner --mtime='@0' \
    "$archive_root"

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
mapfile -t actual_members < <(tar --list --gzip --file="$build_work/$archive_name")
[[ ${#actual_members[@]} -eq ${#expected_members[@]} ]] || fail 'Portable archive contains an unexpected number of members.'
for expected_member in "${expected_members[@]}"; do
    member_count=0
    for actual_member in "${actual_members[@]}"; do
        [[ "$actual_member" == "$expected_member" ]] && member_count=$((member_count + 1))
    done
    [[ "$member_count" -eq 1 ]] || fail "Portable archive is missing or duplicates member: $expected_member"
done

elf_name='OpenCodeAccountManager-linux-x64'
elf_hash_name='OpenCodeAccountManager-linux-x64.sha256'
archive_hash_name='OpenCodeAccountManager-linux-x64.tar.gz.sha256'
install -m 0755 -- "$program" "$package_stage/$elf_name"
install -m 0644 -- "$build_work/$archive_name" "$package_stage/$archive_name"
(
    cd -- "$package_stage"
    sha256sum -- "$elf_name" > "$elf_hash_name"
    sha256sum -- "$archive_name" > "$archive_hash_name"
    sha256sum --check "$elf_hash_name"
    sha256sum --check "$archive_hash_name"
    chmod 0644 -- "$elf_hash_name" "$archive_hash_name"
)

expected_assets=(
    "$elf_name"
    "$elf_hash_name"
    "$archive_name"
    "$archive_hash_name"
)
mapfile -t actual_assets < <(find "$package_stage" -mindepth 1 -maxdepth 1 -printf '%f\t%y\n')
[[ ${#actual_assets[@]} -eq 4 ]] || fail 'Output staging area contains an unexpected number of members.'
for actual_asset_entry in "${actual_assets[@]}"; do
    asset_name="${actual_asset_entry%%$'\t'*}"
    asset_kind="${actual_asset_entry#*$'\t'}"
    [[ "$asset_kind" == 'f' ]] || fail "Unexpected non-file member in output staging area: $asset_name"
    asset_known=0
    for expected_asset in "${expected_assets[@]}"; do
        if [[ "$asset_name" == "$expected_asset" ]]; then
            asset_known=1
            break
        fi
    done
    [[ "$asset_known" -eq 1 ]] || fail "Unexpected output asset: $asset_name"
done
for expected_asset in "${expected_assets[@]}"; do
    [[ -f "$package_stage/$expected_asset" && ! -L "$package_stage/$expected_asset" ]] || fail "Missing regular output asset: $expected_asset"
done

if [[ -e "$output_dir" || -L "$output_dir" ]]; then
    fail "Refusing to overwrite output created during the build: $output_dir"
fi
mv -T -n -- "$package_stage" "$output_dir"
[[ ! -e "$package_stage" ]] || fail "Output appeared during the build; leaving it untouched: $output_dir"
package_stage=''

informational_version="$(sed -nE 's@.*<InformationalVersion>([^<]+)</InformationalVersion>.*@\1@p' "$project" | head -n 1)"
printf 'Version=%s\n' "$version"
printf 'InformationalVersionProperty=%s\n' "${informational_version:-<SDK default>}"
printf 'BuilderSDK=%s\n' "$builder_sdk"
printf 'RequestedRuntimeFrameworkVersion=%s\n' "$requested_runtime"
printf 'ResolvedRuntimeFrameworkVersion=%s\n' "$resolved_runtime"
printf 'OutputDirectory=%s\n' "$output_dir"
printf 'PackageRoot=%s\n' "$archive_root"
