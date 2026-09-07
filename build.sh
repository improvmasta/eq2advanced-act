#!/usr/bin/env bash
# Build EQ2Advanced.dll. Works on the Linux dev host and on Windows CI.
#
# The DLL this produces is the real, loadable plugin: it is compiled against the
# genuine ACT executable that tools/fetch-act.sh downloads, so its assembly
# reference carries ACT's strong-name identity and ACT's own binding redirect
# takes care of version differences. See tools/fetch-act.sh for the full story
# (and for the bug that made this necessary).
#
# .NET Framework 4.8 normally means "Windows only", but the plugin is compiled,
# not run, here: Microsoft.NETFramework.ReferenceAssemblies supplies the 4.8
# reference assemblies. The SDK is user-local (~/.dotnet); nothing system-wide.
set -e
cd "$(dirname "$0")"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Install it user-locally with:" >&2
  echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir \$HOME/.dotnet" >&2
  exit 1
fi

CONFIG="${1:-Release}"

bash tools/fetch-act.sh

echo "==> EQ2Advanced.dll"
dotnet build EQ2Advanced.sln -c "$CONFIG" --nologo -v minimal

DLL="EQ2Advanced/bin/$CONFIG/EQ2Advanced.dll"
echo
echo "Built: $DLL"
