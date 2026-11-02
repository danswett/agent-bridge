public struct BridgeDelegate: Identifiable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let status: String
    public let line: String?

    public init?(json: JSONValue) {
        guard let id = json["id"]?.string, !id.isEmpty else { return nil }
        self.id = id
        name = json["name"]?.string ?? "Delegate"
        let status = json["status"]?.string ?? "idle"
        self.status = ["working", "waiting", "idle", "ended"].contains(status) ? status : "idle"
        line = json["line"]?.string
    }
}
