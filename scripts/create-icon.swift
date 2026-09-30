import AppKit

let destination = CommandLine.arguments[1]
let size = NSSize(width: 1024, height: 1024)
let image = NSImage(size: size)
image.lockFocus()
NSColor(calibratedRed: 0.31, green: 0.27, blue: 0.90, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 40, y: 40, width: 944, height: 944), xRadius: 210, yRadius: 210).fill()
NSColor.white.setFill()
NSBezierPath(roundedRect: NSRect(x: 250, y: 200, width: 524, height: 640), xRadius: 70, yRadius: 70).fill()
NSColor(calibratedRed: 0.83, green: 0.85, blue: 1, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 388, y: 773, width: 248, height: 110), xRadius: 34, yRadius: 34).fill()
NSColor(calibratedRed: 0.31, green: 0.27, blue: 0.90, alpha: 1).setFill()
for (index, width) in [324.0, 324.0, 218.0].enumerated() {
    NSBezierPath(roundedRect: NSRect(x: 350, y: 625 - Double(index) * 135, width: width, height: 46), xRadius: 23, yRadius: 23).fill()
}
image.unlockFocus()
let bitmap = NSBitmapImageRep(data: image.tiffRepresentation!)!
try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: destination))
