#!/usr/bin/env bash
# 개발자 인증서 없이 지인에게 공유할 zip 을 만든다 (ad-hoc 서명, 공증 없음).
# 받는 사람은 처음 한 번 "확인 없이 열기" 절차가 필요하다 (README 의 공유하기 참고).
# 테스트용 메뉴(지금 새우 부르기)는 빠진 빌드다. 본인 앱(프로젝트 루트의 ShrimpBreak.app)은 건드리지 않는다.
# Usage: scripts/package.sh   → dist/ShrimpBreak.zip
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist"

STAGE="$DIST/stage"
rm -rf "$STAGE"
mkdir -p "$STAGE"
DEV_TOOLS=0 APP_PATH="$STAGE/ShrimpBreak.app" "$ROOT/scripts/make_app.sh" release

echo "▶ zipping"
mkdir -p "$DIST"
rm -f "$DIST/ShrimpBreak.zip"
# ditto 로 묶어야 서명·확장 속성이 깨지지 않는다 (zip 명령은 번들 서명을 망가뜨릴 수 있음).
ditto -c -k --keepParent "$STAGE/ShrimpBreak.app" "$DIST/ShrimpBreak.zip"
rm -rf "$STAGE"

echo "✓ $DIST/ShrimpBreak.zip ($(du -h "$DIST/ShrimpBreak.zip" | cut -f1))"
