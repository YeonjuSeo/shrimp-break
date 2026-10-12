import AppKit

// Dock 아이콘 없이 메뉴바에만 뜨는 것은 Info.plist 의 LSUIElement 로 처리한다.
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
