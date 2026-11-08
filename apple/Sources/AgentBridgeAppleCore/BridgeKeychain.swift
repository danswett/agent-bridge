import Foundation
import Security

/// Secrets are local to this installation and inaccessible while the device is locked.
/// The account namespace is the normalized relay origin, never a display name.
public enum BridgeKeychain {
    private static let service = "dev.agentbridge.apple"

    public static func load(_ name: String) throws -> Data? {
        var query = base(name)
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess, let data = result as? Data else { throw failure(status) }
        return data
    }

    public static func save(_ data: Data, name: String) throws {
        let query = base(name)
        let changes: [CFString: Any] = [kSecValueData: data, kSecAttrAccessible: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        var status = SecItemUpdate(query as CFDictionary, changes as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            changes.forEach { item[$0.key] = $0.value }
            status = SecItemAdd(item as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw failure(status) }
    }

    public static func remove(_ name: String) throws {
        let status = SecItemDelete(base(name) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw failure(status) }
    }

    private static func base(_ name: String) -> [CFString: Any] {
        [kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: name,
         kSecUseDataProtectionKeychain: true, kSecAttrSynchronizable: false]
    }

    private static func failure(_: OSStatus) -> RelayError {
        .refused("Keychain access failed. Unlock this device and try again.")
    }
}
