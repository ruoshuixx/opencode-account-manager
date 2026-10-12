#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --project src/AccountManager.App -c Release
