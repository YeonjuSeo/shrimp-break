import AppKit
import QuartzCore

/// 화면 위를 통통 튀어다니는 새우들.
///
/// 성능 메모
/// - 새우가 한 마리도 없으면 오버레이 창을 내리고(orderOut) 디스플레이 링크를 멈춘다 → CPU/GPU 0.
/// - 새우는 CALayer 로 그리고 위치만 바꾼다. 실제 합성은 WindowServer(GPU)가 한다.
/// - 오버레이는 평소 클릭을 통과시키고(ignoresMouseEvents), 커서가 새우 위에 있을 때만 클릭을 받는다.
@MainActor
final class ShrimpOverlay {
    var onShrimpClicked: () -> Void = {}

    private var stages: [CGDirectDisplayID: ScreenStage] = [:]
    private let images = ShrimpImages()

    init() {
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleScreensChanged() }
        }
    }

    var count: Int { stages.values.reduce(0) { $0 + $1.count } }

    /// 커서가 있는 화면 바닥에서 새우 한 마리를 내보낸다.
    func spawn() {
        guard let screen = Self.screenUnderMouse() ?? NSScreen.main,
              let stage = stage(for: screen) else { return }
        let isTempura = Double.random(in: 0..<1) < 0.25
        stage.spawn(image: isTempura ? images.tempura : images.shrimp)
    }

    func clearAll() {
        stages.values.forEach { $0.clear() }
    }

    // MARK: - Private

    private func stage(for screen: NSScreen) -> ScreenStage? {
        guard let id = screen.displayID else { return nil }
        if let stage = stages[id] { return stage }
        let stage = ScreenStage(screen: screen)
        stage.onShrimpClicked = { [weak self] in self?.onShrimpClicked() }
        stages[id] = stage
        return stage
    }

    /// 모니터 연결/해제·해상도 변경 시 무대를 다시 만들고, 떠 있던 새우 수만큼 다시 내보낸다.
    private func handleScreensChanged() {
        let liveCount = count
        stages.values.forEach { $0.tearDown() }
        stages.removeAll()
        for _ in 0..<liveCount { spawn() }
    }

    private static func screenUnderMouse() -> NSScreen? {
        let mouse = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
    }
}

// MARK: - Images

@MainActor
private struct ShrimpImages {
    let shrimp: CGImage
    let tempura: CGImage

    init() {
        shrimp = Self.load("shrimp")
        tempura = Self.load("tempura")
    }

    private static func load(_ name: String) -> CGImage {
        guard let url = Bundle.main.url(forResource: name, withExtension: "png"),
              let image = NSImage(contentsOf: url),
              let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
        else { fatalError("missing resource \(name).png — build with scripts/make_app.sh") }
        return cg
    }
}

// MARK: - Screen stage (screen 하나당 오버레이 창 하나)

@MainActor
private final class ScreenStage: NSObject {
    var onShrimpClicked: () -> Void = {}

    private let screen: NSScreen
    private let panel: NSPanel
    private let stageView: StageView
    private var displayLink: CADisplayLink?
    private var shrimps: [Shrimp] = []
    private var lastTimestamp: CFTimeInterval?
    private var isClearing = false

    /// 새우 크기: 실제 화면 기준 가로세로 3cm.
    private let shrimpSize: CGFloat
    private static let shrimpSizeMillimeters: CGFloat = 30
    private static let maxShrimps = 40
    private static let gravity: CGFloat = 2600 // pt/s²

    var count: Int { isClearing ? 0 : shrimps.count }

    init(screen: NSScreen) {
        self.screen = screen
        shrimpSize = Self.shrimpSizeMillimeters * screen.pointsPerMillimeter

        panel = NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: true)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .screenSaver
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.ignoresMouseEvents = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.setFrame(screen.frame, display: false)

        stageView = StageView(frame: NSRect(origin: .zero, size: screen.frame.size))
        stageView.wantsLayer = true
        panel.contentView = stageView

        super.init()

        stageView.onMouseDown = { [weak self] point in self?.handleMouseDown(at: point) }
    }

    func spawn(image: CGImage) {
        if isClearing { finishClear() }
        guard shrimps.count < Self.maxShrimps, let root = stageView.layer else { return }

        let width = screen.frame.width
        let size = shrimpSize
        let layer = CALayer()
        layer.contents = image
        layer.contentsGravity = .resizeAspect
        layer.contentsScale = screen.backingScaleFactor
        layer.bounds = CGRect(x: 0, y: 0, width: size, height: size)
        layer.anchorPoint = CGPoint(x: 0.5, y: 0) // 바닥 중앙 기준

        // 높이 제각각으로 통통. 바닥 아래(-size)에서 솟아오른다.
        let peak = CGFloat.random(in: 140...360)
        let bounceVelocity = sqrt(2 * Self.gravity * peak)
        let shrimp = Shrimp(
            layer: layer,
            x: CGFloat.random(in: 0...(max(1, width - size))),
            y: -size,
            vx: CGFloat.random(in: 160...320) * (Bool.random() ? 1 : -1),
            vy: sqrt(2 * Self.gravity * (peak + size)),
            bounceVelocity: bounceVelocity
        )
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        apply(shrimp)
        root.addSublayer(layer)
        CATransaction.commit()
        shrimps.append(shrimp)

        if !panel.isVisible { panel.orderFrontRegardless() }
        startAnimating()
    }

    /// 살짝 커지며 사라지는 효과 후 정리.
    func clear() {
        guard !shrimps.isEmpty, !isClearing else { return }
        isClearing = true
        panel.ignoresMouseEvents = true

        CATransaction.begin()
        CATransaction.setAnimationDuration(0.22)
        CATransaction.setCompletionBlock { [weak self] in
            MainActor.assumeIsolated { self?.finishClear() }
        }
        for shrimp in shrimps {
            shrimp.layer.opacity = 0
            shrimp.layer.setAffineTransform(shrimp.layer.affineTransform().scaledBy(x: 1.4, y: 1.4))
        }
        CATransaction.commit()
    }

    func tearDown() {
        finishClear()
        panel.close()
    }

    // MARK: - Animation

    private func startAnimating() {
        if displayLink == nil {
            let link = stageView.displayLink(target: self, selector: #selector(step(_:)))
            link.add(to: .main, forMode: .common)
            displayLink = link
        }
        lastTimestamp = nil
        displayLink?.isPaused = false
    }

    private func stopAnimating() {
        displayLink?.isPaused = true
        lastTimestamp = nil
    }

    @objc private func step(_ link: CADisplayLink) {
        guard !isClearing else { return }
        let now = link.timestamp
        // 프레임이 밀려도(멈칫) 새우가 순간이동하지 않도록 dt 상한을 둔다. 60/120Hz 모두 동일 속도.
        let dt = CGFloat(min(1.0 / 20.0, now - (lastTimestamp ?? now)))
        lastTimestamp = now

        let width = screen.frame.width
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for shrimp in shrimps {
            shrimp.vy -= Self.gravity * dt
            shrimp.x += shrimp.vx * dt
            shrimp.y += shrimp.vy * dt

            if shrimp.y <= 0, shrimp.vy < 0 {
                shrimp.y = 0
                shrimp.vy = shrimp.bounceVelocity
            }
            if shrimp.x <= 0 {
                shrimp.x = 0
                shrimp.vx = abs(shrimp.vx)
            } else if shrimp.x + shrimpSize >= width {
                shrimp.x = width - shrimpSize
                shrimp.vx = -abs(shrimp.vx)
            }
            apply(shrimp)
        }
        CATransaction.commit()

        updateMousePassThrough()
    }

    private func apply(_ shrimp: Shrimp) {
        let size = shrimpSize
        shrimp.layer.position = CGPoint(x: shrimp.x + size / 2, y: shrimp.y)
        // 진행 방향을 바라보고, 오를 땐 고개를 들고 떨어질 땐 숙인다.
        let facing: CGFloat = shrimp.vx >= 0 ? 1 : -1
        let tilt = max(-1, min(1, shrimp.vy / shrimp.bounceVelocity)) * 0.18 * facing
        shrimp.layer.setAffineTransform(CGAffineTransform(rotationAngle: tilt).scaledBy(x: facing, y: 1))
    }

    // MARK: - Mouse

    /// 커서가 새우 위에 있을 때만 이 창이 클릭을 받는다. 나머지는 아래 앱으로 그대로 통과.
    private func updateMousePassThrough() {
        let mouse = NSEvent.mouseLocation
        let local = CGPoint(x: mouse.x - screen.frame.minX, y: mouse.y - screen.frame.minY)
        let isOverShrimp = shrimpIndex(at: local) != nil
        if panel.ignoresMouseEvents == isOverShrimp {
            panel.ignoresMouseEvents = !isOverShrimp
            if isOverShrimp { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
        }
    }

    private func shrimpIndex(at point: CGPoint) -> Int? {
        let inset = shrimpSize * 0.12
        return shrimps.lastIndex { shrimp in
            CGRect(x: shrimp.x, y: shrimp.y, width: shrimpSize, height: shrimpSize)
                .insetBy(dx: inset, dy: inset)
                .contains(point)
        }
    }

    private func handleMouseDown(at point: CGPoint) {
        guard !isClearing, shrimpIndex(at: point) != nil else { return }
        NSCursor.arrow.set()
        onShrimpClicked()
    }

    private func finishClear() {
        stopAnimating()
        shrimps.forEach { $0.layer.removeFromSuperlayer() }
        shrimps.removeAll()
        isClearing = false
        panel.ignoresMouseEvents = true
        panel.orderOut(nil)
    }
}

private final class Shrimp {
    let layer: CALayer
    var x: CGFloat
    var y: CGFloat
    var vx: CGFloat
    var vy: CGFloat
    let bounceVelocity: CGFloat

    init(layer: CALayer, x: CGFloat, y: CGFloat, vx: CGFloat, vy: CGFloat, bounceVelocity: CGFloat) {
        self.layer = layer
        self.x = x
        self.y = y
        self.vx = vx
        self.vy = vy
        self.bounceVelocity = bounceVelocity
    }
}

private final class StageView: NSView {
    var onMouseDown: (CGPoint) -> Void = { _ in }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        onMouseDown(convert(event.locationInWindow, from: nil))
    }
}

// MARK: - NSScreen helpers

private extension NSScreen {
    var displayID: CGDirectDisplayID? {
        deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
    }

    /// 물리 크기(mm) 대비 포인트 수. 알 수 없으면 일반적인 Retina 노트북 값(약 5pt/mm)을 쓴다.
    var pointsPerMillimeter: CGFloat {
        guard let id = displayID else { return 5 }
        let millimeters = CGDisplayScreenSize(id)
        guard millimeters.width > 0 else { return 5 }
        return frame.width / millimeters.width
    }
}
