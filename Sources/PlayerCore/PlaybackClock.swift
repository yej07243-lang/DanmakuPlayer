import Foundation

/// A monotonic host clock interpolates only between samples of the player's media clock.
public struct PlaybackClock: Sendable {
    private var anchor: Double = 0
    private var host: Double = 0
    private var velocity: Double = 0
    private var playbackRate: Double = 1
    private var stopped = true
    private var initialized = false
    public init() {}
    public func time(at hostTime: Double) -> Double {
        anchor + max(0, min(0.2, hostTime - host)) * velocity
    }
    public mutating func sample(position: Double, at hostTime: Double, rate: Double, paused: Bool, discontinuity: Bool = false) {
        guard position.isFinite, hostTime.isFinite, rate.isFinite, rate > 0 else { return }
        let predicted = time(at: hostTime)
        let error = position - predicted
        if !initialized || paused || stopped || rate != playbackRate || discontinuity || abs(error) > 0.25 {
            anchor = position; velocity = paused ? 0 : rate
        } else {
            // Correct frame-quantized engine samples through velocity, avoiding a backwards jump every poll.
            anchor = predicted
            velocity = rate + min(rate * 0.1, max(-rate * 0.1, error / 0.5))
        }
        host = hostTime; playbackRate = rate; stopped = paused; initialized = true
    }
}
