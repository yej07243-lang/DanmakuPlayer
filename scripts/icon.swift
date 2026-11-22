import AppKit
let target = CommandLine.arguments[1]
let image = NSImage(size: NSSize(width: 1024, height: 1024))
image.lockFocus()
NSColor(calibratedRed: 0.05, green: 0.13, blue: 0.15, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 64, y: 64, width: 896, height: 896), xRadius: 196, yRadius: 196).fill()
NSColor(calibratedRed: 0.36, green: 0.85, blue: 0.76, alpha: 1).setFill()
let triangle = NSBezierPath(); triangle.move(to: NSPoint(x: 362, y: 276)); triangle.line(to: NSPoint(x: 362, y: 748)); triangle.line(to: NSPoint(x: 740, y: 512)); triangle.close(); triangle.fill()
NSColor.white.setFill()
NSBezierPath(roundedRect: NSRect(x: 220, y: 740, width: 220, height: 36), xRadius: 18, yRadius: 18).fill()
NSBezierPath(roundedRect: NSRect(x: 580, y: 248, width: 220, height: 36), xRadius: 18, yRadius: 18).fill()
image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: target))
