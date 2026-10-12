OpenCode Account Manager — Linux x64 portable package

Targets: Ubuntu 22.04, 24.04, and 26.04 x64 are intended targets, but this
package has not yet been verified on those systems. Ubuntu 20.04 is best-effort.

The .NET 8 runtime is bundled; native desktop libraries (including GTK/X11 or
XWayland-related libraries used by Avalonia) are still required from the OS.
No system .NET installation is needed. Extract this archive into a stable,
user-writable directory and run ./start.sh. The script launches the adjacent
OpenCodeAccountManager executable and forwards its arguments.

Optional user-level desktop entry:
  ./install-desktop.sh
  ./uninstall-desktop.sh

These scripts write only this application's user XDG desktop/icon entries and
remove them only while their ownership marker and file hashes still match.
They do not use sudo or install extensions/system packages. Uninstalling the
desktop entry does not remove the program, account data, settings, or logs.

The updater is intended only for an owned, writable portable installation.
If a manual recovery is needed, extract a fresh copy into a new directory and
launch it there; review the previous directory before doing anything with it.
Settings and logs may be stored beside the program.

For tests, use a fresh extraction and a dedicated isolated Linux user with no
real accounts or OpenCode work. An empty HOME alone does not isolate processes
running under the same user. Do not use a normal account or production data for
testing. Native GTK/X11, XWayland, tray, and desktop integration checks remain
unverified.
