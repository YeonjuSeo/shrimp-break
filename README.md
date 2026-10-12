# 🦐 ShrimpBreak

N분마다 화면 바닥에서 새우가 튀어나와 "일어나서 자세 바꾸기"를 알려주는 macOS 메뉴바 앱.

## 빌드 / 실행

```bash
scripts/make_app.sh          # → ShrimpBreak.app
open ShrimpBreak.app
```

이미지를 바꾸려면 원본을 `assets-src/` 에 두고 배경을 따낸다:

```bash
swift scripts/cutout.swift assets-src/shrimp-original.png Resources/shrimp.png
```

## 동작

- 간격: 10분 / 20분 / 30분 / 1시간 / 직접 입력 (메뉴바 🦐 클릭)
- 시간이 되면 새우 1마리 → 클릭 안 하면 1분마다 1마리씩 추가 (25% 확률로 새우튀김, 최대 40마리)
- 아무 새우나 한 번 클릭 = 자세 바꿈 → 전부 사라지고 다음 주기 시작
- 일시정지 토글
- 자리 비움(화면 잠금·잠자기·디스플레이 꺼짐·N분 무입력) → 타이머 정지, 돌아오면 처음부터 다시 셈
