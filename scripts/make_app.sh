#!/usr/bin/env bash
# SwiftPM 빌드 결과물로 ShrimpBreak.app 을 조립한다.
# Usage: scripts/make_app.sh [debug|release]   (default: release)
#   release: Apple Silicon + Intel 유니버설 바이너리 (다른 사람에게 공유 가능)
#   debug:   이 Mac 아키텍처만 (빠른 빌드)
# 환경 변수
#   DEV_TOOLS=1  메뉴에 "지금 새우 부르기 (테스트)" 포함 (본인 확인용). 기본은 미포함.
#   APP_PATH=…   .app 을 만들 위치 (기본: 프로젝트 루트의 ShrimpBreak.app)
set -euo pipefail

CONFIG="${1:-release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="${APP_PATH:-$ROOT/ShrimpBreak.app}"
SWIFT_FLAGS=()
if [ "${DEV_TOOLS:-0}" = "1" ]; then
  echo "▶ dev tools ON (지금 새우 부르기 포함)"
  SWIFT_FLAGS=(-Xswiftc -DSHRIMP_DEV_TOOLS)
fi

if [ "$CONFIG" = "release" ]; then
  echo "▶ swift build -c release (arm64 + x86_64)"
  swift build -c release --arch arm64 --arch x86_64 --package-path "$ROOT" ${SWIFT_FLAGS[@]+"${SWIFT_FLAGS[@]}"}
  BIN="$ROOT/.build/apple/Products/Release/ShrimpBreak"
else
  echo "▶ swift build -c $CONFIG"
  swift build -c "$CONFIG" --package-path "$ROOT" ${SWIFT_FLAGS[@]+"${SWIFT_FLAGS[@]}"}
  BIN="$ROOT/.build/$CONFIG/ShrimpBreak"
fi

echo "▶ assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ShrimpBreak"
cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
# 익명 통계를 받을 구글 시트(Apps Script 웹 앱) 주소. 파일이 없으면 통계 기능 전체가 꺼진 빌드가 된다.
if [ -s "$ROOT/telemetry/endpoint.txt" ]; then
  /usr/libexec/PlistBuddy -c "Add :ShrimpTelemetryURL string $(tr -d '[:space:]' < "$ROOT/telemetry/endpoint.txt")" "$APP/Contents/Info.plist"
  echo "▶ 익명 통계 주소 포함"
fi
cp "$ROOT/Resources/shrimp.png" "$ROOT/Resources/tempura.png" \
   "$ROOT"/Resources/MenuBarSitting*.png "$ROOT"/Resources/MenuBarStanding*.png "$ROOT"/Resources/MenuBarMoving*.png "$APP/Contents/Resources/"

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
