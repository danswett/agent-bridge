using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A file or image ready to go with a reply.
public sealed class ReplyAttachment
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public string Type { get; init; } = "";
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public bool Resized { get; init; }
    public bool IsImage => Type.StartsWith("image/", StringComparison.Ordinal);
    public long Size => Bytes.Length;
    public string SizeText => $"{Words.Size(Size)}{(Resized ? " · made smaller" : "")}";
    // Sent to the host with a reply once: removing it then lets the host go of it too.
    internal bool Offered { get; set; }
}

// Shrinks an image over the attachment limit (a screenshot, usually) to a JPEG that fits, or
// null if it cannot. The app's is WinRT's encoder; tests give their own.
public interface IImageShrinker
{
    Task<byte[]?> ShrinkAsync(byte[] image, int maxBytes);
}

// The reply box on a card (pwa/js/ui/reply-view.js): text for the session, typed into its
// own input as one line; files and images pasted or attached, sent ahead in chunks; Ctrl+Enter
// sends; what was sent comes out of the box only once the session has it.
public sealed class ReplyViewModel : Observable
{
    static readonly Dictionary<string, Func<string, string>> OutcomeText = new()
    {
        ["delivered"] = _ => "Delivered. The session has your reply.",
        ["unconfirmed"] = at => $"Sent, but not confirmed. Check at the machine{at} before sending it again.",
        ["stale"] = _ => "Not sent: the session has ended.",
        ["busy"] = _ => "Not sent: an answer or another reply to this session is being delivered. Try again in a moment.",
        ["refused"] = _ => "The machine refused this reply.",
    };
    static readonly Dictionary<string, string> ImageExtensions = new()
    {
        ["image/png"] = "png", ["image/jpeg"] = "jpg", ["image/gif"] = "gif", ["image/webp"] = "webp", ["image/heic"] = "heic", ["image/heif"] = "heif",
    };

    readonly Deliveries deliveries;
    readonly HostConnection? host;
    readonly IImageShrinker? shrinker;
    readonly string sessionId;
    readonly string at;
    Session session;
    bool attach;
    string text = "";
    string? error;
    string statusText = "";
    Tone statusTone;
    int preparing;
    (string Request, string Text, List<ReplyAttachment> Files)? sent;
    string? handled;
    bool pausedAfterBusy;

    public ReplyViewModel(Session session, bool attach, Deliveries deliveries, HostConnection? host = null, IImageShrinker? shrinker = null, string? machineName = null)
    {
        this.session = session;
        this.attach = attach;
        this.deliveries = deliveries;
        this.host = host;
        this.shrinker = shrinker;
        sessionId = session.Id;
        at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        deliveries.Changed += OnDelivery;
        Refresh();
    }

    void OnDelivery(Delivery d) { if (d.Key == Key) { Refresh(); } }

    public void Detach() => deliveries.Changed -= OnDelivery;

    public string Key => $"reply|{sessionId}";
    public ObservableCollection<ReplyAttachment> Attachments { get; } = [];

    // The card's reply box rebuilt after the host stopped and started taking replies again:
    // the words, files and any reply on its way carry over.
    internal void TakeDraft(ReplyViewModel old)
    {
        Text = old.text;
        sent = old.sent;
        if (attach) { foreach (var f in old.Attachments) { Attachments.Add(f); } }
        Refresh();
    }

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value)) { return; }
            Error = null;
            Raise(nameof(LineBreakHint));
            Raise(nameof(BytesText));
            Raise(nameof(CanSend));
        }
    }

    public bool CanAttach => attach && !Held && !Pending && Attachments.Count + preparing < AttachmentLimits.PerReply;
    public bool ShowAttach => attach;
    public bool Held => session.Question is not null;
    public string? HeldText => session.Question is null ? null : session.Question.AnswerAtMachine == true
        ? "The session is waiting at the machine for a prompt only it can answer. Answer it there first, then reply."
        : "The session is waiting for an answer to its question. Answer that first, then reply.";
    public bool LineBreakHint => text.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) >= 0;
    public string BytesText
    {
        get
        {
            var n = Encoding.UTF8.GetByteCount(text);
            return n > Limits.ReplyText * 3 / 4 ? $"{(n > Limits.ReplyText ? "Too long: " : "")}{n / 1024.0:F1} KiB of {Limits.ReplyText / 1024} KiB" : "";
        }
    }

    public bool Pending => deliveries.Get(Key)?.State is DeliveryState.Sending or DeliveryState.Waiting;
    public bool CanSend => !Held && !Pending && !pausedAfterBusy && preparing == 0 && (!string.IsNullOrWhiteSpace(text) || Attachments.Count > 0);
    public bool ShowResend => deliveries.Get(Key)?.State == DeliveryState.Failed;
    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); } } }
    public bool HasError => error is not null;
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }

    public void Update(Session next, bool takesFiles)
    {
        session = next;
        attach = takesFiles;
        Refresh();
    }

    // A file too big to read at all (the app does not load gigabytes to find out): refused as
    // AddAsync refuses one over the limit.
    public void Refuse(string? name, string? type, long size) =>
        Error = $"Couldn't attach {Name(name, type ?? "")}: it is {Words.Size(size)}, over the {Words.Size(AttachmentLimits.Bytes)} limit for an attachment.";

    public void Unreadable(string name) => Error = $"Couldn't attach {name}: it could not be read.";

    // Reads a picked or pasted file. An image over the limit is redrawn smaller; anything else
    // over it, or empty, is refused with a reason.
    public async Task AddAsync(string? name, string? type, byte[] bytes)
    {
        Error = null;
        if (Attachments.Count + preparing >= AttachmentLimits.PerReply)
        {
            Error = $"A reply carries at most {AttachmentLimits.PerReply} attachments.";
            return;
        }
        preparing++;
        Refresh();
        try
        {
            type ??= "";
            var fileName = Name(name, type);
            var resized = false;
            if (bytes.Length > AttachmentLimits.Bytes)
            {
                var smaller = type.StartsWith("image/", StringComparison.Ordinal) && shrinker is not null ? await shrinker.ShrinkAsync(bytes, AttachmentLimits.Bytes) : null;
                if (smaller is null || smaller.Length > AttachmentLimits.Bytes)
                {
                    Error = $"Couldn't attach {fileName}: it is {Words.Size(bytes.Length)}, over the {Words.Size(AttachmentLimits.Bytes)} limit for an attachment.";
                    return;
                }
                bytes = smaller;
                resized = true;
                type = "image/jpeg";
                var stem = Path.GetFileNameWithoutExtension(fileName);
                fileName = Name($"{(stem.Length > 0 ? stem : "image")}.jpg", type);
            }
            if (bytes.Length == 0) { Error = $"Couldn't attach {fileName}: it is empty."; return; }
            Attachments.Add(new ReplyAttachment
            {
                Id = Guid.NewGuid().ToString(),
                Name = fileName,
                Type = Encoding.UTF8.GetByteCount(type) <= AttachmentLimits.Type ? type : "",
                Bytes = bytes,
                Sha256 = AgentBridge.Protocol.Attachments.Base64Url(SHA256.HashData(bytes)),
                Resized = resized,
            });
        }
        finally
        {
            preparing--;
            Refresh();
        }
    }

    // The name a file goes by: a pasted image gets one the host can tell its format from, and
    // a name over the limit keeps its extension.
    public static string Name(string? name, string type)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) { n = ImageExtensions.TryGetValue(type, out var ext) ? $"image.{ext}" : "attachment"; }
        if (Encoding.UTF8.GetByteCount(n) <= AttachmentLimits.Name) { return n; }
        var dot = n.LastIndexOf('.');
        var extension = dot > 0 && n.Length - dot <= 13 ? n[dot..] : "";
        var stem = n[..^extension.Length];
        while (stem.Length > 0 && Encoding.UTF8.GetByteCount(stem + extension) > AttachmentLimits.Name) { stem = stem[..^(char.IsLowSurrogate(stem[^1]) && stem.Length > 1 ? 2 : 1)]; }
        return stem + extension;
    }

    public void Remove(ReplyAttachment file)
    {
        if (Pending || !Attachments.Remove(file)) { return; }
        // The host has it from an earlier reply that did not go: it can let it go now.
        if (file.Offered && host is not null) { _ = DiscardAsync(file.Id); }
        Refresh();
    }

    async Task DiscardAsync(string id)
    {
        try { await host!.CallAsync(Ops.AttachmentDiscard, new AttachmentDiscard { Attachment = id }); }
        catch (Exception e) when (e is IOException or Local.HostErrorException or TimeoutException) { }
    }

    public void Send()
    {
        if (!CanSend) { return; }
        var files = Attachments.ToList();
        var request = Request("00000000-0000-4000-8000-000000000000", text, files);
        var problem = Replies.Check(request);
        if (problem is null && JsonSerializer.SerializeToUtf8Bytes(new { id = 1, op = Ops.SessionReply, data = request }, ProtocolJson.Options).Length > ProtocolJson.MaxMessageBytes)
        {
            problem = "the reply is too large to send";
        }
        if (problem is not null)
        {
            Error = char.ToUpperInvariant(problem[0]) + problem[1..] + ".";
            return;
        }
        Error = null;
        foreach (var f in files) { f.Offered = true; }
        var sentText = text;
        var entry = deliveries.Submit(Key, Ops.SessionReply, r => Request(r, sentText, files), _ => false, payload: files, before: (call, progress) => UploadAsync(call, files, progress));
        sent = (entry.Request, sentText, files);
        Refresh();
    }

    public void Resend()
    {
        if (deliveries.Get(Key) is not { State: DeliveryState.Failed, Payload: List<ReplyAttachment> files } || sent is null) { return; }
        var sentText = sent.Value.Text;
        deliveries.Resend(Key, Ops.SessionReply, r => Request(r, sentText, files), (call, progress) => UploadAsync(call, files, progress));
    }

    ReplyRequest Request(string request, string body, IReadOnlyList<ReplyAttachment> files) => new()
    {
        Id = sessionId,
        Request = request,
        Text = QuestionViewModel.WellFormed(body),
        Attachments = files.Count > 0 ? [.. files.Select(f => new AttachmentRef { Id = f.Id })] : null,
    };

    // Each file from wherever the host has got to with it, so one already there costs one
    // small request, and a resend after the host restarted sends it again.
    static async Task UploadAsync(Func<string, object?, TimeSpan?, Task<JsonElement>> call, IReadOnlyList<ReplyAttachment> files, Action<long, long> progress)
    {
        var total = files.Sum(f => f.Size);
        long done = 0;
        foreach (var f in files)
        {
            var state = Read(await call(Ops.AttachmentStart, new AttachmentStart { Attachment = f.Id, Name = f.Name, Type = f.Type.Length > 0 ? f.Type : null, Size = f.Size, Sha256 = f.Sha256 }, null));
            progress(done + Math.Min(state.Received, f.Size), total);
            while (!state.Complete)
            {
                var offset = state.Received;
                if (offset >= f.Size) { throw new Local.HostErrorException($"the machine has all of {f.Name} but did not accept it"); }
                var end = (int)Math.Min(f.Size, offset + AttachmentLimits.Chunk);
                state = Read(await call(Ops.AttachmentChunk, new AttachmentChunk { Attachment = f.Id, Offset = offset, Data = Convert.ToBase64String(f.Bytes, (int)offset, end - (int)offset) }, null));
                if (!state.Complete && state.Received <= offset) { throw new Local.HostErrorException($"the machine did not take the next part of {f.Name}"); }
                progress(done + Math.Min(state.Received, f.Size), total);
            }
            done += f.Size;
        }
    }

    static AttachmentState Read(JsonElement e) => e.Deserialize<AttachmentState>(ProtocolJson.Options) ?? throw new Local.HostErrorException("the machine's answer about the attachment was empty");

    public void Refresh()
    {
        var e = deliveries.Get(Key);
        if (e is { State: DeliveryState.Done } && sent is { } s && s.Request == e.Request && handled != e.Request)
        {
            handled = e.Request;
            if (e.Outcome == "delivered")
            {
                // What went comes out of the box, and only that: anything typed meanwhile stays.
                if (text.StartsWith(s.Text, StringComparison.Ordinal)) { Text = text[s.Text.Length..].TrimStart(); }
                foreach (var f in s.Files) { Attachments.Remove(f); }
            }
            else if (e.Outcome == "unconfirmed")
            {
                // The host has let them go with the reply: sent again, they are new.
                foreach (var f in s.Files) { f.Id = Guid.NewGuid().ToString(); f.Offered = false; }
            }
            sent = null;
        }
        if (e is { State: DeliveryState.Done, Outcome: "busy" } && !pausedAfterBusy && handled == e.Request && e.Request != busyFor)
        {
            busyFor = e.Request;
            pausedAfterBusy = true;
            _ = Task.Delay(2000).ContinueWith(_ => deliveries.Ui.Post(() => { pausedAfterBusy = false; Raise(nameof(CanSend)); }), TaskScheduler.Default);
        }
        (StatusText, StatusTone) = preparing > 0 && !Pending ? ("Reading the attachment…", Tone.Progress) : e is null ? ("", Tone.None) : Describe(e);
        Raise(nameof(Held));
        Raise(nameof(HeldText));
        Raise(nameof(Pending));
        Raise(nameof(CanSend));
        Raise(nameof(CanAttach));
        Raise(nameof(ShowAttach));
        Raise(nameof(ShowResend));
    }

    string? busyFor;

    (string, Tone) Describe(Delivery e)
    {
        switch (e.State)
        {
            case DeliveryState.Sending when e.Progress is { Total: > 0 } p:
                var count = (e.Payload as List<ReplyAttachment>)?.Count ?? 1;
                return ($"Sending {(count == 1 ? "the attachment" : $"{count} attachments")}… {100 * p.Sent / p.Total}%", Tone.Progress);
            case DeliveryState.Sending:
                var sending = e.Attempts > 1 ? "Sending again…" : "Sending…";
                return (session.Status == SessionStatus.Working ? $"{sending} The session is working, and takes it at its next step." : sending, Tone.Progress);
            case DeliveryState.Waiting:
                return (e.Reason == "still being delivered" ? "The machine is still delivering it…" : $"Waiting for the Bridge Host{at}. Your reply goes as soon as it is back.", Tone.Progress);
            case DeliveryState.Failed:
                return ($"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad);
        }
        var outcome = e.Outcome is { } o && OutcomeText.ContainsKey(o) ? o : "unconfirmed";
        var t = OutcomeText[outcome](at);
        if (outcome == "refused" && e.Reason is { } r) { t = $"The machine refused this reply: {r}."; }
        if (outcome == "unconfirmed" && e.Reason is { } u) { t += $" ({u})"; }
        return (t, outcome == "delivered" ? Tone.Ok : outcome == "refused" ? Tone.Bad : Tone.Warn);
    }
}
