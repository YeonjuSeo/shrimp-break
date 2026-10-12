#!/usr/bin/env bash
# SwiftPM 빌드 결과물로 ShrimpBreak.app 을 조립한다.
# Usage: scripts/make_app.sh [debug|release]   (default: release)
#   release: Apple Silicon + Intel 유니버설 바이너리 (다른 사람에게 공유 가능)
#   debug:   이 Mac 아키텍처만 (빠른 빌드)
set -euo pipefail

CONFIG="${1:-release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/ShrimpBreak.app"

if [ "$CONFIG" = "release" ]; then
  echo "▶ swift build -c release (arm64 + x86_64)"
  swift build -c release --arch arm64 --arch x86_64 --package-path "$ROOT"
  BIN="$ROOT/.build/apple/Products/Release/ShrimpBreak"
else
  echo "▶ swift build -c $CONFIG"
  swift build -c "$CONFIG" --package-path "$ROOT"
  BIN="$ROOT/.build/$CONFIG/ShrimpBreak"
fi

echo "▶ assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ShrimpBreak"
cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/Resources/shrimp.png" "$ROOT/Resources/tempura.png" \
   "$ROOT/Resources/MenuBarIcon.png" "$ROOT/Resources/MenuBarIcon@2x.png" "$APP/Contents/Resources/"

echo "▶ app icon"
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
SQUARE="$(dirname "$ICONSET")/square.png"
sips -s format png --padToHeightWidth 440 440 --padColor FFFFFF "$ROOT/Resources/shrimp.png" --out "$SQUARE" >/dev/null 2>&1 || cp "$ROOT/Resources/shrimp.png" "$SQUARE"
for size in 16 32 128 256 512; do
  sips -z $size $size "$SQUARE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$SQUARE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "  (icon skipped)"

echo "▶ ad-hoc codesign"
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || \
  echo "  (codesign skipped/failed — app still runs unsigned for local dev)"

echo "✓ built $APP"
