import Foundation
import PlayerCore

/// Opt-in QA draw-callback cadence, not a measurement of displayed GPU frames.
final class RenderCadenceProbe: @unchecked Sendable {
    static let shared = RenderCadenceProbe()
    private let lock = NSLock()
    private let output = ProcessInfo.processInfo.environment["DANMAKU_RENDER_PROBE"]
    private var times: [Double] = []
    private var finished = false
    func record() {
        guard let output else { return }
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return }
        times.append(ProcessInfo.processInfo.systemUptime)
        guard [61, 301, 601].contains(times.count) else { return }
        finished = times.count == 601
        let gaps = zip(times, times.dropFirst()).map { $1 - $0 }.sorted()
        let duration = times.last! - times.first!
        let result: [String: Any] = ["kind": "CPU overlay draw callback cadence; not displayed FPS", "frames": times.count, "seconds": duration, "meanCallbacksPerSecond": Double(times.count - 1) / duration, "medianIntervalMS": gaps[gaps.count / 2] * 1000, "p95IntervalMS": gaps[Int(Double(gaps.count - 1) * 0.95)] * 1000, "maxIntervalMS": gaps.last! * 1000, "gapsOver50MS": gaps.filter { $0 > 0.05 }.count]
        if let data = try? JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: URL(fileURLWithPath: output), options: .atomic) }
    }
}
