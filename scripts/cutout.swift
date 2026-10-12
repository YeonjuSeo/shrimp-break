// 체커보드 배경이 픽셀로 박혀 있는(RGB, 알파 없음) 스톡 이미지를 투명 PNG 로 만든다.
// 테두리에서 flood fill 로 "밝고 무채색" 픽셀만 지우므로, 주황/갈색 피사체 내부는 건드리지 않는다.
// Usage: swift scripts/cutout.swift <in.png> <out.png>
import AppKit
import CoreGraphics

let args = CommandLine.arguments
guard args.count == 3,
      let src = NSImage(contentsOfFile: args[1]),
      let cg = src.cgImage(forProposedRect: nil, context: nil, hints: nil)
else {
    FileHandle.standardError.write("usage: cutout.swift <in.png> <out.png>\n".data(using: .utf8)!)
    exit(1)
}

let w = cg.width, h = cg.height
var px = [UInt8](repeating: 0, count: w * h * 4)
let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                    space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))

func sat(_ i: Int) -> Int {
    let r = Int(px[i * 4]), g = Int(px[i * 4 + 1]), b = Int(px[i * 4 + 2])
    return max(r, g, b) - min(r, g, b)
}
func light(_ i: Int) -> Int { Int(min(px[i * 4], px[i * 4 + 1], px[i * 4 + 2])) }
func isBackground(_ i: Int) -> Bool { sat(i) < 28 && light(i) > 150 }

// 1) 테두리에서 시작하는 flood fill
var removed = [Bool](repeating: false, count: w * h)
var stack: [Int] = []
for x in 0..<w { stack.append(x); stack.append((h - 1) * w + x) }
for y in 0..<h { stack.append(y * w); stack.append(y * w + w - 1) }
while let i = stack.popLast() {
    if removed[i] || !isBackground(i) { continue }
    removed[i] = true
    let x = i % w, y = i / w
    if x > 0 { stack.append(i - 1) }
    if x < w - 1 { stack.append(i + 1) }
    if y > 0 { stack.append(i - w) }
    if y < h - 1 { stack.append(i + w) }
}

// 2) 지운 영역에 맞닿은 경계 픽셀은 채도 기반으로 반투명 처리 (회색 헤일로 제거)
var alpha = [UInt8](repeating: 255, count: w * h)
for i in 0..<(w * h) where removed[i] { alpha[i] = 0 }
for i in 0..<(w * h) where !removed[i] {
    let x = i % w, y = i / w
    let touches = (x > 0 && removed[i - 1]) || (x < w - 1 && removed[i + 1])
        || (y > 0 && removed[i - w]) || (y < h - 1 && removed[i + w])
    guard touches, light(i) > 110 else { continue }
    let a = min(1.0, Double(sat(i)) / 90.0)
    alpha[i] = UInt8(a * 255)
}

// 3) 피사체 bounding box 로 크롭 (여백 4px)
var minX = w, minY = h, maxX = 0, maxY = 0
for i in 0..<(w * h) where alpha[i] > 0 {
    let x = i % w, y = i / w
    minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
}
// 작은 섬(워터마크 잔여 점)이 bbox 를 늘리지 않도록: 연결 성분 중 가장 큰 것만 남긴다
var label = [Int](repeating: -1, count: w * h)
var sizes: [Int] = []
for start in 0..<(w * h) where alpha[start] > 0 && label[start] < 0 {
    let id = sizes.count
    var count = 0
    var st = [start]
    label[start] = id
    while let i = st.popLast() {
        count += 1
        let x = i % w, y = i / w
        for (dx, dy) in [(-1, 0), (1, 0), (0, -1), (0, 1)] {
            let nx = x + dx, ny = y + dy
            guard nx >= 0, nx < w, ny >= 0, ny < h else { continue }
            let n = ny * w + nx
            if alpha[n] > 0 && label[n] < 0 { label[n] = id; st.append(n) }
        }
    }
    sizes.append(count)
}
let keep = sizes.indices.max { sizes[$0] < sizes[$1] }!
minX = w; minY = h; maxX = 0; maxY = 0
for i in 0..<(w * h) {
    if label[i] != keep { alpha[i] = 0; continue }
    let x = i % w, y = i / w
    minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
}
let pad = 4
minX = max(0, minX - pad); minY = max(0, minY - pad)
maxX = min(w - 1, maxX + pad); maxY = min(h - 1, maxY + pad)

// premultiplied 버퍼에 알파 적용
for i in 0..<(w * h) {
    let a = Double(alpha[i]) / 255.0
    px[i * 4] = UInt8(Double(px[i * 4]) * a)
    px[i * 4 + 1] = UInt8(Double(px[i * 4 + 1]) * a)
    px[i * 4 + 2] = UInt8(Double(px[i * 4 + 2]) * a)
    px[i * 4 + 3] = alpha[i]
}

let full = ctx.makeImage()!
// CGContext 버퍼는 위→아래 행 순서이므로 crop rect 도 그대로 사용
let cropped = full.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1))!
let rep = NSBitmapImageRep(cgImage: cropped)
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: args[2]))
print("✓ \(args[2]) \(cropped.width)x\(cropped.height)")
