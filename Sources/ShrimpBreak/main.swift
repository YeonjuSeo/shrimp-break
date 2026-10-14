import AppKit

// Dock 아이콘 없이 메뉴바에만 뜨는 것은 Info.plist 의 LSUIElement 로 처리한다.
let app = NSApplication.shared
// 스케줄러가 첫 루틴을 정하기 전에 예전 타이머 설정을 옮겨 둔다.
Settings.migrateIfNeeded()
let delegate = AppDelegate()
app.delegate = delegate
app.run()
