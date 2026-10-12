#!/bin/bash
# Runs as the logged-in user, detached from the application (started with setsid by the app):
# waits for the application to exit, installs the downloaded package through pkexec, then starts
# the program again - the new version, or the old one if the installation did not happen.
#
#   apply-update.sh <downloaded-.deb> <sha256> <application-pid>
set -u
deb=${1:-}
sha=${2:-}
pid=${3:-}
program=/usr/bin/opencode-account-manager
helper=/usr/lib/opencode-account-manager/install-update
data=$HOME/.config/OpenCodeAccountImporter
log=$data/logs/update-$(date +%Y%m%d-%H%M%S).log
mkdir -p "$data/logs"
exec >>"$log" 2>&1
echo "update started: package=$deb pid=$pid"

relaunch() {
    echo "starting $program"
    setsid -f "$program" </dev/null >/dev/null 2>&1
}

finish() {  # $1 = user-facing failure message, or empty on success
    rm -f -- "$deb"
    rmdir -- "$(dirname -- "$deb")" 2>/dev/null || true
    rmdir -- "$(dirname -- "$(dirname -- "$deb")")" 2>/dev/null || true
    if [[ -n $1 ]]; then
        printf '%s\n' "$1" > "$data/update-failed.txt"
        echo "update failed: $1"
    else
        rm -f -- "$data/update-failed.txt"
        echo "update installed"
    fi
    relaunch
    exit 0
}

[[ -f $deb && -n $sha && $pid =~ ^[0-9]+$ ]] || finish '升级参数无效，已取消。'

for _ in $(seq 1 120); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.25
done
if kill -0 "$pid" 2>/dev/null; then finish '程序没有在 30 秒内退出，已取消升级。'; fi

command -v pkexec >/dev/null 2>&1 || finish '系统缺少 pkexec（polkit），无法获得管理员授权；请手动安装新的 .deb。'
pkexec "$helper" "$deb" "$sha"
rc=$?
echo "installer exit code: $rc"
case $rc in
    0)   finish '' ;;
    126) finish '没有获得管理员授权，升级已取消，仍是旧版本。' ;;
    127) finish '无法完成管理员授权（没有可用的授权窗口），升级已取消，仍是旧版本。' ;;
    67)  finish '安装包校验失败，升级已取消，仍是旧版本。' ;;
    69)  finish '下载的版本不高于当前版本，升级已取消。' ;;
    *)   finish "安装没有成功（退出码 $rc），仍是旧版本；详见日志 $log" ;;
esac
