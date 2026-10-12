// 흰 배경 위 검정 실루엣 이미지를 메뉴바용 템플릿 아이콘(@1x/@2x PNG)으로 만든다.
// 어두울수록 불투명한 검정이 되고, 흰 부분(배경·눈·마디 선)은 투명해진다.
// macOS 는 템플릿 이미지의 알파만 보고 메뉴바 색(라이트/다크)에 맞춰 칠한다.
// Usage: swift scripts/make_menubar_icon.swift <in.png> <out-dir>
import AppKit
import CoreGraphics

let args = CommandLine.arguments
guard args.count == 3,
      let src = NSImage(contentsOfFile: args[1]),
      let cg = src.cgImage(forProposedRect: nil, context: nil, hints: nil)
else {
    FileHandle.standardError.write("usage: make_menubar_icon.swift <in.png> <out-dir>\n".data(using: .utf8)!)
    exit(1)
}

let w = cg.width, h = cg.height
var px = [UInt8](repeating: 0, count: w * h * 4)
let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                    space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))

// 밝기 → 알파, 색은 검정 (premultiplied 이므로 RGB 는 0)
var minX = w, minY = h, maxX = 0, maxY = 0
for i in 0..<(w * h) {
    let luminance = (Int(px[i * 4]) * 299 + Int(px[i * 4 + 1]) * 587 + Int(px[i * 4 + 2]) * 114) / 1000
    let alpha = UInt8(255 - luminance)
    px[i * 4] = 0; px[i * 4 + 1] = 0; px[i * 4 + 2] = 0
    px[i * 4 + 3] = alpha
    if alpha > 8 {
        let x = i % w, y = i / w
        minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
    }
}
let masked = ctx.makeImage()!
let trimmed = masked.cropping(to: CGRect(x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1))!

/// 정사각 캔버스 가운데에 비율 유지로 맞춰 그린다.
func render(side: Int, to path: String) {
    let out = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    out.interpolationQuality = .high
    let scale = CGFloat(side) / CGFloat(max(trimmed.width, trimmed.height))
    let dw = CGFloat(trimmed.width) * scale, dh = CGFloat(trimmed.height) * scale
    out.draw(trimmed, in: CGRect(x: (CGFloat(side) - dw) / 2, y: (CGFloat(side) - dh) / 2, width: dw, height: dh))
    let rep = NSBitmapImageRep(cgImage: out.makeImage()!)
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
    print("✓ \(path) \(side)x\(side)")
}

// 메뉴바 아이콘은 18pt 정사각.
render(side: 18, to: "\(args[2])/MenuBarIcon.png")
render(side: 36, to: "\(args[2])/MenuBarIcon@2x.png")
