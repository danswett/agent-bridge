@testable import AgentBridgeAppleCore
import CryptoKit
import Foundation
import Testing

private let answerSizeRequest = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

private func sizeQuestion(_ fields: [String]) throws -> BridgeQuestion {
    try #require(BridgeQuestion(json: .object([
        "id": .string("q"), "fields": .array(fields.map { .object(["name": .string($0), "type": .string("text")]) }),
    ])))
}

@Test func twoValidEmojiFieldsAreRejectedBeforeTheHostRefusesTheAnswer() throws {
    let question = try sizeQuestion(["a", "b"])
    let butterflies = String(repeating: "🦋", count: 1024)
    #expect(butterflies.utf8.count == 4096)
    let values: [String: JSONValue] = ["a": .string(butterflies), "b": .string(butterflies)]
    #expect(try JSONEncoder().encode(JSONValue.object(values)).count < 16 * 1024)
    #expect(throws: BridgeOperationError.self) {
        try question.answer(sessionID: "s", currentQuestionID: "q", values: values,
                            capabilities: ["sessions.answer"], sessionAgent: "copilot", requestID: answerSizeRequest)
    }
}

@Test func answerBudgetAcceptsExactlySixteenKiBOfHostJSONAndRejectsOneMoreByte() throws {
    let question = try sizeQuestion(["a", "b"])
    let emptyHostJSON = #"{"id":"s","question":"q","request":"00000000-0000-0000-0000-000000000001","fields":{"a":"","b":""}}"#
    let remaining = 16 * 1024 - emptyHostJSON.utf8.count - 1024 * 12
    let first = String(repeating: "🦋", count: 1024)
    let second = String(repeating: "🦋", count: remaining / 12) + String(repeating: "x", count: remaining % 12)
    let operation = try question.answer(sessionID: "s", currentQuestionID: "q", values: ["a": .string(first), "b": .string(second)],
                                        capabilities: ["sessions.answer"], sessionAgent: "copilot", requestID: answerSizeRequest)
    #expect(try BridgeAnswerJSONSize.byteCount(.object(operation.data)) == 16 * 1024)
    #expect(throws: BridgeOperationError.self) {
        try question.answer(sessionID: "s", currentQuestionID: "q", values: ["a": .string(first), "b": .string(second + "x")],
                            capabilities: ["sessions.answer"], sessionAgent: "copilot", requestID: answerSizeRequest)
    }
}

@Test func answerSizingKeepsBMPTextRawAndCountsNestedOtherAnswersAndKeys() throws {
    let question = try sizeQuestion(["a", "b", "c"])
    let cjk = String(repeating: "漢", count: 1365)
    let values = Dictionary(uniqueKeysWithValues: ["a", "b", "c"].map { ($0, JSONValue.string(cjk)) })
    let operation = try question.answer(sessionID: "s", currentQuestionID: "q", values: values,
                                        capabilities: ["sessions.answer"], sessionAgent: "copilot", requestID: answerSizeRequest)
    #expect(try BridgeAnswerJSONSize.byteCount(.object(operation.data)) == JSONEncoder().encode(JSONValue.object(operation.data)).count)
    // The same host string escaping applies inside arrays, Other objects and property names.
    let nested: JSONValue = .object(["🦋": .array([.object(["other": .string("🦋")]), .number(1.25e30), .null, .bool(false)])])
    let number = try String(decoding: JSONEncoder().encode(JSONValue.number(1.25e30)), as: UTF8.self)
    let expected = "{\"\\uD83E\\uDD8B\":[{\"other\":\"\\uD83E\\uDD8B\"},\(number),null,false]}"
    #expect(try BridgeAnswerJSONSize.byteCount(nested) == expected.utf8.count)
}

@Test func answerSizerMatchesThePinnedDotNet10BitmapAcrossEveryBMPCodePoint() throws {
    // Independently generated from dotnet/runtime v10.0.0 UnicodeHelpers.generated.cs.
    // Includes unassigned/private-use/separator scalars and FEFF, regardless of OS Unicode data.
    var bitmap = Data(repeating: 0, count: 8192)
    for value in UInt32(0) ... 0xFFFF {
        let allowed: Bool
        if value < 0x20 || (0xD800 ... 0xDFFF).contains(value) {
            allowed = false
        } else if value == 0x22 || value == 0x5C {
            allowed = true
        } else {
            let scalar = try #require(Unicode.Scalar(value))
            let text = String(scalar)
            allowed = try BridgeAnswerJSONSize.byteCount(.string(text)) == text.utf8.count + 2
        }
        if allowed {
            bitmap[Int(value / 8)] |= UInt8(1 << (value % 8))
        }
    }
    let digest = SHA256.hash(data: bitmap).map { String(format: "%02x", $0) }.joined()
    #expect(digest == "97392397b3101dfb6d6c3b25e1d26343b91808d7bbf6eb7ca5ad3d4d98af105f")
}

@Test func answerSizerMatchesRelaxedJSONEscapesWithoutChangingLaunchSizing() throws {
    let text = "\u{0000}\u{0008}\t\n\u{000C}\r\"\\/<>&'+`\u{007F}\u{00A0}\u{200D}\u{2028}\u{FEFF}🦋漢"
    let hostJSON = #""\u0000\b\t\n\f\r\"\\/<>&'+`\u007F\u00A0‍\u2028\uFEFF\uD83E\uDD8B漢""#
    #expect(try BridgeAnswerJSONSize.byteCount(.string(text)) == hostJSON.utf8.count)
    let data: [String: JSONValue] = ["prompt": .string(String(repeating: "🦋", count: 2048))]
    #expect(try BridgeAnswerJSONSize.byteCount(.object(data)) > 20 * 1024)
    // Launch measures its received JSON. The answer-only surrogate expansion must not leak here.
    try BridgeOperationValidation.encodedSize(data, maximum: 20 * 1024)
}
