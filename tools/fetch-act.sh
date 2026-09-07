#!/usr/bin/env bash
# Download the ACT release this plugin compiles against, into Thirdparty/ACT.
#
# WHY WE BUILD AGAINST THE REAL EXE
#
# ACT is strong-named:
#
#     Advanced Combat Tracker, Version=3.8.5.288, Culture=neutral,
#     PublicKeyToken=a946b61e93d97868
#
# A .NET assembly reference records the identity of what it was compiled
# against, so a stand-in facade (which cannot be signed with ACT's key) produces
# a reference with PublicKeyToken=null. That is a DIFFERENT assembly identity,
# ACT is never a candidate for it, and loading the plugin fails with
# FUSION_E_REF_DEF_MISMATCH (HRESULT 0x80131040). This repo shipped exactly that
# bug once; the fix is to reference the genuine exe, which is freely downloadable.
#
# VERSION DOES NOT NEED TO MATCH THE USER'S ACT. Its own exe.config carries
#
#     <bindingRedirect oldVersion="2.0.0.0-3.65535.65535.65535" newVersion="<theirs>"/>
#
# for this exact assembly identity, so any real ACT in the 2.x-3.x range binds to
# whatever the raider is running. That redirect is keyed on the public key token,
# which is precisely why the facade could never benefit from it.
#
# The exe is NOT committed (see .gitignore) - it is someone else's software; we
# fetch it at build time like OverlayPlugin does.
set -euo pipefail

ACT_VERSION="${ACT_VERSION:-3.8.5.288}"
ACT_SHA256="${ACT_SHA256:-1a0ca91375d79daf3d2cbbb2920c90dbc40f3be2421cdc6765d93e27bf25e4b0}"
URL="https://github.com/EQAditu/AdvancedCombatTracker/releases/download/${ACT_VERSION}/ACTv3.zip"

root="$(cd "$(dirname "$0")/.." && pwd)"
dest="$root/Thirdparty/ACT"
exe="$dest/Advanced Combat Tracker.exe"
stamp="$dest/.version"

if [ -f "$exe" ] && [ "$(cat "$stamp" 2>/dev/null || true)" = "$ACT_VERSION" ]; then
  echo "ACT $ACT_VERSION already present."
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading ACT $ACT_VERSION..."
curl -sSL --fail -o "$tmp/ACTv3.zip" "$URL"

actual="$(sha256sum "$tmp/ACTv3.zip" | cut -d' ' -f1)"
if [ "$actual" != "$ACT_SHA256" ]; then
  echo "SHA256 mismatch for ACTv3.zip" >&2
  echo "  expected $ACT_SHA256" >&2
  echo "  got      $actual" >&2
  echo "Refusing to build against an unverified binary." >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$dest"
unzip -oq "$tmp/ACTv3.zip" -d "$dest"
[ -f "$exe" ] || { echo "ACTv3.zip did not contain the exe" >&2; exit 1; }
printf '%s' "$ACT_VERSION" > "$stamp"

echo "ACT $ACT_VERSION -> Thirdparty/ACT"
