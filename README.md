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

- 간격: 10분 / 20분 / 30분 / 1시간 / 직접 입력 (메뉴바 새우 아이콘 클릭)
- 시간이 되면 새우 1마리 → 클릭 안 하면 1분마다 1마리씩 추가 (25% 확률로 새우튀김, 최대 40마리)
- 여러 모니터를 넘나든다: 이웃 모니터가 있는 경계는 통과(바닥이 더 높으면 점프해서 올라탐), 바깥 끝은 벽
- 아무 새우나 한 번 클릭 = 자세 바꿈 → 전부 사라지고 다음 주기 시작
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
