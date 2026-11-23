using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host;

public sealed partial class DeliveryJournal
{
    // The head is {count, tail MAC} of the authenticated priority chain. Appended first, sealed second: a crash
    // between them leaves the journal ahead of the head, which is not a rollback and is accepted and resealed.
    void SealAnchor()
    {
        if (anchor is null) { return; }
        try { anchor.Store(JsonSerializer.SerializeToUtf8Bytes(new { n = priorityRecords.Count, tail = priorityTail })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        { Uncertain = true; log($"priority head seal failed ({e.GetType().Name}); no authority granted"); throw; }
    }

    // A missing, stale, damaged or mismatching head refuses agent priority: no repair, reset, refund or revival.
    void VerifyAnchor()
    {
        if (anchor is null || priorityQuarantined) { return; }
        try
        {
            var blob = anchor.Load();
            if (blob is null)
            {
                if (priorityRecords.Count > 0) { RefuseAnchor("missing"); }
                return;
            }
            using var doc = JsonDocument.Parse(blob);
            var n = doc.RootElement.GetProperty("n").GetInt32();
            var tail = doc.RootElement.GetProperty("tail").GetString();
            if (n < 1 || priorityRecords.Count < n || priorityRecords[n - 1].Hmac != tail) { RefuseAnchor("stale or mismatching"); }
            else if (priorityRecords.Count > n) { SealAnchor(); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or KeyNotFoundException or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        { RefuseAnchor($"unreadable ({e.GetType().Name})"); }
    }

    void RefuseAnchor(string why)
    {
        priorityQuarantined = true;
        log($"priority head {why}; agent priority refused until the journal is reviewed");
    }
}

// A sealed copy of the priority chain's head, kept outside the rewindable journal file (#3720).
internal interface IPriorityAnchor
{
    // Null only when no head was ever sealed; a damaged head throws, which refuses priority.
    byte[]? Load();
    void Store(byte[] head);
}

// DPAPI CurrentUser, so the head is bound to this Windows user on this machine: another user, machine or a
// restored copy of the journal alone cannot forge or refresh it. If the node leaves, the head is gone and
// agent priority is refused (fail closed). A VM snapshot that rewinds both files is a documented gap.
[SupportedOSPlatform("windows")]
internal sealed class DpapiPriorityAnchor(string file) : IPriorityAnchor
{
    static readonly byte[] Entropy = "agent-bridge delivery priority head v1"u8.ToArray();

    public byte[]? Load() => File.Exists(file)
        ? ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser) : null;

    public void Store(byte[] head)
    {
        var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(ProtectedData.Protect(head, Entropy, DataProtectionScope.CurrentUser));
            stream.Flush(true);
        }
        File.Move(temp, file, true);
    }
}
