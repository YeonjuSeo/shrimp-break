import AppKit
import QuartzCore

/// 화면 위를 통통 튀어다니는 새우들. 여러 모니터를 넘나든다.
///
/// 새우는 전역 좌표(모든 모니터를 합친 좌표계)에서 움직이고, 모니터마다 있는 투명 오버레이 창(무대)에
/// 자기 몸이 걸쳐 있는 만큼 그려진다. 경계를 지날 때 양쪽 화면에 반씩 보인다.
///
/// 이동 규칙
/// - 바닥: 지금 속한 모니터의 아래쪽 끝.
/// - 옆에 이웃 모니터가 있는 경계는 통과한다. 이웃 바닥이 더 높으면 크게 점프해서 올라탄다.
/// - 이웃이 없는 바깥 끝은 벽 — 반대 방향으로 튕긴다.
///
/// 성능 메모
/// - 새우가 한 마리도 없으면 오버레이 창을 내리고(orderOut) 디스플레이 링크를 멈춘다 → CPU/GPU 0.
/// - 새우는 CALayer 로 그리고 위치만 바꾼다. 실제 합성은 WindowServer(GPU)가 한다.
/// - 새우가 걸쳐 있지 않은 모니터의 창은 내려 둔다.
/// - 오버레이는 평소 클릭을 통과시키고(ignoresMouseEvents), 커서가 새우 위에 있을 때만 클릭을 받는다.
@MainActor
final class ShrimpOverlay: NSObject {
    /// 새우를 클릭했을 때. 인자는 클릭한 것이 새우튀김인지.
    var onShrimpClicked: (_ clickedTempura: Bool) -> Void = { _ in }

    private var stages: [ScreenStage] = []
    private var shrimps: [Shrimp] = []
    private let images = ShrimpImages()
    private var displayLink: CADisplayLink?
    private var lastTimestamp: CFTimeInterval?
    private var isClearing = false
    private var isHoveringShrimp = false
    /// 새우가 떠 있는 동안에만 켜는 전역 클릭 감지 (아래 updateMousePassThrough 주석 참고).
    private var globalClickMonitor: Any?

    private static let shrimpSizeMillimeters: CGFloat = 50
    private static let maxShrimps = 40
    /// 낮을수록 같은 높이를 더 천천히 오르내린다 (통통 튀는 리듬).
    private static let gravity: CGFloat = 1664 // pt/s² — 처음 값(2600)의 0.8² → 같은 높이를 0.8배 속도로
    private static let minSpeed: CGFloat = 128 // pt/s — 옆으로 가는 최소 속도
    private static let maxSpeed: CGFloat = 256
    /// 충돌 판정용 원의 반지름 (새우 그림이 정사각 칸을 다 채우지 않아서 조금 작게).
    private static let collisionRadiusRatio: CGFloat = 0.38
    /// 더 높은 이웃 모니터로 올라탈 때, 그 바닥보다 이만큼 더 높이 뛴다 (새우 크기 대비).
    private static let climbMarginRatio: CGFloat = 0.8
    /// 이보다 더 높이 올라가야 하는 경계는 벽으로 취급한다.
    private static let maxClimbHeight: CGFloat = 1200

    override init() {
        super.init()
        NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleScreensChanged() }
        }
    }

    var count: Int { isClearing ? 0 : shrimps.count }

    /// 커서가 있는 화면 바닥에서 새우 한 마리를 내보낸다.
    func spawn() {
        if isClearing { finishClear() }
        if stages.isEmpty { buildStages() }
        guard shrimps.count < Self.maxShrimps,
              let home = stageUnderMouse() ?? stages.first else { return }

        let size = Self.shrimpSizeMillimeters * home.pointsPerMillimeter
        let isTempura = Double.random(in: 0..<1) < 0.20
        // 높이 제각각으로 통통. 바닥 아래(-size)에서 솟아오른다.
        let peak = CGFloat.random(in: 140...360)
        let shrimp = Shrimp(
            image: isTempura ? images.tempura : images.shrimp,
            isTempura: isTempura,
            size: size,
            home: home,
            x: CGFloat.random(in: home.frame.minX...max(home.frame.minX + 1, home.frame.maxX - size)),
            y: home.frame.minY - size,
            vx: CGFloat.random(in: Self.minSpeed...Self.maxSpeed) * (Bool.random() ? 1 : -1),
            vy: sqrt(2 * Self.gravity * (peak + size)),
            bounceVelocity: sqrt(2 * Self.gravity * peak)
        )
        shrimps.append(shrimp)
        render()
        startAnimating()
    }

    /// 살짝 커지며 사라지는 효과 후 정리.
    func clearAll() {
        guard !shrimps.isEmpty, !isClearing else { return }
        isClearing = true
        stages.forEach { $0.setAcceptsMouse(false) }

        CATransaction.begin()
        CATransaction.setAnimationDuration(0.22)
        CATransaction.setCompletionBlock { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.isClearing else { return }
                self.finishClear()
            }
        }
        for layer in shrimps.flatMap({ $0.layers.values }) {
            layer.opacity = 0
            layer.setAffineTransform(layer.affineTransform().scaledBy(x: 1.4, y: 1.4))
        }
        CATransaction.commit()
    }

    // MARK: - Stages

    private func buildStages() {
        stages = NSScreen.screens.compactMap { ScreenStage(screen: $0) }
        stages.forEach { stage in
            stage.onMouseDown = { [weak self] point in self?.handleMouseDown(atGlobal: point) }
        }
    }

    /// 모니터 연결/해제·배치 변경 시 무대를 다시 만들고, 떠 있던 새우 수만큼 다시 내보낸다.
    private func handleScreensChanged() {
        let liveCount = count
        finishClear()
        displayLink?.invalidate()
        displayLink = nil
        stages.forEach { $0.close() }
        stages.removeAll()
        for _ in 0..<liveCount { spawn() }
    }

    private func stageUnderMouse() -> ScreenStage? {
        let mouse = NSEvent.mouseLocation
        return stages.first { NSMouseInRect(mouse, $0.frame, false) }
    }

    /// home 의 왼쪽/오른쪽 경계에 맞닿은 모니터 중 새우가 들어갈 수 있는 곳.
    private func neighbor(of home: ScreenStage, towardRight: Bool, for shrimp: Shrimp) -> ScreenStage? {
        let edge = towardRight ? home.frame.maxX : home.frame.minX
        let candidates = stages.filter { stage in
            let touches = abs((towardRight ? stage.frame.minX : stage.frame.maxX) - edge) < 1
            let overlapsVertically = stage.frame.maxY > home.frame.minY && stage.frame.minY < home.frame.maxY
            // 천장이 새우보다 낮은 모니터로는 못 들어간다.
            return touches && overlapsVertically && stage.frame.maxY >= shrimp.y + shrimp.size
        }
        // 지금 높이에서 바로 들어갈 수 있는 곳 우선, 없으면 가장 덜 올라가도 되는 곳.
        let best = candidates.min { climbHeight($0, shrimp) < climbHeight($1, shrimp) }
        guard let best, climbHeight(best, shrimp) <= Self.maxClimbHeight else { return nil }
        return best
    }

    private func climbHeight(_ stage: ScreenStage, _ shrimp: Shrimp) -> CGFloat {
        max(0, stage.frame.minY - shrimp.y)
    }

    // MARK: - Animation

    private func startAnimating() {
        if displayLink == nil, let screen = NSScreen.main ?? NSScreen.screens.first {
            let link = screen.displayLink(target: self, selector: #selector(step(_:)))
            link.add(to: .main, forMode: .common)
            displayLink = link
        }
        lastTimestamp = nil
        displayLink?.isPaused = false

        if globalClickMonitor == nil {
            globalClickMonitor = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDown) { [weak self] _ in
                MainActor.assumeIsolated { self?.handleMouseDown(atGlobal: NSEvent.mouseLocation) }
            }
        }
    }

    private func stopAnimating() {
        displayLink?.isPaused = true
        lastTimestamp = nil
        if let globalClickMonitor { NSEvent.removeMonitor(globalClickMonitor) }
        globalClickMonitor = nil
    }

    @objc private func step(_ link: CADisplayLink) {
        guard !isClearing else { return }
        let now = link.timestamp
        // 프레임이 밀려도(멈칫) 새우가 순간이동하지 않도록 dt 상한을 둔다. 60/120Hz 모두 동일 속도.
        let dt = CGFloat(min(1.0 / 20.0, now - (lastTimestamp ?? now)))
        lastTimestamp = now

        shrimps.forEach { move($0, dt: dt) }
        collide()
        render()
        updateMousePassThrough()
    }

    /// 새우끼리 부딪히면 튕긴다 (Windows 버전 ShrimpPhysics.Collide 와 같은 규칙, 그쪽에 시뮬레이션 테스트가 있다).
    /// 몸을 원으로 보고, 겹친 만큼 서로 밀어낸 뒤 부딪힌 방향(법선)으로 속도를 주고받는다 (같은 무게의 완전 탄성 충돌).
    /// 충돌로 옆 속도가 거의 0 이 되면 제자리에서만 튀므로 최소 옆 속도를 지킨다.
    /// 최대 40마리라 모든 쌍을 비교해도 프레임당 780번이면 충분히 가볍다.
    private func collide() {
        for i in shrimps.indices {
            for j in shrimps.indices where j > i {
                let a = shrimps[i], b = shrimps[j]
                let minDistance = (a.size + b.size) * Self.collisionRadiusRatio
                let dx = (b.x + b.size / 2) - (a.x + a.size / 2)
                let dy = (b.y + b.size / 2) - (a.y + a.size / 2)
                var distance = (dx * dx + dy * dy).squareRoot()
                guard distance < minDistance else { continue }

                // 완전히 겹친 경우엔 아무 방향으로나 떼어 낸다.
                let (nx, ny): (CGFloat, CGFloat)
                if distance < 1e-6 {
                    (nx, ny) = (1, 0)
                    distance = 0
                } else {
                    (nx, ny) = (dx / distance, dy / distance)
                }

                // 1) 겹친 만큼 반씩 밀어낸다.
                let push = (minDistance - distance) / 2
                a.x -= nx * push
                a.y -= ny * push
                b.x += nx * push
                b.y += ny * push

                // 2) 서로 다가오는 중일 때만 법선 방향 속도를 맞바꾼다.
                let approaching = (b.vx - a.vx) * nx + (b.vy - a.vy) * ny
                guard approaching < 0 else { continue }
                a.vx += approaching * nx
                a.vy += approaching * ny
                b.vx -= approaching * nx
                b.vy -= approaching * ny
                for shrimp in [a, b] where abs(shrimp.vx) < Self.minSpeed {
                    shrimp.vx = shrimp.vx < 0 ? -Self.minSpeed : Self.minSpeed
                }
            }
        }
    }

    private func move(_ shrimp: Shrimp, dt: CGFloat) {
        shrimp.vy -= Self.gravity * dt
        shrimp.y += shrimp.vy * dt

        var home = shrimp.home
        var newX = shrimp.x + shrimp.vx * dt
        let towardRight = shrimp.vx > 0
        let isLeavingHome = towardRight
            ? newX + shrimp.size > home.frame.maxX
            : newX < home.frame.minX

        if isLeavingHome {
            // 이미 경계에 몸을 걸친 상태면 높이와 상관없이 끝까지 건너간다 (되돌아가며 튀는 현상 방지).
            let isStraddling = towardRight
                ? shrimp.x + shrimp.size > home.frame.maxX
                : shrimp.x < home.frame.minX
            if let next = neighbor(of: home, towardRight: towardRight, for: shrimp) {
                if shrimp.y < next.frame.minY, !isStraddling {
                    // 이웃 바닥이 더 높다 → 경계에 붙어서 올라탈 만큼 점프.
                    newX = towardRight ? home.frame.maxX - shrimp.size : home.frame.minX
                    let needed = next.frame.minY - shrimp.y + shrimp.size * Self.climbMarginRatio
                    let reachable = shrimp.vy > 0 ? shrimp.vy * shrimp.vy / (2 * Self.gravity) : 0
                    if reachable < needed { shrimp.vy = sqrt(2 * Self.gravity * needed) }
                } else {
                    // 몸 중심이 경계를 넘으면 이웃 모니터 소속으로 바뀐다.
                    let centerX = newX + shrimp.size / 2
                    if towardRight ? centerX > home.frame.maxX : centerX < home.frame.minX {
                        home = next
                        shrimp.home = next
                    }
                }
            } else {
                // 바깥 벽 → 반대 방향으로.
                newX = towardRight ? home.frame.maxX - shrimp.size : home.frame.minX
                shrimp.vx = -shrimp.vx
            }
        }
        shrimp.x = newX

        let floor = home.frame.minY
        if shrimp.y <= floor, shrimp.vy < 0 {
            shrimp.y = floor
            shrimp.vy = shrimp.bounceVelocity
        }
    }

    /// 새우마다 몸이 걸쳐 있는 모니터에만 레이어를 두고 위치를 맞춘다.
    private func render() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for shrimp in shrimps {
            // 기울기 때문에 살짝 삐져나오는 부분까지 포함.
            let bounds = shrimp.rect.insetBy(dx: -shrimp.size * 0.2, dy: -shrimp.size * 0.2)
            // 진행 방향을 바라보고, 오를 땐 고개를 들고 떨어질 땐 숙인다.
            let facing: CGFloat = shrimp.vx >= 0 ? 1 : -1
            let tilt = max(-1, min(1, shrimp.vy / shrimp.bounceVelocity)) * 0.18 * facing
            let transform = CGAffineTransform(rotationAngle: tilt).scaledBy(x: facing, y: 1)

            for stage in stages {
                guard stage.frame.intersects(bounds) else {
                    shrimp.layers.removeValue(forKey: stage.id)?.removeFromSuperlayer()
                    continue
                }
                let layer = shrimp.layers[stage.id] ?? makeLayer(for: shrimp, on: stage)
                layer.position = CGPoint(x: shrimp.x + shrimp.size / 2 - stage.frame.minX,
                                         y: shrimp.y - stage.frame.minY)
                layer.setAffineTransform(transform)
            }
        }
        CATransaction.commit()
        stages.forEach { $0.updateVisibility() }
    }

    private func makeLayer(for shrimp: Shrimp, on stage: ScreenStage) -> CALayer {
        let layer = CALayer()
        layer.contents = shrimp.image
        layer.contentsGravity = .resizeAspect
        layer.contentsScale = stage.backingScaleFactor
        layer.bounds = CGRect(x: 0, y: 0, width: shrimp.size, height: shrimp.size)
        layer.anchorPoint = CGPoint(x: 0.5, y: 0) // 바닥 중앙 기준
        stage.rootLayer.addSublayer(layer)
        shrimp.layers[stage.id] = layer
        return layer
    }

    private func finishClear() {
        stopAnimating()
        shrimps.flatMap { $0.layers.values }.forEach { $0.removeFromSuperlayer() }
        shrimps.removeAll()
        isClearing = false
        isHoveringShrimp = false
        stages.forEach {
            $0.setAcceptsMouse(false)
            $0.updateVisibility()
        }
    }

    // MARK: - Mouse

    /// 커서가 새우 위에 있을 때만 그 모니터의 창이 클릭을 받는다. 나머지는 아래 앱으로 그대로 통과.
    /// 이 판정은 프레임마다 하므로, 커서를 새우 위로 휙 옮기자마자 누르면 한 프레임 늦어 클릭이
    /// 아래 앱으로 갈 수 있다. 그 경우는 전역 클릭 감지(globalClickMonitor)가 새우 위치를 보고 잡아낸다.
    private func updateMousePassThrough() {
        let mouse = NSEvent.mouseLocation
        let isHovering = shrimpHit(atGlobal: mouse) != nil
        for stage in stages {
            stage.setAcceptsMouse(isHovering && NSMouseInRect(mouse, stage.frame, false))
        }
        if isHovering != isHoveringShrimp {
            isHoveringShrimp = isHovering
            if isHovering { NSCursor.pointingHand.set() } else { NSCursor.arrow.set() }
        }
    }

    private func shrimpHit(atGlobal point: CGPoint) -> Shrimp? {
        shrimps.last { shrimp in
            shrimp.rect.insetBy(dx: shrimp.size * 0.12, dy: shrimp.size * 0.12).contains(point)
        }
    }

    private func handleMouseDown(atGlobal point: CGPoint) {
        guard !isClearing, let shrimp = shrimpHit(atGlobal: point) else { return }
        NSCursor.arrow.set()
        onShrimpClicked(shrimp.isTempura)
    }
}

// MARK: - Shrimp

@MainActor
private final class Shrimp {
    let image: CGImage
    let isTempura: Bool
    let size: CGFloat
    /// 지금 바닥으로 삼는 모니터.
    var home: ScreenStage
    // 전역 좌표 (새우 몸의 왼쪽 아래)
    var x: CGFloat
    var y: CGFloat
    var vx: CGFloat
    var vy: CGFloat
    let bounceVelocity: CGFloat
    /// 몸이 걸쳐 있는 모니터별 레이어.
    var layers: [CGDirectDisplayID: CALayer] = [:]

    init(image: CGImage, isTempura: Bool, size: CGFloat, home: ScreenStage,
         x: CGFloat, y: CGFloat, vx: CGFloat, vy: CGFloat, bounceVelocity: CGFloat) {
        self.image = image
        self.isTempura = isTempura
        self.size = size
        self.home = home
        self.x = x
        self.y = y
        self.vx = vx
        self.vy = vy
        self.bounceVelocity = bounceVelocity
    }

    var rect: CGRect { CGRect(x: x, y: y, width: size, height: size) }
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

// MARK: - Screen stage (모니터 하나당 투명 오버레이 창 하나)

@MainActor
private final class ScreenStage {
    var onMouseDown: (CGPoint) -> Void = { _ in }

    let id: CGDirectDisplayID
    /// 전역 좌표에서의 모니터 영역.
    let frame: CGRect
    let backingScaleFactor: CGFloat
    let pointsPerMillimeter: CGFloat
    let rootLayer: CALayer
    private let panel: NSPanel

    init?(screen: NSScreen) {
        guard let id = screen.displayID else { return nil }
        self.id = id
        frame = screen.frame
        backingScaleFactor = screen.backingScaleFactor
        pointsPerMillimeter = screen.pointsPerMillimeter

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

        let view = StageView(frame: NSRect(origin: .zero, size: screen.frame.size))
        view.wantsLayer = true
        panel.contentView = view
        rootLayer = view.layer!

        let origin = frame.origin
        view.onMouseDown = { [weak self] local in
            self?.onMouseDown(CGPoint(x: local.x + origin.x, y: local.y + origin.y))
        }
    }

    /// 이 모니터에 새우가 하나라도 걸쳐 있을 때만 창을 띄운다.
    func updateVisibility() {
        let hasShrimp = !(rootLayer.sublayers?.isEmpty ?? true)
        if hasShrimp, !panel.isVisible {
            panel.orderFrontRegardless()
        } else if !hasShrimp, panel.isVisible {
            panel.orderOut(nil)
        }
    }

    func setAcceptsMouse(_ accepts: Bool) {
        if panel.ignoresMouseEvents == accepts { panel.ignoresMouseEvents = !accepts }
    }

    func close() {
        panel.close()
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
