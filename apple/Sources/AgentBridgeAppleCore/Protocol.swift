import Foundation

/// Bridge Protocol v1 envelopes. Unknown fields and enum values remain forward compatible.
public struct BridgeRequest: Encodable, Sendable {
    public let id: Int
    public let op: String
    public let data: [String: String]

    public init(id: Int, op: String, data: [String: String] = [:]) {
        self.id = id
        self.op = op
        self.data = data
    }
}

public struct BridgeEnvelope: Decodable, Sendable {
    public let re: Int?
    public let ok: Bool?
    public let error: String?
    public let ev: String?
    public let seq: Int?
    public let result: JSONValue?
    public let data: JSONValue?
}

public indirect enum JSONValue: Codable, Sendable, Equatable {
    case object([String: JSONValue])
    case array([JSONValue])
    case string(String)
    case number(Double)
    case bool(Bool)
    case null

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode([String: JSONValue].self) { self = .object(value) }
        else { self = .array(try container.decode([JSONValue].self)) }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .object(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .bool(let value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }

    public subscript(_ key: String) -> JSONValue? {
        if case .object(let object) = self { return object[key] }
        return nil
    }

    public var string: String? {
        if case .string(let value) = self { return value }
        return nil
    }

    public var array: [JSONValue]? {
        if case .array(let value) = self { return value }
        return nil
    }
}

public struct BridgeSession: Identifiable, Sendable, Equatable {
    public let id: String
    public let agent: String
    public let status: String
    public let title: String

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string else { return nil }
        self.id = id
        self.agent = json["agent"]?.string ?? "unknown"
        self.status = json["status"]?.string ?? "idle"
        self.title = json["title"]?.string ?? json["workspace"]?.string ?? id
    }
}
