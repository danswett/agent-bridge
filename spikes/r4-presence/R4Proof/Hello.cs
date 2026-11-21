using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Roster;

namespace R4Proof;

// webauthn.dll (webauthn.h): a platform credential with user verification required, as the Windows
// app would make one. The client data is ours, as for any native WebAuthn client; its origin is the
// one the roster verifier expects, so a person's assertion here is checked exactly as in a roster.
public sealed unsafe class Hello(string rpId, nint window, Action<Action> ui) : IHello
{
    public const int Cancelled = unchecked((int)0x80090036); // NTE_USER_CANCELLED
    const uint Platform = 1, UvRequired = 1, UvDiscouraged = 3, AttestationNone = 1, AttestationDirect = 3;
    byte[]? credentialId;

    public static byte[] ClientData(string type, byte[] challenge, string rpId) => Encoding.UTF8.GetBytes(
        $"{{\"type\":\"{type}\",\"challenge\":\"{Codec.Base64Url(challenge)}\",\"origin\":\"{WebAuthnAssertion.Origin(rpId)}\",\"crossOrigin\":false}}");

    public static uint ApiVersion => WebAuthNGetApiVersionNumber();
    public static bool PlatformAvailable => WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out var b) == 0 && b != 0;
    public static string ErrorName(int hr) => Marshal.PtrToStringUni(WebAuthNGetErrorName(hr)) ?? hr.ToString("x8");

    // ui runs each webauthn.dll call on the owner window's own thread, so the hWnd it names is the caller's.
    public const int InvalidParameter = unchecked((int)0x80090027); // NTE_INVALID_PARAMETER
    readonly Action<Action> ui = ui;

    public Created Create(byte[] challenge) => Isolate((rk, att) => Make(challenge, rk, att));

    // Every call names the harness's own window. Diagnosis only, after NTE_INVALID_PARAMETER: change
    // one option at a time and stop at the first the API accepts. Whatever that makes is not used
    // (the run stays inconclusive, no attack runs) and the runner's finally deletes it.
    public static Created Isolate(Func<int, uint, Created> make)
    {
        var made = make(1, AttestationDirect);
        if (made.Hr != InvalidParameter) { return made; }
        var log = $"{made.Note}; isolation:";
        foreach (var (name, rk, att) in new[] { ("resident key off", 0, AttestationDirect), ("attestation none", 1, AttestationNone) })
        {
            var next = make(rk, att);
            log += $" {name}: {(next.Hr == 0 ? "accepted" : next.Note)};";
            if (next.Hr != InvalidParameter) { break; }
        }
        return made with { Note = log };
    }

    Created Make(byte[] challenge, int residentKey, uint attestation)
    {
        int hr = 0; nint p = 0;
        ui(() =>
        {
            var rp = new Rp { Version = 1, Id = S(rpId), Name = S("R4 presence proof") };
            var userId = Bytes(RandomNumberGenerator.GetBytes(16));
            var user = new User { Version = 1, IdBytes = 16, Id = userId, Name = S("r4-proof"), DisplayName = S("R4 presence proof") };
            var param = new CoseParam { Version = 1, Type = S("public-key"), Alg = -7 };
            var pars = new List { Count = 1, Items = (nint)(&param) };
            var client = Client(ClientData("webauthn.create", challenge, rpId));
            var opts = new MakeOptions { Version = 3, Timeout = 120_000, Attachment = Platform, RequireResidentKey = residentKey, Uv = UvRequired, Attestation = attestation };
            hr = WebAuthNAuthenticatorMakeCredential(window, &rp, &user, &pars, &client, &opts, out p);
        });
        if (hr != 0) { return new(hr, [], [], false, false, "", ErrorName(hr)); }
        try
        {
            var a = (Attestation*)p;
            var data = Copy(a->AuthData, a->AuthDataBytes);
            credentialId ??= Copy(a->CredentialId, a->CredentialIdBytes);
            return new(0, credentialId, CoseKey(data), (data[32] & 0x08) != 0, (data[32] & 0x04) != 0, Marshal.PtrToStringUni(a->Format) ?? "");
        }
        finally { WebAuthNFreeCredentialAttestation(p); }
    }

    public Call AssertWithPerson(byte[] challenge) => Assert(challenge, UvRequired, 120_000, null);

    public (Task<Call> Done, Action Cancel) Begin(byte[] challenge, bool requireUv)
    {
        var id = (Guid*)Marshal.AllocHGlobal(sizeof(Guid));
        WebAuthNGetCancellationId(id);
        var done = Task.Run(() => Assert(challenge, requireUv ? UvRequired : UvDiscouraged, 30_000, id));
        return (done, () => WebAuthNCancelCurrentOperation(id));
    }

    Call Assert(byte[] challenge, uint uv, uint timeout, Guid* cancel)
    {
        var client = ClientData("webauthn.get", challenge, rpId);
        int hr = 0; nint p = 0;
        ui(() =>
        {
            var cd = Client(client);
            var cred = new Credential { Version = 1, IdBytes = (uint)credentialId!.Length, Id = Bytes(credentialId), Type = S("public-key") };
            var opts = new GetOptions { Version = 3, Timeout = timeout, Credentials = new List { Count = 1, Items = (nint)(&cred) }, Attachment = Platform, Uv = uv, CancellationId = (nint)cancel };
            hr = WebAuthNAuthenticatorGetAssertion(window, rpId, &cd, &opts, out p);
        });
        if (hr != 0) { return new(hr, null, ErrorName(hr)); }
        try { var a = (AssertionOut*)p; return new(0, new(Copy(a->AuthData, a->AuthDataBytes), client, Copy(a->Signature, a->SignatureBytes))); }
        finally { WebAuthNFreeAssertion(p); }
    }

    public int? DeleteMade()
    {
        if (credentialId is not { } id) { return null; }
        fixed (byte* b = id) { return WebAuthNDeletePlatformCredential((uint)id.Length, b); }
    }

    // The COSE EC2 key in the attested credential data: x is -2 (0x21), y is -3 (0x22), each a 32-byte string.
    static byte[] CoseKey(byte[] data)
    {
        var cose = data.AsSpan(55 + (data[53] << 8 | data[54]));
        var x = cose.IndexOf((ReadOnlySpan<byte>)[0x21, 0x58, 0x20]);
        var y = cose.IndexOf((ReadOnlySpan<byte>)[0x22, 0x58, 0x20]);
        return [0x04, .. cose.Slice(x + 3, 32), .. cose.Slice(y + 3, 32)];
    }

    static ClientDataIn Client(byte[] json) => new() { Version = 1, Bytes = (uint)json.Length, Json = Bytes(json), HashAlg = S("SHA-256") };
    // Process-lifetime allocations for a run of a few minutes; never freed, never reused.
    static nint S(string s) => Marshal.StringToHGlobalUni(s);
    static nint Bytes(byte[] b) { var p = Marshal.AllocHGlobal(b.Length); Marshal.Copy(b, 0, p, b.Length); return p; }
    static byte[] Copy(nint p, uint n) { var b = new byte[n]; Marshal.Copy(p, b, 0, (int)n); return b; }

#pragma warning disable CS0649 // filled in by webauthn.dll
    struct Rp { public uint Version; public nint Id, Name, Icon; }
    struct User { public uint Version, IdBytes; public nint Id, Name, Icon, DisplayName; }
    struct CoseParam { public uint Version; public nint Type; public int Alg; }
    struct List { public uint Count; public nint Items; }
    struct ClientDataIn { public uint Version, Bytes; public nint Json, HashAlg; }
    struct Credential { public uint Version, IdBytes; public nint Id, Type; }
    struct MakeOptions { public uint Version, Timeout; public List Credentials, Extensions; public uint Attachment; public int RequireResidentKey; public uint Uv, Attestation, Flags; public nint CancellationId, Exclude; }
    struct GetOptions { public uint Version, Timeout; public List Credentials, Extensions; public uint Attachment, Uv, Flags; public nint U2fAppId, U2fUsed, CancellationId; }
    struct Attestation { public uint Version; public nint Format; public uint AuthDataBytes; public nint AuthData; public uint AttBytes; public nint Att; public uint DecodeType; public nint Decode; public uint ObjectBytes; public nint Object; public uint CredentialIdBytes; public nint CredentialId; }
    struct AssertionOut { public uint Version, AuthDataBytes; public nint AuthData; public uint SignatureBytes; public nint Signature; }
#pragma warning restore CS0649

    [DllImport("webauthn.dll")] static extern uint WebAuthNGetApiVersionNumber();
    [DllImport("webauthn.dll")] static extern int WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out int available);
    [DllImport("webauthn.dll")] static extern nint WebAuthNGetErrorName(int hr);
    [DllImport("webauthn.dll")] static extern int WebAuthNGetCancellationId(Guid* id);
    [DllImport("webauthn.dll")] static extern int WebAuthNCancelCurrentOperation(Guid* id);
    [DllImport("webauthn.dll")] static extern int WebAuthNAuthenticatorMakeCredential(nint hwnd, Rp* rp, User* user, List* pars, ClientDataIn* client, MakeOptions* opts, out nint attestation);
    [DllImport("webauthn.dll", CharSet = CharSet.Unicode)] static extern int WebAuthNAuthenticatorGetAssertion(nint hwnd, string rpId, ClientDataIn* client, GetOptions* opts, out nint assertion);
    [DllImport("webauthn.dll")] static extern void WebAuthNFreeCredentialAttestation(nint p);
    [DllImport("webauthn.dll")] static extern void WebAuthNFreeAssertion(nint p);
    [DllImport("webauthn.dll")] static extern int WebAuthNDeletePlatformCredential(uint bytes, byte* id);
}
