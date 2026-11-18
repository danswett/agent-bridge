@testable import AgentBridgeAppleCore
import Foundation
import Testing

enum InvisibleFixtures {
    static let blanks = [
        "", " \t\n\u{0085}", "\u{0000}", "\u{FEFF}", "\u{200B}",
        "\u{0378}", "\u{0301}", "\u{0903}", "\u{20DD}", "\u{2800}",
        "\u{034F}", "\u{115F}", "\u{1160}", "\u{3164}", "\u{FFA0}",
        "\u{17B4}", "\u{180F}", "\u{2065}", "\u{FE0F}", "\u{FFF0}",
        "\u{1BCA0}", "\u{1D173}", "\u{E0001}", "\u{E0100}",
        "\u{3164}\u{200B}\u{FE0F}\u{2800}",
    ]
}

func invisibleVectorText(_ file: String, record: String, named name: String, field: String) throws -> String {
    let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
    let url = root.appendingPathComponent("docs/protocol/vectors/\(file).json")
    let vectors = try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url))
    let refused = try #require(vectors["records"]?[record]?["refused"]?.array)
    let vector = try #require(refused.first { $0["name"]?.string == name })
    return try #require(vector["json"]?[field]?.string)
}

@Test(arguments: InvisibleFixtures.blanks)
func invisibleOnlyTextIsBlankAndCannotBeRepliedWith(_ text: String) {
    #expect(BridgeOperationValidation.blank(text))
    #expect(throws: BridgeOperationError.self) {
        try BridgeSessionOperations.reply(sessionID: "s", text: text, hasQuestion: false, capabilities: ["sessions.reply"])
    }
}

@Test(arguments: InvisibleFixtures.blanks)
func repliesRefuseOperatorsAfterInvisiblePrefixes(_ prefix: String) {
    for command in ["/clear", "!dir"] {
        #expect(throws: BridgeOperationError.self) {
            try BridgeSessionOperations.reply(sessionID: "s", text: prefix + command, hasQuestion: false, capabilities: ["sessions.reply"])
        }
    }
}

@Test func invisibleReplyProtocolVectorsAreRefused() throws {
    for name in ["only-invisible-characters", "a-shell-command-after-an-invisible-operator"] {
        let text = try invisibleVectorText("sessions", record: "ReplyRequest", named: name, field: "text")
        #expect(throws: BridgeOperationError.self) {
            try BridgeSessionOperations.reply(sessionID: "s", text: text, hasQuestion: false, capabilities: ["sessions.reply"])
        }
    }
}

@Test func repliesPreserveVisibleAndNonLatinText() throws {
    for text in ["Continue", "\u{4F60}\u{597D}", "\u{0645}\u{0631}\u{062D}\u{0628}\u{0627}",
                 "\u{0915}\u{093F}", "\u{D55C}", "\u{1F98B}", "\u{FFFD}", "\u{E000}",
                 "\u{034F}e\u{0301} /continue", "\u{3164}Continue !please"]
    {
        #expect(!BridgeOperationValidation.blank(text))
        let reply = try BridgeSessionOperations.reply(sessionID: "s", text: text, hasQuestion: false, capabilities: ["sessions.reply"])
        #expect(reply.data["text"] == .string(text))
    }
}

@Test func invisibleAttachmentRepliesAllowBlankTextButNeverHiddenCommands() throws {
    let capabilities: Set = ["sessions.reply", "sessions.attach"]
    let attachments = [UUID()]
    let reply = try BridgeSessionOperations.reply(sessionID: "s", text: "\u{2800}", attachments: attachments,
                                                 hasQuestion: false, capabilities: capabilities)
    #expect(reply.data["text"] == .string("\u{2800}"))
    for command in ["\u{FEFF}/clear", "\u{3164}/", "\u{2065}!dir"] {
        #expect(throws: BridgeOperationError.self) {
            try BridgeSessionOperations.reply(sessionID: "s", text: command, attachments: attachments,
                                              hasQuestion: false, capabilities: capabilities)
        }
    }
}
