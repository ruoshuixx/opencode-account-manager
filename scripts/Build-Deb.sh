#!/usr/bin/env bash
# Builds opencode-account-manager_<version>_amd64.deb from a Publish-Linux.sh output directory.
#
#   scripts/Publish-Linux.sh 1.0.5 /tmp/ocam-publish
#   scripts/Build-Deb.sh /tmp/ocam-publish /tmp/ocam-deb [debian-version]
#
# Layout: the single-file program in /opt/opencode-account-manager, a /usr/bin launcher link, a
# desktop entry so the program shows up in the application list, and the icon. Settings and logs go
# to the user's ~/.config/OpenCodeAccountImporter because /opt is not writable by the user.
set -euo pipefail

fail() { printf 'Build-Deb: %s\n' "$*" >&2; exit 1; }

(($# >= 2 && $# <= 3)) || fail 'Usage: Build-Deb.sh <publish-output-dir> <new-output-dir> [debian-version]'
for tool in dpkg-deb install sed readlink du awk; do command -v "$tool" >/dev/null 2>&1 || fail "Required command not found: $tool"; done

script_dir="$(cd -- "$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")" && pwd -P)"
root_dir="$(cd -- "$script_dir/.." && pwd -P)"
project="$root_dir/src/AccountManager.App/AccountManager.App.csproj"
source_dir="$(readlink -f -- "$1")"
output_dir="$2"
binary="$source_dir/OpenCodeAccountManager-linux-x64"
icon="$root_dir/packaging/linux/opencode-account-manager.png"
deb_files="$root_dir/packaging/linux/deb"
license_file="$root_dir/LICENSE"

[[ -f "$binary" && -x "$binary" ]] || fail "Published program not found: $binary"
[[ -f "$icon" ]] || fail "Icon not found: $icon"
for needed in install-update apply-update.sh opencode-account-manager-update.policy; do
    [[ -f "$deb_files/$needed" ]] || fail "Packaging file not found: $deb_files/$needed"
done
[[ -f "$license_file" ]] || fail "LICENSE not found: $license_file"
bash -n "$deb_files/install-update" && bash -n "$deb_files/apply-update.sh" || fail 'A helper script has a syntax error.'
head -c 4 "$binary" | grep -q $'\x7fELF' || fail 'The published program is not an ELF executable.'
[[ ! -e "$output_dir" ]] || fail "Output directory already exists: $output_dir"

default_version="$(sed -nE 's@.*<InformationalVersion>([^<]+)</InformationalVersion>.*@\1@p' "$project" | head -n 1)"
[[ -n "$default_version" ]] || default_version="$(sed -nE 's@.*<Version>([^<]+)</Version>.*@\1@p' "$project" | head -n 1)"
version="${3:-$default_version}"
[[ "$version" =~ ^[0-9][A-Za-z0-9.+~-]*$ ]] || fail "Not a valid Debian version: $version"

package='opencode-account-manager'
stage="$(mktemp -d "${TMPDIR:-/tmp}/ocam-deb.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
root="$stage/$package"

install -d -m 0755 "$root/DEBIAN" "$root/opt/$package" "$root/usr/bin" \
    "$root/usr/share/applications" "$root/usr/share/icons/hicolor/256x256/apps" \
    "$root/usr/share/doc/$package" "$root/usr/lib/$package" "$root/usr/share/polkit-1/actions"
install -m 0755 "$binary" "$root/opt/$package/OpenCodeAccountManager"
ln -s "/opt/$package/OpenCodeAccountManager" "$root/usr/bin/$package"
install -m 0644 "$icon" "$root/usr/share/icons/hicolor/256x256/apps/$package.png"
# In-app update: a user-side orchestrator and a root helper that polkit authorizes by its path.
install -m 0755 "$deb_files/install-update" "$root/usr/lib/$package/install-update"
install -m 0755 "$deb_files/apply-update.sh" "$root/usr/lib/$package/apply-update.sh"
install -m 0644 "$deb_files/opencode-account-manager-update.policy" \
    "$root/usr/share/polkit-1/actions/io.github.ruoshuixx.opencode-account-manager.update.policy"

cat > "$root/usr/share/applications/$package.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=OpenCode Account Manager
Name[zh_CN]=OpenCode 账号管理
GenericName=OpenCode account manager
GenericName[zh_CN]=OpenCode 账号管理器
Comment=Manage OpenCode accounts, quotas and API providers
Comment[zh_CN]=管理 OpenCode 账号、额度与 API 供应商
Exec=opencode-account-manager
Icon=opencode-account-manager
Terminal=false
Categories=Utility;
Keywords=OpenCode;account;quota;codex;账号;额度;
StartupWMClass=OpenCodeAccountManager
DESKTOP
chmod 0644 "$root/usr/share/applications/$package.desktop"

{
    printf 'OpenCode Account Manager\nCopyright (c) 2026 ruoshuixx\n\nLicense: MIT\n\n'
    cat "$license_file"
    printf '\nThird-party components keep their own licenses (Avalonia, SkiaSharp, CommunityToolkit.Mvvm,\nthe .NET runtime and others bundled in the single-file program).\n'
} > "$root/usr/share/doc/$package/copyright"
chmod 0644 "$root/usr/share/doc/$package/copyright"

installed_size="$(du -sk --apparent-size "$root/opt" "$root/usr" | awk '{s+=$1} END {print s}')"
cat > "$root/DEBIAN/control" <<CONTROL
Package: $package
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $installed_size
Maintainer: ruoshuixx <16500530+ruoshuixx@users.noreply.github.com>
Homepage: https://github.com/ruoshuixx/opencode-account-manager
Depends: curl, ca-certificates, pkexec | policykit-1, libc6 (>= 2.31), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libssl3t64 | libssl3 | libssl1.1, libicu74 | libicu72 | libicu70 | libicu66
Description: OpenCode account manager
 Manage the OpenCode accounts, quota windows and API providers stored by the
 local OpenCode installation: switch the active account, query remaining quota,
 import and export account files. The .NET runtime is bundled.
CONTROL
chmod 0644 "$root/DEBIAN/control"

cat > "$root/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q /usr/share/applications || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
    fi
fi
exit 0
POSTINST
cat > "$root/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
if [ "$1" = "remove" ] || [ "$1" = "purge" ]; then
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q /usr/share/applications || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
    fi
fi
exit 0
POSTRM
chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/postrm"

mkdir -p "$output_dir"
deb="$output_dir/${package}_${version}_amd64.deb"
dpkg-deb --root-owner-group --build "$root" "$deb" >/dev/null

# ---- verification of what was just built -------------------------------------------------------
check="$stage/check"
mkdir "$check"
dpkg-deb -x "$deb" "$check"
[[ -x "$check/opt/$package/OpenCodeAccountManager" ]] || fail 'Program is missing or not executable in the package.'
[[ "$(readlink "$check/usr/bin/$package")" == "/opt/$package/OpenCodeAccountManager" ]] || fail 'Launcher link is wrong.'
for helper in install-update apply-update.sh; do
    [[ -x "$check/usr/lib/$package/$helper" ]] || fail "Update helper $helper is missing or not executable."
done
policy="$check/usr/share/polkit-1/actions/io.github.ruoshuixx.opencode-account-manager.update.policy"
[[ -f "$policy" ]] || fail 'The polkit policy is missing.'
grep -q "/usr/lib/$package/install-update" "$policy" || fail 'The polkit policy does not name the update helper.'
if command -v xmllint >/dev/null 2>&1; then xmllint --noout "$policy" || fail 'The polkit policy is not well-formed XML.'; fi
cmp -s "$binary" "$check/opt/$package/OpenCodeAccountManager" || fail 'Packaged program differs from the published one.'
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$check/usr/share/applications/$package.desktop" || fail 'Desktop entry did not validate.'
fi
if dpkg-deb -c "$deb" | awk '{print $1}' | grep -Eq '^-..[sS]|^-.....[sS]'; then fail 'The package contains a setuid/setgid file.'; fi

sha256sum "$deb" | awk -v n="$(basename "$deb")" '{print $1 "  " n}' > "$deb.sha256"
printf 'Package=%s\nVersion=%s\nFile=%s\nSHA256=%s\n' "$package" "$version" "$deb" "$(cut -d' ' -f1 "$deb.sha256")"
