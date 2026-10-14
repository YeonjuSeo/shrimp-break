# 🦐 ShrimpBreak

N분마다 화면 바닥에서 새우가 튀어나와 "일어나서 자세 바꾸기"를 알려주는 macOS 메뉴바 앱.

## 빌드 / 실행

```bash
DEV_TOOLS=1 scripts/make_app.sh   # → ShrimpBreak.app (본인용: "지금 새우 부르기" 테스트 메뉴 포함)
open ShrimpBreak.app
```

이미지를 바꾸려면 원본을 `assets-src/` 에 두고 배경을 따낸다:

```bash
swift scripts/cutout.swift assets-src/shrimp-original.png Resources/shrimp.png
swift scripts/make_menubar_icon.swift assets-src/menubar-sitting-original.png Resources MenuBarSitting
```

## 동작

- 간격: 10분 / 20분 / 30분 / 1시간 / 직접 입력 (메뉴바 아이콘 클릭)
- "앉기·서기 시간 따로 설정"을 켜면 앉아 있는 시간·서 있는 시간을 각각 설정 (켤 때는 기존 간격이 둘 다에 채워지고, 끌 때는 서 있는 시간이 간격이 됨)
- 시간이 되면 새우 1마리 → 클릭 안 하면 설정한 간격(3초~1분, 기본 1분)마다 1마리씩 추가 (20% 확률로 새우튀김, 최대 40마리, 서로 부딪히면 튕김)
- 여러 모니터를 넘나든다: 이웃 모니터가 있는 경계는 통과(바닥이 더 높으면 점프해서 올라탐), 바깥 끝은 벽
- 아무 새우나 한 번 클릭 = 자세 바꿈 → 파도(겹겹이 쌓인 물결 띠 5개)가 밀려와 다음 주기 시작. 새우는 파도가 닿은 그 자리에서 가까운 띠 위로 올라타 물결을 따라 울렁이며 함께 흘러가고, 파도 앞쪽이 화면 끝에 닿으면 물 전체가 빨라지며 새우와 함께 오른쪽 밖으로 빠져나가고 수위가 가라앉음. 바닥에서 시작하는 건 오른쪽 앞 끝뿐이고, 왼쪽은 화면 끝까지 물이 차 있음 (좌우로 맞닿은 모니터들은 파도 하나가 이어서 지나가고, 위아래로 쌓인 모니터는 각자 자기 바닥에서 지나감. 일시정지·자리 비움 등으로 정리될 땐 조용히 사라짐)
- 일시정지 토글
- 메뉴바 아이콘: 타이머 차례마다 앉은 사람 ↔ 선 사람 교대 (시작은 앉음, 타이머가 울리는 순간 다음 자세로)
- 자리 비움(화면 잠금·잠자기·디스플레이 꺼짐·N분 무입력) → 타이머 정지, 돌아오면 처음부터 다시 셈

## 공유하기

### A. 인증서 없이 지인에게 (무료)

```bash
scripts/package.sh            # → dist/ShrimpBreak.zip (Apple Silicon + Intel, 테스트 메뉴 없음)
```

받는 사람: zip 풀기 → `ShrimpBreak.app` 을 응용 프로그램 폴더로 옮기기 → 더블클릭.
"확인되지 않은 개발자" 경고가 뜨면 **시스템 설정 › 개인정보 보호 및 보안** 맨 아래
"그래도 열기" → 암호 입력. (처음 한 번만. macOS 15부터는 우클릭 › 열기로는 안 됨)

터미널이 편하면 대신:

```bash
xattr -dr com.apple.quarantine /Applications/ShrimpBreak.app
```

### B. 경고 없이 배포 (Apple Developer Program, 연 $99)

"Developer ID Application" 인증서로 서명 + Apple 공증 → `.dmg`. 절차는 `scripts/release.sh` 상단 주석 참고.

```bash
DEV_ID="Developer ID Application: 이름 (TEAMID)" scripts/release.sh   # → dist/ShrimpBreak.dmg
```

## Windows 버전

`windows/` — C# (.NET Framework 4.8, Windows 10/11 기본 탑재 → 설치 불필요). Mac 에서 빌드한다 (.NET SDK 필요, `~/.dotnet`).

```bash
scripts/build_windows.sh               # → dist/ShrimpBreak-Windows.zip (공유용)
DEV_TOOLS=1 scripts/build_windows.sh   # 테스트 메뉴 포함
```

- 빌드 전에 새우 이동 규칙 시뮬레이션 테스트(`windows/tests`)가 먼저 돈다 — 여러 모니터 넘나들기·벽·바닥.
- Mac 과 다른 점: 메뉴바 대신 작업 표시줄 알림 영역(트레이) 아이콘, 남은 시간은 아이콘 툴팁으로 표시.
- 받는 사람: zip 풀기 → `ShrimpBreak.exe` 실행 → "Windows의 PC 보호" 창에서 [추가 정보] → [실행].
- 오류 기록: `%LOCALAPPDATA%\ShrimpBreak\error.log`

## 익명 사용 통계

- 처음 실행 때 동의를 묻고(보내기/안 보내기), 메뉴 "익명 사용 통계 보내기"로 언제든 켜고 끈다.
- 받는 곳: 구글 시트 + Apps Script 웹 앱 — 설정은 `telemetry/SETUP.md`, 코드는 `telemetry/apps-script.gs`.
- 주소는 `telemetry/endpoint.txt`(git 제외)에 두면 Mac(`make_app.sh`)·Windows(`build_windows.sh`) 빌드에 들어간다. 없으면 통계 기능 전체가 꺼진 빌드.
- `DEV_TOOLS=1` 빌드가 보낸 기록은 `build=dev` 로 표시돼 대시보드 집계에서 빠진다.
