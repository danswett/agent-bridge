import Foundation

/// Bridge Protocol v1 envelopes. Unknown fields and enum values remain forward compatible.
public struct BridgeRequest: Encodable, Sendable {
    public let id: Int
    public let op: String
    public let data: [String: JSONValue]

    public init(id: Int, op: String, data: [String: JSONValue] = [:]) {
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
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode([String: JSONValue].self) {
            self = .object(value)
        } else {
            self = try .array(container.decode([JSONValue].self))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case let .object(value): try container.encode(value)
        case let .array(value): try container.encode(value)
        case let .string(value): try container.encode(value)
        case let .number(value): try container.encode(value)
        case let .bool(value): try container.encode(value)
        case .null: try container.encodeNil()
        }
    }

    public subscript(_ key: String) -> JSONValue? {
        if case let .object(object) = self {
            return object[key]
        }
        return nil
    }

    public var string: String? {
        if case let .string(value) = self {
            return value
        }
        return nil
    }

    public var array: [JSONValue]? {
        if case let .array(value) = self {
            return value
        }
        return nil
    }
}

public extension JSONValue {
    var bool: Bool? {
        if case let .bool(value) = self {
            return value
        }
        return nil
    }

    var number: Double? {
        if case let .number(value) = self {
            return value
        }
        return nil
    }
}
