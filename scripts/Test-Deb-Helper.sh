#!/usr/bin/env bash
# Logic test of the root update helper (packaging/linux/deb/install-update) without root: the
# helper is copied with its PATH and temp directory redirected to stubs, so dpkg/apt are never run.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")" && pwd -P)"
helper_src="$script_dir/../packaging/linux/deb/install-update"
[[ -f "$helper_src" ]] || { echo "helper not found: $helper_src" >&2; exit 1; }

root="$(mktemp -d "${TMPDIR:-/tmp}/ocam-helper-test.XXXXXX")"
trap 'rm -rf -- "$root"' EXIT
stubs="$root/stubs"; work_base="$root/var-tmp"; mkdir -p "$stubs" "$work_base"

# helper under test: stub PATH first, temp dir inside the sandbox
sed -e "s#^PATH=.*#PATH=$stubs:/usr/bin:/bin#" -e "s#/var/tmp/#$work_base/#" "$helper_src" > "$root/install-update"
chmod 755 "$root/install-update"

cat > "$stubs/apt-get" <<'STUB'
#!/bin/bash
printf '%s\n' "$*" >> "$STUB_LOG"
STUB
cat > "$stubs/dpkg-query" <<'STUB'
#!/bin/bash
printf '%s' "${STUB_CURRENT:-}"
STUB
cat > "$stubs/dpkg-deb" <<'STUB'
#!/bin/bash
[[ $1 == --field ]] || exit 2
case $3 in
    Package) printf '%s\n' "${STUB_PACKAGE:-opencode-account-manager}" ;;
    Architecture) printf '%s\n' "${STUB_ARCH:-amd64}" ;;
    Version) printf '%s\n' "${STUB_VERSION:-1.0.5}" ;;
esac
STUB
chmod 755 "$stubs"/*
export STUB_LOG="$root/apt.log"

fail_count=0
expect() {  # name expected-exit [env assignments...] -- args...
    local name=$1 want=$2; shift 2
    local -a envs=()
    while [[ $1 != -- ]]; do envs+=("$1"); shift; done
    shift
    : > "$STUB_LOG"
    set +e
    env "${envs[@]}" "$root/install-update" "$@" >/dev/null 2>&1
    local got=$?
    set -e
    if [[ $got -ne $want ]]; then echo "FAIL $name: exit $got, expected $want"; fail_count=$((fail_count + 1)); return; fi
    if [[ $want -ne 0 && -s $STUB_LOG ]]; then echo "FAIL $name: apt-get ran although the helper refused"; fail_count=$((fail_count + 1)); return; fi
    echo "ok   $name"
}

payload="$root/package.deb"
printf 'synthetic package bytes' > "$payload"
sha="$(sha256sum "$payload" | cut -d' ' -f1)"
uid="$(id -u)"

expect 'newer package is installed'            0  PKEXEC_UID="$uid" STUB_CURRENT=1.0.4B STUB_VERSION=1.0.5 -- "$payload" "$sha"
grep -q -- 'install -y --no-install-recommends' "$STUB_LOG" || { echo 'FAIL: apt-get was not called with the expected options'; fail_count=$((fail_count + 1)); }
expect 'checksum mismatch'                     67 PKEXEC_UID="$uid" -- "$payload" "$(printf 'b%.0s' {1..64})"
expect 'not started through pkexec'            66 -- "$payload" "$sha"
expect 'file owned by someone else'            66 PKEXEC_UID=$((uid + 1)) -- "$payload" "$sha"
ln -sf "$payload" "$root/link.deb"
expect 'symlink is refused'                    65 PKEXEC_UID="$uid" -- "$root/link.deb" "$sha"
expect 'relative path is refused'              65 PKEXEC_UID="$uid" -- "package.deb" "$sha"
expect 'bad checksum argument'                 64 PKEXEC_UID="$uid" -- "$payload" "not-a-hash"
expect 'wrong argument count'                  64 PKEXEC_UID="$uid" -- "$payload"
expect 'another package name'                  68 PKEXEC_UID="$uid" STUB_PACKAGE=other -- "$payload" "$sha"
expect 'another architecture'                  68 PKEXEC_UID="$uid" STUB_ARCH=arm64 -- "$payload" "$sha"
expect 'same version is not an update'         69 PKEXEC_UID="$uid" STUB_CURRENT=1.0.5 STUB_VERSION=1.0.5 -- "$payload" "$sha"
expect 'older version is not an update'        69 PKEXEC_UID="$uid" STUB_CURRENT=1.0.5 STUB_VERSION=1.0.4 -- "$payload" "$sha"

[[ -z "$(ls -A "$work_base")" ]] || { echo 'FAIL: the helper left its work directory behind'; fail_count=$((fail_count + 1)); }
if ((fail_count > 0)); then echo "$fail_count check(s) failed"; exit 1; fi
echo 'all helper checks passed'
