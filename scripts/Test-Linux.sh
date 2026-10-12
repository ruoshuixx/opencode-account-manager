#!/usr/bin/env bash
set -euo pipefail
# readelf and ldd print localized labels under other locales, and the checks below match English text.
export LANG=C.UTF-8 LC_ALL=C.UTF-8

# Run from a fresh, dedicated, unprivileged test account. For the Ubuntu matrix, pass the same
# four-asset directory produced on Ubuntu 22.04 to every target release; without it this script
# creates a fresh local package output using Publish-Linux.sh.
usage() {
    cat <<'USAGE'
Usage: bash scripts/Test-Linux.sh [--assets-dir FOUR_ASSET_DIRECTORY \
  --candidate-build-sdk NUMERIC_VERSION --candidate-runtime NUMERIC_VERSION \
  --candidate-elf-sha256 LOWERCASE_SHA256 --candidate-archive-sha256 LOWERCASE_SHA256 \
  --candidate-version NUMERIC_VERSION]

Required environment:
  OCAM_LINUX_TEST_USER  Must exactly match `id -un`; the caller provisions a fresh non-root user.

The optional asset directory is validated in place and never rebuilt. Reuse the exact same
Ubuntu 22.04-produced directory on Ubuntu 22.04, 24.04 and 26.04, and supply its publisher SDK/runtime
record plus expected ELF/archive hashes from BUILD-INFO.json. The expected numeric version defaults
to the App project's three-part version; matrix operators may pass --candidate-version from that same
BUILD-INFO.json. Without --assets-dir, a fresh isolated output is created with Publish-Linux.sh.
All test content and process fixtures are synthetic.
USAGE
}

fail() {
    printf 'Test-Linux: %s\n' "$*" >&2
    exit 1
}

assets_input=''
candidate_build_sdk_input=''
candidate_runtime_input=''
candidate_elf_hash_input=''
candidate_archive_hash_input=''
candidate_version_input=''
while (($#)); do
    case "$1" in
        --assets-dir)
            (($# >= 2)) || fail '--assets-dir needs a directory argument.'
            [[ -z "$assets_input" ]] || fail '--assets-dir may be specified only once.'
            assets_input="$2"
            shift 2
            ;;
        --candidate-build-sdk)
            (($# >= 2)) || fail '--candidate-build-sdk needs a version argument.'
            [[ -z "$candidate_build_sdk_input" ]] || fail '--candidate-build-sdk may be specified only once.'
            candidate_build_sdk_input="$2"
            shift 2
            ;;
        --candidate-runtime)
            (($# >= 2)) || fail '--candidate-runtime needs a version argument.'
            [[ -z "$candidate_runtime_input" ]] || fail '--candidate-runtime may be specified only once.'
            candidate_runtime_input="$2"
            shift 2
            ;;
        --candidate-elf-sha256)
            (($# >= 2)) || fail '--candidate-elf-sha256 needs a hash argument.'
            [[ -z "$candidate_elf_hash_input" ]] || fail '--candidate-elf-sha256 may be specified only once.'
            candidate_elf_hash_input="$2"
            shift 2
            ;;
        --candidate-archive-sha256)
            (($# >= 2)) || fail '--candidate-archive-sha256 needs a hash argument.'
            [[ -z "$candidate_archive_hash_input" ]] || fail '--candidate-archive-sha256 may be specified only once.'
            candidate_archive_hash_input="$2"
            shift 2
            ;;
        --candidate-version)
            (($# >= 2)) || fail '--candidate-version needs a version argument.'
            [[ -z "$candidate_version_input" ]] || fail '--candidate-version may be specified only once.'
            candidate_version_input="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage >&2
            fail "Unknown argument: $1"
            ;;
    esac
done

[[ "$(uname -s)" == 'Linux' ]] || fail 'This entry is Linux-only; it will not substitute Windows tests for native scenarios.'
[[ "$(uname -m)" == 'x86_64' ]] || fail 'This test entry requires an x86_64 Linux process.'
test_uid="$(id -u)"
((test_uid != 0)) || fail 'Root cannot prove owner-only permission behavior; run as a fresh unprivileged dedicated test user.'
test_user="$(id -un)"
[[ -n "${OCAM_LINUX_TEST_USER:-}" && "$OCAM_LINUX_TEST_USER" == "$test_user" ]] || fail 'Set OCAM_LINUX_TEST_USER to the exact dedicated non-root account name; required scenarios are not skipped.'

script_path="$(readlink -f -- "${BASH_SOURCE[0]}")"
script_dir="$(cd -- "$(dirname -- "$script_path")" && pwd -P)"
root_dir="$(cd -- "$script_dir/.." && pwd -P)"
[[ -f "$root_dir/AccountManager.sln" && -f "$root_dir/src/AccountManager.App/AccountManager.App.csproj" ]] || fail 'Repository root is incomplete.'
contract_helper="$script_dir/Test-Linux-Contracts.py"
[[ -f "$contract_helper" && ! -L "$contract_helper" ]] || fail 'The focused Linux contract helper is missing or linked.'
owned_child_markers=()
track_owned_child_marker() {
    owned_child_markers+=("$1")
}

for command_name in bash dotnet env uname id mktemp mkdir chmod rm find sort sha256sum tar file readelf ldd desktop-file-validate curl timeout stat awk grep sed head tail wc tr cmp readlink dirname cp sleep python3; do
    command -v "$command_name" >/dev/null 2>&1 || fail "Required native test command not found: $command_name"
done

dotnet_command="$(readlink -f -- "$(command -v dotnet)")"
dotnet_root="$(dirname -- "$dotnet_command")"
curl_command="$(readlink -f -- "$(command -v curl)")"
curl_directory="$(dirname -- "$curl_command")"
safe_path="$curl_directory:$dotnet_root:/usr/bin:/bin:/usr/local/bin"

[[ -d /tmp && ! -L /tmp ]] || fail 'A normal non-linked /tmp directory is required for owned temporary test data.'
work="$(mktemp -d /tmp/ocam-test-linux.XXXXXXXX)"
chmod 0700 -- "$work"
cleanup() {
    local status=$?
    local cleanup_failed=0
    local harness_marker_count=0
    local reported_unconfirmed=0
    trap - EXIT
    if [[ -n "${logs:-}" && -f "$logs/native-suite.log" ]]; then
        while IFS= read -r line; do
            [[ "$line" == 'NATIVE_OWNED_CHILD_CLEANUP=UNCONFIRMED' ]] && reported_unconfirmed=1
        done < "$logs/native-suite.log"
    fi
    if [[ -n "${harness_root:-}" && -d "$harness_root" ]]; then
        while IFS= read -r marker; do
            owned_child_markers+=("$marker")
            [[ "$(basename -- "$marker")" == owned-child-*.marker ]] && harness_marker_count=$((harness_marker_count + 1))
        done < <(find "$harness_root" -type f \( -name 'owned-child-*.marker' -o -name 'owned-child-identity-unavailable' \) -print)
    fi
    for marker in "${owned_child_markers[@]}"; do
        [[ -f "$marker" ]] || continue
        if [[ "$(basename -- "$marker")" == 'owned-child-identity-unavailable' ]] || \
            ! env -i "PATH=$safe_path" python3 -I "$contract_helper" cleanup-process "$marker" >/dev/null 2>&1; then
            cleanup_failed=1
        fi
    done
    if ((reported_unconfirmed && harness_marker_count == 0)); then
        cleanup_failed=1
    fi
    if ((cleanup_failed)); then
        printf 'CLEANUP_BLOCKED owned-child-confirmation=failed scratch-retained=yes\n' >&2
        exit 1
    fi
    if [[ -n "${work:-}" && -d "$work" ]]; then
        find "$work" -type d -exec chmod u+rwx -- {} + >/dev/null 2>&1 || true
        rm -rf -- "$work"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

home="$work/home"
xdg_data="$home/.local/share"
xdg_config="$home/.config"
xdg_cache="$home/.cache"
xdg_runtime="$home/.runtime"
dotnet_home="$home/.dotnet-home"
test_tmp="$work/tmp"
bundle_extract="$work/bundle-extract"
logs="$work/logs"
for directory in "$home" "$xdg_data" "$xdg_config" "$xdg_cache" "$xdg_runtime" "$dotnet_home" "$test_tmp" "$bundle_extract" "$logs"; do
    mkdir -p -- "$directory"
    chmod 0700 -- "$directory"
done
mkdir -m 0700 -- "$work/test-results"
mkdir -m 0700 -- "$work/test-results/core" "$work/test-results/app"
[[ "$(stat -c '%a' "$work/test-results")" == '700' &&
   "$(stat -c '%a' "$work/test-results/core")" == '700' &&
   "$(stat -c '%a' "$work/test-results/app")" == '700' ]] || fail 'Owned test-results directories must have exact mode 0700.'
: > "$logs/updater-scenarios.log"

common_environment=(
    "HOME=$home"
    "XDG_DATA_HOME=$xdg_data"
    "XDG_CONFIG_HOME=$xdg_config"
    "XDG_CACHE_HOME=$xdg_cache"
    "XDG_RUNTIME_DIR=$xdg_runtime"
    "TMPDIR=$test_tmp"
    "DOTNET_CLI_HOME=$dotnet_home"
    "DOTNET_ROOT=$dotnet_root"
    'DOTNET_CLI_TELEMETRY_OPTOUT=1'
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
    'DOTNET_NOLOGO=1'
    "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$bundle_extract"
    "PATH=$safe_path"
    'LANG=C.UTF-8'
    'LC_ALL=C.UTF-8'
)

run_clean() {
    env -i "${common_environment[@]}" "$@"
}

run_logged() {
    local label="$1"
    shift
    if "$@" >"$logs/$label.log" 2>&1; then
        printf 'CHECK name=%s exit=0\n' "$label"
    else
        local status=$?
        printf 'CHECK name=%s exit=%s status=FAIL\n' "$label" "$status" >&2
        tail -n 40 "$logs/$label.log" >&2 || true
        exit "$status"
    fi
}

os_id="$(sed -nE 's/^ID="?([^" ]+)"?$/\1/p' /etc/os-release | head -n 1)"
os_version="$(sed -nE 's/^VERSION_ID="?([^" ]+)"?$/\1/p' /etc/os-release | head -n 1)"
sdk_version="$(run_clean "$dotnet_command" --version)"
[[ "$sdk_version" =~ ^8\. ]] || fail "The selected .NET SDK must be 8.x (found $sdk_version)."
project_version="$(sed -nE 's@.*<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>.*@\1@p' "$root_dir/src/AccountManager.App/AccountManager.App.csproj" | head -n 1)"
[[ "$project_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Could not read the App numeric three-part version.'
if [[ -z "$candidate_version_input" ]]; then
    candidate_version_input="$project_version"
fi
[[ "$candidate_version_input" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail '--candidate-version must have three numeric components.'
printf 'OS_ID=%s\nOS_VERSION_ID=%s\nKernel=%s\nArchitecture=%s\nTestUser=%s\nTestUid=%s\nDotnetSdk=%s\n' \
    "${os_id:-unknown}" "${os_version:-unknown}" "$(uname -sr)" "$(uname -m)" "$test_user" "$test_uid" "$sdk_version"
printf 'ExpectedCandidateVersion=%s\n' "$candidate_version_input"
printf 'DotnetHostRuntimes follows (self-contained bundle runtime is reported separately from harness bundle metadata):\n'
run_clean "$dotnet_command" --list-runtimes | sed -E 's@^(.+ )\[(.*)\]$@\1[runtime-path-redacted]@'

source_shells=(
    "$script_dir/Publish-Linux.sh"
    "$script_dir/Test-Linux.sh"
    "$script_dir/Test-Linux-Packaging.sh"
    "$root_dir/packaging/linux/start.sh"
    "$root_dir/packaging/linux/install-desktop.sh"
    "$root_dir/packaging/linux/uninstall-desktop.sh"
)
for index in "${!source_shells[@]}"; do
    run_logged "bash-syntax-$index" bash -n "${source_shells[$index]}"
done
run_logged linux-contract-helper-regressions run_clean python3 "$contract_helper" self-test

if [[ -n "$assets_input" ]]; then
    [[ -d "$assets_input" && ! -L "$assets_input" ]] || fail 'The supplied four-asset directory must exist and must not be a link.'
    [[ "$candidate_build_sdk_input" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Prebuilt assets require --candidate-build-sdk with the publisher-reported numeric SDK version.'
    [[ "$candidate_runtime_input" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Prebuilt assets require --candidate-runtime with the publisher-reported numeric runtime version.'
    [[ "$candidate_runtime_input" == '8.0.31' ]] || fail 'The external candidate runtime record does not match the pinned 8.0.31 package contract.'
    [[ "$candidate_elf_hash_input" =~ ^[0-9a-f]{64}$ ]] || fail 'Prebuilt assets require --candidate-elf-sha256 from BUILD-INFO.json.'
    [[ "$candidate_archive_hash_input" =~ ^[0-9a-f]{64}$ ]] || fail 'Prebuilt assets require --candidate-archive-sha256 from BUILD-INFO.json.'
    assets_dir="$(cd -- "$assets_input" && pwd -P)"
    asset_source='provided-four-asset-directory'
else
    [[ -z "$candidate_build_sdk_input" && -z "$candidate_runtime_input" && -z "$candidate_elf_hash_input" && -z "$candidate_archive_hash_input" ]] || fail 'Publisher metadata overrides are only valid with --assets-dir.'
    [[ "$candidate_version_input" == "$project_version" ]] || fail 'Without --assets-dir, --candidate-version must match the App project version used for the isolated publish.'
    assets_dir="$work/published-assets"
    run_logged baseline-publish run_clean bash "$script_dir/Publish-Linux.sh" "$project_version" "$assets_dir"
    candidate_build_sdk_input="$(sed -nE 's/^BuilderSDK=([0-9]+\.[0-9]+\.[0-9]+)$/\1/p' "$logs/baseline-publish.log" | tail -n 1)"
    candidate_runtime_input="$(sed -nE 's/^ResolvedRuntimeFrameworkVersion=([0-9]+\.[0-9]+\.[0-9]+)$/\1/p' "$logs/baseline-publish.log" | tail -n 1)"
    [[ "$candidate_build_sdk_input" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Could not capture the baseline publisher SDK record.'
    [[ "$candidate_runtime_input" == '8.0.31' ]] || fail 'The resolved baseline candidate runtime differs from the 8.0.31 pin.'
    asset_source='fresh-isolated-local-publish'
fi

expected_assets=(
    'OpenCodeAccountManager-linux-x64'
    'OpenCodeAccountManager-linux-x64.sha256'
    'OpenCodeAccountManager-linux-x64.tar.gz'
    'OpenCodeAccountManager-linux-x64.tar.gz.sha256'
)
mapfile -t actual_assets < <(find "$assets_dir" -mindepth 1 -maxdepth 1 -printf '%f\t%y\n' | sort)
[[ ${#actual_assets[@]} -eq 4 ]] || fail 'The baseline input must contain exactly four immediate assets and no unpacked directory.'
for actual_entry in "${actual_assets[@]}"; do
    asset_name="${actual_entry%%$'\t'*}"
    asset_type="${actual_entry#*$'\t'}"
    [[ "$asset_type" == 'f' ]] || fail "Unexpected non-file in four-asset input: $asset_name"
    known=0
    for expected_asset in "${expected_assets[@]}"; do
        [[ "$asset_name" == "$expected_asset" ]] && known=1
    done
    ((known == 1)) || fail "Unexpected four-asset input member: $asset_name"
done
for expected_asset in "${expected_assets[@]}"; do
    [[ -f "$assets_dir/$expected_asset" && ! -L "$assets_dir/$expected_asset" ]] || fail "Missing regular asset: $expected_asset"
done

elf_name='OpenCodeAccountManager-linux-x64'
archive_name='OpenCodeAccountManager-linux-x64.tar.gz'
elf_checksum='OpenCodeAccountManager-linux-x64.sha256'
archive_checksum='OpenCodeAccountManager-linux-x64.tar.gz.sha256'
for checksum_name in "$elf_checksum" "$archive_checksum"; do
    [[ "$(wc -l < "$assets_dir/$checksum_name" | tr -d '[:space:]')" == '1' ]] || fail "Checksum file must have exactly one LF-terminated line: $checksum_name"
done
elf_checksum_line="$(<"$assets_dir/$elf_checksum")"
archive_checksum_line="$(<"$assets_dir/$archive_checksum")"
elf_hash="${elf_checksum_line:0:64}"
archive_hash="${archive_checksum_line:0:64}"
[[ "$elf_hash" =~ ^[0-9a-f]{64}$ && "${elf_checksum_line:64}" == "  $elf_name" ]] || fail 'ELF checksum is not lowercase SHA256SUM format with two spaces and the fixed filename.'
[[ "$archive_hash" =~ ^[0-9a-f]{64}$ && "${archive_checksum_line:64}" == "  $archive_name" ]] || fail 'Archive checksum is not lowercase SHA256SUM format with two spaces and the fixed filename.'
if [[ -n "$assets_input" ]]; then
    [[ "$elf_hash" == "$candidate_elf_hash_input" ]] || fail 'The standalone ELF hash differs from BUILD-INFO.json; refusing a substitute matrix payload.'
    [[ "$archive_hash" == "$candidate_archive_hash_input" ]] || fail 'The archive hash differs from BUILD-INFO.json; refusing a substitute matrix payload.'
fi
run_logged baseline-sha256sum-check bash -c 'cd -- "$1" && sha256sum --check --strict --status "$2" && sha256sum --check --strict --status "$3"' _ "$assets_dir" "$elf_checksum" "$archive_checksum"
source_elf_mode_before="$(stat -c '%a' "$assets_dir/$elf_name")"
standalone_work="$work/standalone-candidate"
mkdir -m 0700 -- "$standalone_work"
program="$standalone_work/OpenCodeAccountManager"
cp -- "$assets_dir/$elf_name" "$program"
chmod 0755 -- "$program"
[[ "$(stat -c '%a' "$assets_dir/$elf_name")" == "$source_elf_mode_before" ]] || fail 'Preparing the executable working copy mutated the external candidate mode.'
run_logged standalone-candidate-binding run_clean python3 "$contract_helper" verify-sha256 "$program" "$elf_hash"
run_logged external-candidate-unchanged run_clean python3 "$contract_helper" verify-sha256 "$assets_dir/$elf_name" "$elf_hash"
file_description="$(file -b -- "$program")"
[[ "$file_description" == *'ELF 64-bit'* && "$file_description" == *'x86-64'* ]] || fail 'The standalone asset is not an ELF64 x86-64 executable.'
readelf -h -- "$program" >"$logs/baseline-readelf.log" 2>&1 || fail 'readelf could not parse the standalone asset.'
grep -Fq 'Class:                             ELF64' "$logs/baseline-readelf.log" || fail 'readelf did not report ELF64.'
grep -Fq 'little endian' "$logs/baseline-readelf.log" || fail 'readelf did not report little-endian encoding.'
grep -Fq 'Advanced Micro Devices X86-64' "$logs/baseline-readelf.log" || fail 'readelf did not report x86-64 machine type.'
grep -Eq 'Type: +DYN|Type: +EXEC' "$logs/baseline-readelf.log" || fail 'readelf did not report ET_DYN or ET_EXEC.'
ldd -- "$program" >"$logs/baseline-ldd.log" 2>&1 || fail 'ldd could not inspect baseline runtime dependencies.'
if grep -Fq 'not found' "$logs/baseline-ldd.log"; then fail 'The baseline ELF has an unresolved runtime dependency.'; fi

archive_root='opencode-account-manager-linux-x64'
run_logged baseline-archive-contract run_clean python3 "$contract_helper" validate-archive "$assets_dir/$archive_name"
mapfile -t archive_members < <(tar --list --gzip --file="$assets_dir/$archive_name")
mapfile -t archive_verbose < <(tar --list --verbose --gzip --file="$assets_dir/$archive_name")
[[ ${#archive_members[@]} -eq 8 && ${#archive_verbose[@]} -eq 8 ]] || fail 'The portable archive must contain exactly the eight whitelisted entries.'
for verbose_entry in "${archive_verbose[@]}"; do
    mode="$(awk '{print $1}' <<<"$verbose_entry")"
    member="$(awk '{print $6}' <<<"$verbose_entry")"
    member="${member#./}"
    case "$member" in
        "$archive_root/") expected_mode='drwxr-xr-x' ;;
        "$archive_root/OpenCodeAccountManager"|"$archive_root/start.sh"|"$archive_root/install-desktop.sh"|"$archive_root/uninstall-desktop.sh") expected_mode='-rwxr-xr-x' ;;
        "$archive_root/opencode-account-manager.desktop.in"|"$archive_root/opencode-account-manager.png"|"$archive_root/README.txt") expected_mode='-rw-r--r--' ;;
        *) fail "Unexpected archive metadata path: $member" ;;
    esac
    [[ "$mode" == "$expected_mode" ]] || fail "Archive member $member has mode $mode instead of $expected_mode"
done

extraction_parent="$work/extracted with space 中文"
mkdir -m 0700 -- "$extraction_parent"
tar --extract --gzip --file="$assets_dir/$archive_name" --directory="$extraction_parent" --no-same-owner
extracted="$extraction_parent/$archive_root"
[[ "$(stat -c '%a' "$extracted")" == '755' ]] || fail 'Extracted archive root mode is not 0755.'
for executable in OpenCodeAccountManager start.sh install-desktop.sh uninstall-desktop.sh; do
    [[ "$(stat -c '%a' "$extracted/$executable")" == '755' ]] || fail "Extracted executable mode is not 0755: $executable"
done
for data_file in opencode-account-manager.desktop.in opencode-account-manager.png README.txt; do
    [[ "$(stat -c '%a' "$extracted/$data_file")" == '644' ]] || fail "Extracted data-file mode is not 0644: $data_file"
done
run_logged extracted-elf-candidate-binding run_clean python3 "$contract_helper" verify-sha256 \
    "$extracted/OpenCodeAccountManager" "$elf_hash"

baseline_probe_out="$work/baseline-probe.stdout"
baseline_probe_err="$work/baseline-probe.stderr"
if run_clean timeout --signal=TERM --kill-after=5s 20s "$extracted/OpenCodeAccountManager" --update-probe >"$baseline_probe_out" 2>"$baseline_probe_err"; then
    printf 'CHECK name=baseline-extracted-probe exit=0\n'
else
    status=$?
    printf 'CHECK name=baseline-extracted-probe exit=%s status=FAIL\n' "$status" >&2
    fail 'The extracted candidate did not answer its production early update probe.'
fi
[[ ! -s "$baseline_probe_err" ]] || fail 'The isolated production probe wrote unexpected stderr.'
run_logged baseline-probe-contract run_clean python3 "$contract_helper" verify-probe "$baseline_probe_out" "$candidate_version_input"
baseline_version="$candidate_version_input"
if [[ "$asset_source" == 'provided-four-asset-directory' ]]; then
    candidate_runtime_evidence='publisher-reported metadata supplied separately; runtime is not embedded as a verifiable sidecar in the fixed four-asset payload'
else
    candidate_runtime_evidence='resolved from the isolated publisher restore assets'
fi
printf 'AssetSource=%s\nCandidateVersion=%s\nCandidateBuildSdk=%s\nCandidateRuntimeFrameworkVersion=%s\nCandidateRuntimeEvidence=%s\nCandidateElfSha256=%s\nCandidateArchiveSha256=%s\n' \
    "$asset_source" "$baseline_version" "$candidate_build_sdk_input" "$candidate_runtime_input" "$candidate_runtime_evidence" "$elf_hash" "$archive_hash"

native_extract="$work/native-library-extraction"
mkdir -m 0700 -- "$native_extract"
if run_clean env "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$native_extract" timeout --signal=TERM --kill-after=5s 20s "$extracted/OpenCodeAccountManager" --update-probe >"$work/extract-probe.stdout" 2>"$work/extract-probe.stderr"; then
    printf 'CHECK name=baseline-bundle-native-extraction exit=0\n'
else
    status=$?
    printf 'CHECK name=baseline-bundle-native-extraction exit=%s status=FAIL\n' "$status" >&2
    fail 'The candidate did not complete its controlled single-file bundle extraction/probe.'
fi
[[ ! -s "$work/extract-probe.stderr" ]] || fail 'The extraction probe wrote unexpected stderr.'
mapfile -t extracted_native_libraries < <(find "$native_extract" -type f -name '*.so*' -print | sort)
[[ ${#extracted_native_libraries[@]} -gt 0 ]] || fail 'The self-contained bundle did not expose any native shared objects for dependency inspection.'
for native_library in "${extracted_native_libraries[@]}"; do
    file -b -- "$native_library" | grep -q 'ELF 64-bit' || fail 'A bundle-extracted native object was not ELF64.'
    ldd -- "$native_library" >"$logs/native-ldd.log" 2>&1 || fail 'ldd could not inspect a bundle-extracted native object.'
    if grep -Fq 'not found' "$logs/native-ldd.log"; then fail 'A bundle-extracted native object has an unresolved runtime dependency.'; fi
done
printf 'ExtractedNativeLibraries=%s\n' "${#extracted_native_libraries[@]}"

run_logged native-packaging-regression run_clean bash "$script_dir/Test-Linux-Packaging.sh"

run_logged full-core-tests run_clean "$dotnet_command" test "$root_dir/tests/AccountManager.Core.Tests/AccountManager.Core.Tests.csproj" \
    --configuration Release --nologo --verbosity minimal \
    --artifacts-path "$work/test-artifacts-core" --results-directory "$work/test-results/core" \
    --logger 'trx;LogFileName=core.trx'
run_logged verify-core-linux-facts python3 - "$root_dir/tests/AccountManager.Core.Tests" "$work/test-results/core/core.trx" <<'PY'
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

source_root = pathlib.Path(sys.argv[1])
trx_path = pathlib.Path(sys.argv[2])
required = set()
method_pattern = re.compile(
    r"\[LinuxFact(?:Attribute)?\]\s*(?:\[[^\]]+\]\s*)*"
    r"(?:public|internal|private)\s+(?:static\s+)?(?:async\s+)?"
    r"[\w<>,?.\[\]]+\s+(\w+)\s*\(", re.MULTILINE)
for source in source_root.rglob("*.cs"):
    required.update(method_pattern.findall(source.read_text(encoding="utf-8")))
if not required:
    raise SystemExit("No Core [LinuxFact] methods were discovered; refusing a vacuous native-suite pass.")
root = ET.parse(trx_path).getroot()
results = [node for node in root.iter() if node.tag.endswith("UnitTestResult")]
unrun = []
for method in sorted(required):
    matches = [node for node in results if re.search(rf"(?:^|\.){re.escape(method)}(?:$|\s|\()", node.attrib.get("testName", ""))]
    if not matches or any(node.attrib.get("outcome") != "Passed" for node in matches):
        unrun.append(method)
if unrun:
    raise SystemExit("Core Linux-native facts were missing, skipped, or failed: " + ", ".join(unrun))
passed = sum(1 for node in results if node.attrib.get("outcome") == "Passed")
skipped = sum(1 for node in results if node.attrib.get("outcome") in {"NotExecuted", "Skipped"})
failed = sum(1 for node in results if node.attrib.get("outcome") == "Failed")
print(f"TEST_RESULTS assembly=Core total={len(results)} passed={passed} skipped={skipped} failed={failed}")
print(f"LINUX_FACTS assembly=Core methods={len(required)} skipped=0 failed=0")
PY
grep -E '^(TEST_RESULTS|LINUX_FACTS) ' "$logs/verify-core-linux-facts.log"

run_logged full-app-tests run_clean "$dotnet_command" test "$root_dir/tests/AccountManager.App.Tests/AccountManager.App.Tests.csproj" \
    --configuration Release --nologo --verbosity minimal \
    --artifacts-path "$work/test-artifacts-app" --results-directory "$work/test-results/app" \
    --logger 'trx;LogFileName=app.trx'
run_logged verify-app-linux-facts python3 - "$root_dir/tests/AccountManager.App.Tests" "$work/test-results/app/app.trx" <<'PY'
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

source_root = pathlib.Path(sys.argv[1])
trx_path = pathlib.Path(sys.argv[2])
required = set()
method_pattern = re.compile(
    r"\[LinuxFact(?:Attribute)?\]\s*(?:\[[^\]]+\]\s*)*"
    r"(?:public|internal|private)\s+(?:static\s+)?(?:async\s+)?"
    r"[\w<>,?.\[\]]+\s+(\w+)\s*\(", re.MULTILINE)
for source in source_root.rglob("*.cs"):
    required.update(method_pattern.findall(source.read_text(encoding="utf-8")))
if not required:
    raise SystemExit("No App [LinuxFact] methods were discovered; refusing a vacuous native-suite pass.")
root = ET.parse(trx_path).getroot()
results = [node for node in root.iter() if node.tag.endswith("UnitTestResult")]
unrun = []
for method in sorted(required):
    matches = [node for node in results if re.search(rf"(?:^|\.){re.escape(method)}(?:$|\s|\()", node.attrib.get("testName", ""))]
    if not matches or any(node.attrib.get("outcome") != "Passed" for node in matches):
        unrun.append(method)
if unrun:
    raise SystemExit("App Linux-native facts were missing, skipped, or failed: " + ", ".join(unrun))
passed = sum(1 for node in results if node.attrib.get("outcome") == "Passed")
skipped = sum(1 for node in results if node.attrib.get("outcome") in {"NotExecuted", "Skipped"})
failed = sum(1 for node in results if node.attrib.get("outcome") == "Failed")
print(f"TEST_RESULTS assembly=App total={len(results)} passed={passed} skipped={skipped} failed={failed}")
print(f"LINUX_FACTS assembly=App methods={len(required)} skipped=0 failed=0")
PY
grep -E '^(TEST_RESULTS|LINUX_FACTS) ' "$logs/verify-app-linux-facts.log"

version_one='91.4.1'
version_two='91.4.2'
publish_harness_bundle() {
    local version="$1"
    local output="$2"
    local artifacts="$3"
    run_logged "harness-publish-$version" run_clean "$dotnet_command" publish \
        "$root_dir/tests/AccountManager.Linux.IntegrationHarness/AccountManager.Linux.IntegrationHarness.csproj" \
        --configuration Release --runtime linux-x64 --self-contained true \
        --output "$output" --artifacts-path "$artifacts" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:UseAppHost=true \
        -p:DebugType=none -p:DebugSymbols=false \
        -p:RuntimeFrameworkVersion=8.0.31 \
        "-p:Version=$version" "-p:AssemblyVersion=$version" "-p:FileVersion=$version" "-p:InformationalVersion=$version"
}

publish_one="$work/harness-v1-publish"
publish_two="$work/harness-v2-publish"
mkdir -m 0700 -- "$publish_one" "$publish_two"
publish_harness_bundle "$version_one" "$publish_one" "$work/harness-v1-artifacts"
publish_harness_bundle "$version_two" "$publish_two" "$work/harness-v2-artifacts"

harness_one_original="$publish_one/AccountManager.Linux.IntegrationHarness"
harness_two_original="$publish_two/AccountManager.Linux.IntegrationHarness"
[[ -f "$harness_one_original" && -f "$harness_two_original" ]] || fail 'A single-file harness apphost was not produced for both numeric versions.'

install_one="$work/harness-v1-install"
install_two="$work/harness-v2-install"
mkdir -m 0700 -- "$install_one" "$install_two"
cp -- "$harness_one_original" "$install_one/OpenCodeAccountManager"
cp -- "$harness_two_original" "$install_two/OpenCodeAccountManager"
chmod 0755 -- "$install_one/OpenCodeAccountManager" "$install_two/OpenCodeAccountManager"
harness_one="$install_one/OpenCodeAccountManager"
harness_two="$install_two/OpenCodeAccountManager"
harness_one_hash="$(sha256sum -- "$harness_one" | awk '{print $1}')"
harness_two_hash="$(sha256sum -- "$harness_two" | awk '{print $1}')"
[[ "$harness_one_hash" != "$harness_two_hash" ]] || fail 'The two numeric-version harness bundles have identical content hashes.'

verify_harness_bundle() {
    local version="$1"
    local executable="$2"
    local extract_base="$3"
    local info_file="$4"
    local probe_file="$5"
    local probe_err="$6"
    mkdir -m 0700 -- "$extract_base"
    run_clean env "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$extract_base" timeout --signal=TERM --kill-after=5s 20s "$executable" --harness-info >"$info_file" 2>"$probe_err"
    [[ ! -s "$probe_err" ]] || fail "Harness $version info command wrote unexpected stderr."
    run_clean python3 "$contract_helper" verify-info "$info_file" "$version"
    run_clean env "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$extract_base" timeout --signal=TERM --kill-after=5s 20s "$executable" --update-probe >"$probe_file" 2>"$probe_err"
    [[ ! -s "$probe_err" ]] || fail "Harness $version update probe wrote unexpected stderr."
    run_clean python3 "$contract_helper" verify-probe "$probe_file" "$version"
}

verify_harness_bundle "$version_one" "$harness_one" "$work/harness-v1-extract" "$work/harness-v1-info.json" "$work/harness-v1-probe.json" "$work/harness-v1.stderr"
verify_harness_bundle "$version_two" "$harness_two" "$work/harness-v2-extract" "$work/harness-v2-info.json" "$work/harness-v2-probe.json" "$work/harness-v2.stderr"
printf 'HarnessVersion1=%s HarnessVersion1Sha256=%s\nHarnessVersion2=%s HarnessVersion2Sha256=%s\n' "$version_one" "$harness_one_hash" "$version_two" "$harness_two_hash"

harness_root="$work/native-harness-root"
mkdir -m 0700 -- "$harness_root"
if env -i "${common_environment[@]}" "OCAM_LINUX_HARNESS_ROOT=$harness_root" timeout --signal=TERM --kill-after=5s 180s \
    "$harness_one" --native-suite "$harness_root" "$harness_two" >"$logs/native-suite.log" 2>&1; then
    printf 'CHECK name=native-process-scenarios exit=0\n'
else
    status=$?
    printf 'CHECK name=native-process-scenarios exit=%s status=FAIL\n' "$status" >&2
    python3 "$contract_helper" print-failures "$logs/native-suite.log" >&2 || true
    fail 'The native scenario harness failed; UNRUN/failed cases are not accepted as native success.'
fi
run_logged validate-native-scenario-identities run_clean python3 "$contract_helper" validate-native "$logs/native-suite.log"
cat "$logs/validate-native-scenario-identities.log"

run_harness_probe() {
    local executable="$1"
    local expected="$2"
    local output="$3"
    local error="$4"
    run_clean timeout --signal=TERM --kill-after=5s 20s "$executable" --update-probe >"$output" 2>"$error"
    [[ ! -s "$error" ]] || fail 'A post-update candidate probe wrote unexpected stderr.'
    run_clean python3 "$contract_helper" verify-probe "$output" "$expected"
}

success_install="$work/update success 中文 space"
success_markers="$work/update-success-markers"
mkdir -m 0700 -- "$success_install" "$success_markers"
cp -- "$harness_one" "$success_install/OpenCodeAccountManager"
chmod 0755 -- "$success_install/OpenCodeAccountManager"
success_child_complete="$success_markers/child-complete"
success_child_start="$success_markers/child-start"
track_owned_child_marker "$success_child_start"
run_logged updater-successful-real-bundle-handover env -i "${common_environment[@]}" \
    "OCAM_LINUX_HARNESS_CHILD_COMPLETE_FILE=$success_child_complete" \
    "OCAM_LINUX_HARNESS_CHILD_START_FILE=$success_child_start" \
    timeout --signal=TERM --kill-after=5s 45s "$success_install/OpenCodeAccountManager" --update-success "$harness_two" "$version_two" "$success_markers"
[[ -f "$success_markers/parent-handover-returned" ]] || fail 'The real update parent did not return after authenticated child readiness.'
wait_for_file() {
    local path="$1"
    local attempts="$2"
    for ((attempt = 0; attempt < attempts; attempt++)); do
        [[ -f "$path" ]] && return 0
        sleep 0.1
    done
    return 1
}
wait_for_file "$success_child_complete" 100 || fail 'The actual update child did not reach mechanism startup cleanup after parent exit.'
run_logged confirm-success-child-exit run_clean python3 "$contract_helper" wait-process-exited "$success_child_start" --timeout-seconds 5
[[ "$(sha256sum -- "$success_install/OpenCodeAccountManager" | awk '{print $1}')" == "$harness_two_hash" ]] || fail 'Successful production updater did not install the exact second bundle bytes.'
shopt -s nullglob
success_backups=("$success_install"/OpenCodeAccountManager.old-*)
[[ ${#success_backups[@]} -eq 0 ]] || fail 'Production mechanism cleanup left an attributable old backup after simulated post-startup completion.'
run_harness_probe "$success_install/OpenCodeAccountManager" "$version_two" "$work/update-success-probe.json" "$work/update-success-probe.stderr"
printf '%s\n' 'UPDATE_SCENARIO name=successful-real-bundle-handover status=PASS parent_wait=production-ipc child_cleanup=production-mechanism-only ui_opened=no' | tee -a "$logs/updater-scenarios.log"

readiness_install="$work/readiness-timeout-install"
mkdir -m 0700 -- "$readiness_install"
cp -- "$harness_one" "$readiness_install/OpenCodeAccountManager"
chmod 0755 -- "$readiness_install/OpenCodeAccountManager"
readiness_result="$work/readiness-timeout.result"
readiness_child_result="$work/readiness-timeout.child-result"
track_owned_child_marker "$readiness_child_result"
run_logged updater-managed-entry-readiness-timeout env -i "${common_environment[@]}" \
    "OCAM_LINUX_HARNESS_CHILD_BEHAVIOR=hold-before-entry" \
    "OCAM_LINUX_HARNESS_CHILD_RESULT_FILE=$readiness_child_result" \
    timeout --signal=TERM --kill-after=5s 40s "$readiness_install/OpenCodeAccountManager" --update-readiness-timeout "$harness_two" "$version_two" "$readiness_result"
run_logged verify-readiness-timeout-result run_clean python3 "$contract_helper" verify-readiness-result "$readiness_result"
[[ "$(<"$readiness_child_result")" =~ ^held-before-entry:[0-9]+:[0-9]+:[0-9]+$ ]] || fail 'The harness-owned readiness control did not hold a managed child before production startup.'
run_logged confirm-readiness-timeout-child-exit run_clean python3 "$contract_helper" wait-process-exited "$readiness_child_result" --timeout-seconds 5
[[ "$(sha256sum -- "$readiness_install/OpenCodeAccountManager" | awk '{print $1}')" == "$harness_one_hash" ]] || fail 'Readiness-timeout compensation changed the installed bundle.'
readiness_leftovers=("$readiness_install"/OpenCodeAccountManager.old-* "$readiness_install"/.OpenCodeAccountManager.new-*)
[[ ${#readiness_leftovers[@]} -eq 0 ]] || fail 'Readiness-timeout compensation left staging or backup files.'
printf '%s\n' 'UPDATE_SCENARIO name=production-managed-entry-timeout status=PASS orchestration=production child_control=harness-pre-entry-hold ui_opened=no' | tee -a "$logs/updater-scenarios.log"

parent_timeout_install="$work/parent-exit-timeout-install"
parent_timeout_markers="$work/parent-exit-timeout-markers"
mkdir -m 0700 -- "$parent_timeout_install" "$parent_timeout_markers"
cp -- "$harness_one" "$parent_timeout_install/OpenCodeAccountManager"
chmod 0755 -- "$parent_timeout_install/OpenCodeAccountManager"
parent_timeout_child_start="$parent_timeout_markers/child-start"
parent_timeout_child_result="$parent_timeout_markers/child-result"
parent_timeout_hold_start="$parent_timeout_markers/parent-hold-start"
parent_timeout_hold_end="$parent_timeout_markers/parent-hold-end"
track_owned_child_marker "$parent_timeout_child_start"
track_owned_child_marker "$parent_timeout_child_result"
run_logged updater-parent-exit-timeout env -i "${common_environment[@]}" \
    "OCAM_LINUX_HARNESS_CHILD_START_FILE=$parent_timeout_child_start" \
    "OCAM_LINUX_HARNESS_CHILD_RESULT_FILE=$parent_timeout_child_result" \
    timeout --signal=TERM --kill-after=5s 70s "$parent_timeout_install/OpenCodeAccountManager" --update-parent-exit-timeout "$harness_two" "$version_two" "$parent_timeout_markers"
wait_for_file "$parent_timeout_child_result" 100 || fail 'The production child did not report its parent-exit timeout.'
[[ "$(<"$parent_timeout_markers/parent-handover-returned")" == 'parent-kept-alive-past-production-exit-budget' ]] || fail 'The parent-exit timeout fixture did not hold the exact updater parent past the production bound.'
run_logged verify-production-parent-exit-bound run_clean python3 "$contract_helper" verify-parent-timeout \
    "$parent_timeout_child_start" "$parent_timeout_child_result" "$parent_timeout_hold_start" "$parent_timeout_hold_end"
run_logged confirm-parent-timeout-child-exit run_clean python3 "$contract_helper" wait-process-exited "$parent_timeout_child_result" --timeout-seconds 5
[[ "$(sha256sum -- "$parent_timeout_install/OpenCodeAccountManager" | awk '{print $1}')" == "$harness_two_hash" ]] || fail 'Parent-exit timeout unexpectedly changed the installed candidate bytes.'
parent_timeout_backups=("$parent_timeout_install"/OpenCodeAccountManager.old-*)
[[ ${#parent_timeout_backups[@]} -eq 1 ]] || fail 'Parent-exit timeout did not retain exactly the attributable recovery backup.'
[[ "$(sha256sum -- "${parent_timeout_backups[0]}" | awk '{print $1}')" == "$harness_one_hash" ]] || fail 'Parent-exit recovery backup bytes do not match the original bundle.'
printf '%s\n' 'UPDATE_SCENARIO name=production-parent-exit-timeout status=PASS old_backup=preserved-for-manual-recovery ui_opened=no' | tee -a "$logs/updater-scenarios.log"

readonly_install="$work/readonly-install"
mkdir -m 0700 -- "$readonly_install"
cp -- "$harness_one" "$readonly_install/OpenCodeAccountManager"
chmod 0755 -- "$readonly_install/OpenCodeAccountManager"
chmod 0555 -- "$readonly_install"
readonly_result="$work/readonly-install.result"
if env -i "${common_environment[@]}" timeout --signal=TERM --kill-after=5s 15s \
    "$readonly_install/OpenCodeAccountManager" --update-readonly-refusal "$harness_two" "$version_two" "$readonly_result" \
    >"$logs/updater-readonly-refusal.log" 2>&1; then
    printf 'CHECK name=updater-readonly-install-refusal exit=0\n'
else
    status=$?
    chmod 0700 -- "$readonly_install"
    printf 'CHECK name=updater-readonly-install-refusal exit=%s status=FAIL\n' "$status" >&2
    fail 'The production updater read-only refusal scenario failed.'
fi
chmod 0700 -- "$readonly_install"
[[ "$(<"$readonly_result")" == 'read-only-install-refused' ]] || fail 'Production updater did not refuse the read-only install directory.'
[[ "$(sha256sum -- "$readonly_install/OpenCodeAccountManager" | awk '{print $1}')" == "$harness_one_hash" ]] || fail 'Read-only refusal changed the installed bundle.'
printf '%s\n' 'UPDATE_SCENARIO name=readonly-install-refusal status=PASS' | tee -a "$logs/updater-scenarios.log"

run_logged validate-required-scenario-identities run_clean python3 "$contract_helper" validate-summary \
    "$logs/native-suite.log" "$logs/updater-scenarios.log"
cat "$logs/validate-required-scenario-identities.log"

printf 'TEST_LINUX_RESULT=PASS os=%s version=%s sdk=%s candidate=%s candidate_elf_sha256=%s candidate_archive_sha256=%s\n' \
    "${os_id:-unknown}" "${os_version:-unknown}" "$sdk_version" "$baseline_version" "$elf_hash" "$archive_hash"
printf 'BUILD_RECORD candidate_sdk=%s candidate_runtime=%s test_sdk=%s harness_runtime=8.0.31\n' \
    "$candidate_build_sdk_input" "$candidate_runtime_input" "$sdk_version"
printf 'DESKTOP_UI_ACCEPTANCE=PENDING (GNOME/X11/XWayland/tray/window/file-chooser checks are separate native desktop work)\n'
