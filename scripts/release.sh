#!/usr/bin/env bash
# Developer ID 서명 → 공증(notarize) → .dmg. 받는 사람이 경고 없이 바로 열 수 있다.
#
# ── 1회 준비 ──────────────────────────────────────────────────────────────
# 1. Apple Developer Program 계정 + "Developer ID Application" 인증서가 로그인 키체인에 있어야 한다.
#    확인: security find-identity -v -p codesigning
#    → "Developer ID Application: <이름> (<TEAMID>)" 가 보여야 함.
#    (Apple Development / Apple Distribution 인증서로는 앱스토어 밖 배포 공증이 안 된다.)
# 2. 공증용 자격 증명을 키체인에 한 번 저장 (Apple ID 비밀번호가 아니라 appleid.apple.com 에서
#    만든 "앱 암호"를 쓴다):
#        xcrun notarytool store-credentials shrimpbreak-notary \
#            --apple-id "you@example.com" --team-id "TEAMID" --password "abcd-efgh-ijkl-mnop"
#
# ── 사용 ──────────────────────────────────────────────────────────────────
#     DEV_ID="Developer ID Application: Your Name (TEAMID)" scripts/release.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/ShrimpBreak.app"
DIST="$ROOT/dist"
DMG="$DIST/ShrimpBreak.dmg"
DEV_ID="${DEV_ID:?DEV_ID 에 'Developer ID Application: …' 인증서 이름을 넣어 주세요}"
NOTARY_PROFILE="${NOTARY_PROFILE:-shrimpbreak-notary}"

echo "▶ building release bundle"
"$ROOT/scripts/make_app.sh" release   # ad-hoc 서명된 번들 → 아래에서 다시 서명

echo "▶ code signing (Developer ID + hardened runtime)"
codesign --force --options runtime --timestamp --sign "$DEV_ID" "$APP/Contents/MacOS/ShrimpBreak"
codesign --force --options runtime --timestamp --sign "$DEV_ID" "$APP"
codesign --verify --strict --verbose=2 "$APP"

echo "▶ building .dmg"
mkdir -p "$DIST"
rm -f "$DMG"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"   # 드래그해서 설치
hdiutil create -volname "ShrimpBreak" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
rm -rf "$STAGE"

echo "▶ notarizing (Apple 에 제출 — 몇 분 걸릴 수 있음)"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait

echo "▶ stapling ticket"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"

echo "✓ done → $DMG"
