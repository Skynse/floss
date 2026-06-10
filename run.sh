#!/usr/bin/env bash
# Floss launcher that captures native crashes in SkiaSharp and other unmanaged code.
#
# WHY THIS SCRIPT EXISTS:
#   The .NET runtime reads DOTNET_DbgEnableMiniDump and DOTNET_EnableCrashReport
#   ONCE at startup, before any managed code (including ModuleInitializer) runs.
#   Setting these via Environment.SetEnvironmentVariable inside the app is too
#   late — the runtime has already decided whether to install its native crash
#   handler. To capture SIGSEGV/SIGABRT/etc., the env vars MUST be set in the
#   parent shell before `dotnet` is invoked.

set -euo pipefail

CRASH_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/Floss/crash-reports"
mkdir -p "$CRASH_DIR"

export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=2            # 2 = Heap dump (full info on what crashed)
export DOTNET_DbgMiniDumpName="$CRASH_DIR/coredump.%p"
export DOTNET_EnableCrashReport=1
export DOTNET_CrashReportDirectory="$CRASH_DIR"
# Mirror with COMPlus_ prefix for older runtime compatibility.
export COMPlus_DbgEnableMiniDump=1
export COMPlus_DbgMiniDumpType=2
export COMPlus_DbgMiniDumpName="$CRASH_DIR/coredump.%p"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "--release" ]]; then
    shift
    exec dotnet run --project "$SCRIPT_DIR/src/Floss.App/Floss.App.csproj" \
        --configuration Release -- "$@"
else
    exec dotnet run --project "$SCRIPT_DIR/src/Floss.App/Floss.App.csproj" -- "$@"
fi
