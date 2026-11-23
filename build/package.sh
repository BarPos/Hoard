#!/usr/bin/env bash
# Builds the Native AOT binary and packages a release archive (used by CI and locally).
#
#   build/package.sh            → dist/hoard-<version>-macos-arm64.tar.gz (+ .sha256)
#
# The version comes from the VERSION file. Only macOS on Apple Silicon is supported for now.
set -euo pipefail

cd "$(dirname "$0")/.."
VERSION="$(tr -d '[:space:]' < VERSION)"
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "package.sh: VERSION must look like 1.2.3 or 1.2.3-beta.1, got '$VERSION'" >&2
  exit 1
fi
if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "package.sh: build on macOS / Apple Silicon (this is $(uname -s) $(uname -m))" >&2
  exit 1
fi

RID=osx-arm64
NAME="hoard-${VERSION}-macos-arm64"
OUT="out/publish/${RID}"
STAGE="out/stage/${NAME}"

rm -rf "$OUT" "$STAGE"
mkdir -p dist "$STAGE"

echo "▸ publishing ${RID} (Native AOT, version ${VERSION})"
dotnet publish src/Hoard.Cli -c Release -r "$RID" -o "$OUT" -p:DebugType=none -p:DebugSymbols=false -nologo

echo "▸ smoke test"
reported="$("$OUT/hoard" --version)"
if [[ "$reported" != "$VERSION" ]]; then
  echo "package.sh: built binary reports '$reported', expected '$VERSION'" >&2
  exit 1
fi
"$OUT/hoard" "$OUT" --no-tui --top 1 > /dev/null
codesign --verify "$OUT/hoard"   # the linker ad-hoc signs arm64 binaries; make sure it's intact

echo "▸ packaging ${NAME}.tar.gz"
cp "$OUT/hoard" "$STAGE/hoard"
cp THIRD-PARTY-NOTICES.txt "$STAGE/"
[[ -f LICENSE ]] && cp LICENSE "$STAGE/"
tar -C out/stage -czf "dist/${NAME}.tar.gz" "$NAME"
( cd dist && shasum -a 256 "${NAME}.tar.gz" > "${NAME}.tar.gz.sha256" )

echo "✓ dist/${NAME}.tar.gz ($(du -h "dist/${NAME}.tar.gz" | cut -f1 | tr -d ' '))"
cat "dist/${NAME}.tar.gz.sha256"
