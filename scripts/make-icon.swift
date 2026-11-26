// Draws PS4 Mem's app icon (a memory chip on a dark blue rounded square) as a 1024 x 1024 PNG.
// scripts/build-app.sh turns it into AppIcon.icns. Drawn in code, so the repo needs no image files.
//
//   swift scripts/make-icon.swift <out.png>
import AppKit

let size: CGFloat = 1024
guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("usage: swift make-icon.swift <out.png>\n".utf8))
    exit(2)
}
let output = URL(fileURLWithPath: CommandLine.arguments[1])

guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
      let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
    FileHandle.standardError.write(Data("can't create the bitmap\n".utf8))
    exit(1)
}
NSGraphicsContext.current = context

// The macOS icon grid: an 824-point rounded square in the middle of the 1024 canvas.
let tile = NSRect(x: 100, y: 100, width: 824, height: 824)
let shape = NSBezierPath(roundedRect: tile, xRadius: 185, yRadius: 185)
NSGradient(starting: NSColor(calibratedRed: 0.13, green: 0.30, blue: 0.62, alpha: 1),
           ending: NSColor(calibratedRed: 0.04, green: 0.09, blue: 0.22, alpha: 1))!
    .draw(in: shape, angle: -90)

// The chip: an SF Symbol, drawn white.
let config = NSImage.SymbolConfiguration(pointSize: 470, weight: .regular)
if let symbol = NSImage(systemSymbolName: "memorychip", accessibilityDescription: nil)?
    .withSymbolConfiguration(config) {
    let tinted = NSImage(size: symbol.size, flipped: false) { rect in
        symbol.draw(in: rect)
        NSColor.white.set()
        rect.fill(using: .sourceAtop)
        return true
    }
    let box = NSRect(x: (size - tinted.size.width) / 2, y: (size - tinted.size.height) / 2,
                     width: tinted.size.width, height: tinted.size.height)
    tinted.draw(in: box)
}

NSGraphicsContext.current = nil
guard let png = bitmap.representation(using: .png, properties: [:]) else {
    FileHandle.standardError.write(Data("can't encode the PNG\n".utf8))
    exit(1)
}
do { try png.write(to: output) } catch {
    FileHandle.standardError.write(Data("can't write \(output.path): \(error)\n".utf8))
    exit(1)
}
