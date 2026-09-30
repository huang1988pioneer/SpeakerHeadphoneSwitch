// 產生應用程式圖示：深色圓角底＋琥珀色耳機，右下角喇叭徽章表示「喇叭／耳機切換」，配色與 App.axaml 一致。
// 用法：swift packaging/macos/make-icon.swift
// 輸出：packaging/macos/AppIcon.icns（macOS bundle）、Assets/AppIcon.png（視窗／Dock 圖示）、
//       Assets/AppIcon.ico（Windows exe 圖示）
import AppKit

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
    .deletingLastPathComponent().deletingLastPathComponent()
let icnsURL = root.appendingPathComponent("packaging/macos/AppIcon.icns")
let assets = root.appendingPathComponent("Assets")
try FileManager.default.createDirectory(at: assets, withIntermediateDirectories: true)

let amber = NSColor(srgbRed: 0xF5/255, green: 0xB5/255, blue: 0x44/255, alpha: 1)
let amberStrong = NSColor(srgbRed: 0xFF/255, green: 0xD1/255, blue: 0x66/255, alpha: 1)
let top = NSColor(srgbRed: 0x1B/255, green: 0x2B/255, blue: 0x49/255, alpha: 1)
let bottom = NSColor(srgbRed: 0x0B/255, green: 0x11/255, blue: 0x20/255, alpha: 1)
let line = NSColor(srgbRed: 0x2A/255, green: 0x3B/255, blue: 0x5D/255, alpha: 1)

func drawSymbol(_ name: String, pointSize: CGFloat, weight: NSFont.Weight, color: NSColor, center: NSPoint) {
    let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: weight)
        .applying(.init(paletteColors: [color]))
    guard let symbol = NSImage(systemSymbolName: name, accessibilityDescription: nil)?
        .withSymbolConfiguration(config) else { return }
    let size = symbol.size
    symbol.draw(in: NSRect(x: center.x - size.width / 2, y: center.y - size.height / 2,
                           width: size.width, height: size.height))
}

/// inset：macOS 圖示網格四周留白約 10%；Windows 圖示使用較少留白。
func render(_ pixels: Int, inset insetRatio: CGFloat) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .high

    let s = CGFloat(pixels)
    let inset = s * insetRatio
    let rect = NSRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let w = rect.width
    let radius = w * 0.225

    // 底板：垂直漸層＋細邊框。
    let plate = NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius)
    NSGradient(starting: top, ending: bottom)!.draw(in: plate, angle: -90)
    line.setStroke()
    plate.lineWidth = max(1, w * 0.012)
    plate.stroke()

    // 主體：耳機，略偏左上，讓出右下角給徽章。
    drawSymbol("headphones", pointSize: w * 0.46, weight: .semibold, color: amber,
               center: NSPoint(x: rect.minX + w * 0.45, y: rect.minY + w * 0.55))

    // 徽章：喇叭，實心圓底並以底板色描邊與耳機分開。
    let badgeDiameter = w * 0.42
    let badgeRect = NSRect(x: rect.maxX - badgeDiameter - w * 0.07, y: rect.minY + w * 0.07,
                           width: badgeDiameter, height: badgeDiameter)
    let ring = NSBezierPath(ovalIn: badgeRect.insetBy(dx: -w * 0.03, dy: -w * 0.03))
    bottom.setFill()
    ring.fill()
    let badge = NSBezierPath(ovalIn: badgeRect)
    NSGradient(starting: amberStrong, ending: amber)!.draw(in: badge, angle: -90)
    drawSymbol("speaker.wave.2.fill", pointSize: badgeDiameter * 0.46, weight: .bold, color: bottom,
               center: NSPoint(x: badgeRect.midX, y: badgeRect.midY))

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

// macOS .icns
let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    try render(base, inset: 0.1).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try render(base * 2, inset: 0.1).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", icnsURL.path]
try iconutil.run()
iconutil.waitUntilExit()
guard iconutil.terminationStatus == 0 else { exit(iconutil.terminationStatus) }

// 執行期視窗圖示（Avalonia 資源）：直接執行時 Dock／工作列也會顯示此圖示。
try render(512, inset: 0.1).write(to: assets.appendingPathComponent("AppIcon.png"))

// Windows .ico：每個尺寸以 PNG 壓縮內嵌（Windows Vista 以上支援）。
let icoSizes = [16, 24, 32, 48, 64, 128, 256]
let images = icoSizes.map { render($0, inset: 0.04) }
var ico = Data()
func append16(_ v: Int) { ico.append(contentsOf: [UInt8(v & 0xFF), UInt8(v >> 8 & 0xFF)]) }
func append32(_ v: Int) { append16(v & 0xFFFF); append16(v >> 16 & 0xFFFF) }
append16(0); append16(1); append16(icoSizes.count)
var offset = 6 + 16 * icoSizes.count
for (size, png) in zip(icoSizes, images) {
    ico.append(UInt8(size >= 256 ? 0 : size))
    ico.append(UInt8(size >= 256 ? 0 : size))
    ico.append(0); ico.append(0)
    append16(1); append16(32)
    append32(png.count); append32(offset)
    offset += png.count
}
images.forEach { ico.append($0) }
try ico.write(to: assets.appendingPathComponent("AppIcon.ico"))

print("icns: \(icnsURL.path)")
print("png:  \(assets.appendingPathComponent("AppIcon.png").path)")
print("ico:  \(assets.appendingPathComponent("AppIcon.ico").path)")
