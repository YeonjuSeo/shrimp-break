#!/usr/bin/env bash
# Windows 용 ShrimpBreak.exe 를 빌드하고 공유용 zip 을 만든다 (Mac 에서 실행).
# 필요: .NET SDK (~/.dotnet 또는 PATH 의 dotnet)
# Usage:
#   scripts/build_windows.sh             → dist/ShrimpBreak-Windows.zip (공유용, 테스트 메뉴 없음)
#   DEV_TOOLS=1 scripts/build_windows.sh → 위와 같지만 "지금 새우 부르기" 테스트 메뉴 포함
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/windows/ShrimpBreak.Windows.csproj"
DIST="$ROOT/dist"
STAGE="$DIST/windows-stage"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [ ! -f "$ROOT/windows/AppIcon.ico" ]; then
  echo "▶ exe 아이콘 만들기"
  python3 "$ROOT/scripts/make_ico.py" "$ROOT/Resources/shrimp.png" "$ROOT/windows/AppIcon.ico"
fi

echo "▶ 물리 시뮬레이션 테스트"
dotnet run --project "$ROOT/windows/tests" -c Release

DEV_FLAG="false"
if [ "${DEV_TOOLS:-0}" = "1" ]; then
  echo "▶ dev tools ON (지금 새우 부르기 포함)"
  DEV_FLAG="true"
fi

echo "▶ dotnet build (net48)"
rm -rf "$STAGE"
dotnet build "$PROJECT" -c Release -p:DevTools="$DEV_FLAG" -o "$STAGE/build" -warnaserror

echo "▶ zipping"
mkdir -p "$STAGE/ShrimpBreak"
cp "$STAGE/build/ShrimpBreak.exe" "$STAGE/build/ShrimpBreak.exe.config" "$STAGE/ShrimpBreak/"
# 메모장에서 한글이 확실히 보이도록 UTF-8 BOM + CRLF 로 변환해서 넣는다.
python3 -c 'import sys; t=open(sys.argv[1],encoding="utf-8").read().replace("\r\n","\n").replace("\n","\r\n"); open(sys.argv[2],"w",encoding="utf-8-sig",newline="").write(t)' \
  "$ROOT/windows/README.txt" "$STAGE/ShrimpBreak/README.txt"
rm -f "$DIST/ShrimpBreak-Windows.zip"
(cd "$STAGE" && zip -qr "$DIST/ShrimpBreak-Windows.zip" ShrimpBreak)
rm -rf "$STAGE"

echo "✓ $DIST/ShrimpBreak-Windows.zip ($(du -h "$DIST/ShrimpBreak-Windows.zip" | cut -f1))"
