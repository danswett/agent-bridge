import Foundation
import Testing
@testable import AgentBridgeAppleCore

@Test func frameRoundTripsUTF8() throws {
    let payload = Data(#"{"op":"hello","text":"こんにちは"}"#.utf8)
    let framed = try LocalFraming.encode(payload)
    #expect(framed.count == payload.count + 4)
    #expect(try LocalFraming.decode(framed) == payload)
}

@Test func rejectsOversizedFrames() throws {
    #expect(throws: LocalFraming.FrameError.oversized(65_520)) {
        try LocalFraming.encode(Data(repeating: 0, count: 65_520))
    }
}

@Test func rejectsTruncatedFrames() throws {
    let frame = try LocalFraming.encode(Data("hello".utf8))
    #expect(throws: LocalFraming.FrameError.truncated) {
        try LocalFraming.decode(frame.dropLast())
    }
}
