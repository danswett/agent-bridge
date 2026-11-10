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
    // Sealed and uploaded to the relay's blob store for the machine, and when: a reply sent again
    // reuses it while its ticket lasts.
    internal BlobRef? Blob { get; set; }
    internal DateTimeOffset BlobAt { get; set; }
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
    static readonly Dictionary<string, string> OutcomeText = new()
    {
        ["delivered"] = "Delivered",
        ["unconfirmed"] = ReceiptWords.Unconfirmed,
        ["stale"] = "Not sent: the session has ended.",
        ["busy"] = "Not sent: an answer or another reply to this session is being delivered. Try again in a moment.",
        ["refused"] = "The machine refused this reply.",
    };
    static readonly Dictionary<string, string> ImageExtensions = new()
    {
        ["image/png"] = "png", ["image/jpeg"] = "jpg", ["image/gif"] = "gif", ["image/webp"] = "webp", ["image/heic"] = "heic", ["image/heif"] = "heif",
    };

    readonly Deliveries deliveries;
    readonly HostConnection? host;
    readonly IImageShrinker? shrinker;
    readonly string sessionId;
    readonly Func<bool> current;
    Session session;
    bool attach;
    bool large;
    long blobMax;
    string text = "";
    string? error;
    string statusText = "";
    Tone statusTone;
    ReceiptMark statusMark;
    // Times the status line's words, as the receipt of the reply (Receipt, #600, #617).
    readonly Receipt receipt;
    // Send-to-settled timing (#1600): stamped by Send, fed by each session update.
    public TurnClock Turns { get; }
    readonly string? machineName;
    int preparing;
    (string Request, string Text, List<ReplyAttachment> Files)? sent;
    string? handled;
    bool pausedAfterBusy;
    // The line's room stays from Send until the box is next typed in (ui.md §6.5), so the check and
    // Send's return to rest move nothing either.
    bool keepRoom;
    // Text set by this view model (what went coming out of the box), not typed.
    bool settingText;
    bool sendCheck;
    ITimer? checkTimer;

    // large: the host takes files up to AttachmentLimits.DirectBytes in chunks over the pipe
    // (sessions.attach.direct), not only AttachmentLimits.Bytes. current: whether the machine's rows
    // are its host's word now (Machine.Current, #269); while not, nothing is sent or attached.
    public ReplyViewModel(Session session, bool attach, Deliveries deliveries, HostConnection? host = null, IImageShrinker? shrinker = null, string? machineName = null, bool large = false, Func<bool>? current = null)
    {
        this.session = session;
        this.attach = attach;
        this.large = large;
        this.deliveries = deliveries;
        this.host = host;
        this.shrinker = shrinker;
        this.current = current ?? (() => true);
        sessionId = session.Id;
        this.machineName = machineName;
        receipt = new Receipt(deliveries.Time, deliveries.Ui, Describe, Shown, Said);
        Turns = new TurnClock(deliveries.Time);
        deliveries.Changed += OnDelivery;
        Refresh();
    }

    void OnDelivery(Delivery d) { if (d.Key == Key) { Refresh(); } }

    public void Detach()
    {
        deliveries.Changed -= OnDelivery;
        receipt.Dispose();
        checkTimer?.Dispose();
    }

    void Shown(ReceiptLine line)
    {
        StatusMark = line.Mark;
        (StatusText, StatusTone) = (line.Whole, line.Tone);
        Raise(nameof(ShowStatus));
        Raise(nameof(SendStill));
        Unsay();
    }

    // Send's check for about a second once its reply is delivered, then rest (ui.md §6.5). The
    // expiry belongs to the request that showed the check: disposing the timer doesn't take back a
    // SetCheck(false) already posted, which then cleared the next reply's check early (#625,
    // Copilot 4486937437).
    void ShowCheck()
    {
        checkTimer?.Dispose();
        SetCheck(true);
        var shownFor = handled;
        checkTimer = deliveries.Time.CreateTimer(_ => deliveries.Ui.Post(() => { if (handled == shownFor) { SetCheck(false); } }), null, ReceiptTiming.SendCheckHold, Timeout.InfiniteTimeSpan);
    }

    void SetCheck(bool on)
    {
        if (sendCheck == on) { return; }
        sendCheck = on;
        Raise(nameof(SendCheck));
        Raise(nameof(SendRest));
        Raise(nameof(SendEnabled));
    }

    // The words changed: the line is named by them again until their own announcement, so a reader
    // moving to it never hears what an earlier state said (the last reply's Delivered, say).
    void Unsay()
    {
        if (Announcement.Length == 0) { return; }
        Announcement = "";
        Raise(nameof(Announcement));
    }

    // Cleared first, so the same words said again (the next reply delivered) are a change too.
    void Said(string speech, bool urgent)
    {
        AnnouncementUrgent = urgent;
        Raise(nameof(AnnouncementUrgent));
        Announcement = "";
        Raise(nameof(Announcement));
        Announcement = speech;
        Raise(nameof(Announcement));
    }

    public string Key => $"reply|{sessionId}";
    public ObservableCollection<ReplyAttachment> Attachments { get; } = [];

    // The card's reply box rebuilt after the host stopped and started taking replies again:
    // the words, files and any reply on its way carry over.
    internal void TakeDraft(ReplyViewModel old)
    {
        settingText = true;
        try { Text = old.text; }
        finally { settingText = false; }
        sent = old.sent;
        // The room the old box kept stays kept (a reply's capability coming back rebuilds the box).
        keepRoom = old.keepRoom;
        if (attach) { foreach (var f in old.Attachments) { Attachments.Add(f); } }
        Refresh();
    }

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value)) { return; }
            // Not while the reply goes or its check shows: the line is the live region, and its outcome
            // ("Reply delivered", within a second) is still to be said there.
            if (!settingText && keepRoom && !Pending && !sendCheck)
            {
                keepRoom = false;
                Raise(nameof(ShowStatus));
            }
            Error = null;
            Raise(nameof(LineBreakHint));
            Raise(nameof(BytesText));
            Raise(nameof(CanSend));
        Raise(nameof(SendEnabled));
        }
    }

    public bool CanAttach => attach && current() && !Held && !Pending && Attachments.Count + preparing < AttachmentLimits.PerReply;
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
    public Func<bool>? Moving { get; set; }
    public void RefreshSend() { Raise(nameof(CanSend)); Raise(nameof(SendEnabled)); }
    public bool CanSend => current() && !(Moving?.Invoke() ?? false) && !Held && !Pending && !pausedAfterBusy && preparing == 0 && (!string.IsNullOrWhiteSpace(text) || Attachments.Count > 0);
    public bool ShowResend => current() && deliveries.Get(Key)?.State == DeliveryState.Failed;
    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); } } }
    public bool HasError => error is not null;
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    // What Send shows for the request (ui.md §6.5): its ring, the ring standing still, or its check.
    public ReceiptMark StatusMark { get => statusMark; private set => Set(ref statusMark, value); }
    // Send's ring stands still while the reply waits on this device for its machine to come back.
    public bool SendStill => Pending && StatusMark == ReceiptMark.Still;
    // Send's check, held about a second after its reply is delivered (ReceiptTiming.SendCheckHold).
    public bool SendCheck => sendCheck;
    // Send keeps its look while it shows its ring or its check (ui.md §6.5: the same size and colour
    // throughout); Send itself still does nothing unless CanSend.
    public bool SendEnabled => CanSend || Pending || sendCheck;
    // Send's own arrow: neither its ring nor its check.
    public bool SendRest => !Pending && !sendCheck;
    // The line keeps its room from Send until the box is next typed in, empty unless it has
    // something to say.
    public bool ShowStatus => StatusText.Length > 0 || Pending || keepRoom;
    // What a screen reader is to hear of the status line now: raised once for each, at most one a
    // second (Receipt); the view raises LiveRegionChanged with it.
    public string Announcement { get; private set; } = "";
    // Whether that is a failure or a refusal, which a screen reader hears at once (assertively);
    // set before Announcement is raised, so the view reads it with the words.
    public bool AnnouncementUrgent { get; private set; }
    // The words the view keeps room for (ReceiptWords.Room).
    public string StatusRoom => ReceiptWords.Room;

    // Another machine's way to files over 256 KiB: the relay's blob store, and its device there.
    public IBlobStore? Blobs { get; init; }
    public string? BlobsFor { get; init; }

    // The most one attachment can be: what the host takes in chunks over this pipe, or through the
    // relay's blob store from another machine, else what a channel takes.
    public long MaxBytes => large ? AttachmentLimits.DirectBytes : Math.Max(blobMax, AttachmentLimits.Bytes);

    // blobMax: the largest file the machine takes through the relay's blob store now, 0 for none.
    public void Update(Session next, bool takesFiles, bool takesLarge = false, long blobMax = 0)
    {
        session = next;
        attach = takesFiles;
        Turns.Seen(next.Status);
        large = takesFiles && takesLarge;
        this.blobMax = takesFiles ? blobMax : 0;
        Refresh();
    }

    // A file too big to read at all (the app does not load gigabytes to find out): refused as
    // AddAsync refuses one over the limit.
    public void Refuse(string? name, string? type, long size) =>
        Error = $"Couldn't attach {Name(name, type ?? "")}: it is {Words.Size(size)}, over the {Words.Size(MaxBytes)} limit for an attachment.";

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
            var max = MaxBytes;
            if (bytes.Length > max)
            {
                var smaller = type.StartsWith("image/", StringComparison.Ordinal) && shrinker is not null ? await shrinker.ShrinkAsync(bytes, AttachmentLimits.Bytes) : null;
                if (smaller is null || smaller.Length > AttachmentLimits.Bytes)
                {
                    Error = $"Couldn't attach {fileName}: it is {Words.Size(bytes.Length)}, over the {Words.Size(max)} limit for an attachment.";
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
        // The host has it from an earlier reply that did not go: it can let it go now, and a blob
        // uploaded for it can go from the store.
        if (file.Offered && host is not null) { _ = DiscardAsync(file.Id); }
        if (file.Blob is { } blob && Blobs is not null) { _ = Blobs.DeleteAsync(blob.Id); }
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
        var entry = deliveries.Submit(Key, Ops.SessionReply, r => Request(r, sentText, files), _ => false, payload: files, before: (call, progress) => UploadAsync(call, files, progress), replayAfterRestart: false);
        sent = (entry.Request, sentText, files);
        Turns.Sent();
        keepRoom = true;
        // Sent inside the last one's check: Send shows this one's ring, not that check.
        checkTimer?.Dispose();
        SetCheck(false);
        Refresh();
    }

    public void Resend()
    {
        if (!ShowResend || deliveries.Get(Key) is not { State: DeliveryState.Failed, Payload: List<ReplyAttachment> files } || sent is null) { return; }
        var sentText = sent.Value.Text;
        // The line's room again, as Send kept it: typing after the failure let it go, and the check
        // of a resend that delivers showed with the line collapsing under it (#625, Copilot 4487937147).
        if (!keepRoom)
        {
            keepRoom = true;
            Raise(nameof(ShowStatus));
        }
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
    async Task UploadAsync(Func<string, object?, TimeSpan?, Task<JsonElement>> call, IReadOnlyList<ReplyAttachment> files, Action<long, long> progress)
    {
        var total = files.Sum(f => f.Size);
        long done = 0;
        foreach (var f in files)
        {
            // Over 256 KiB to a machine whose pipe does not take it: through the blob store.
            if (f.Size > AttachmentLimits.Bytes && !large)
            {
                await ThroughBlobStoreAsync(call, f, n => progress(done + n, total));
                done += f.Size;
                continue;
            }
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

    // As the PWA's sendThroughBlobStore: sealed and uploaded once (reused while its ticket lasts),
    // then attachment.start naming it, again until the machine has fetched, opened and checked it.
    // progress: the first half of the file's size is the upload, the second the machine's fetch.
    async Task ThroughBlobStoreAsync(Func<string, object?, TimeSpan?, Task<JsonElement>> call, ReplyAttachment f, Action<long> progress)
    {
        var half = f.Size / 2;
        var now = deliveries.Time.GetUtcNow();
        if (f.Blob is null || now - f.BlobAt > LargeAttachments.TicketReuse)
        {
            // A relay that is down for now reads as no blob store: PutAsync says that, and the reply
            // waits for it, rather than failing here.
            if (Blobs is not { } blobs || BlobsFor is null)
            {
                throw new Local.HostErrorException($"{f.Name} is over {Words.Size(AttachmentLimits.Bytes)}, and the machine cannot take larger files right now");
            }
            f.Blob = await blobs.PutAsync(BlobsFor, f.Name, f.Bytes, n => progress(n * half / f.Size));
            f.BlobAt = now;
        }
        progress(half);
        var start = new AttachmentStart { Attachment = f.Id, Name = f.Name, Type = f.Type.Length > 0 ? f.Type : null, Size = f.Size, Sha256 = f.Sha256, Blob = f.Blob };
        while (true)
        {
            AttachmentState state;
            try { state = Read(await call(Ops.AttachmentStart, start, null)); }
            catch (Local.HostErrorException)
            {
                // The machine could not use the blob (gone, cut short, not what was sealed): the next
                // try uploads it again.
                f.Blob = null;
                throw;
            }
            progress(half + Math.Min(state.Received, f.Size) * (f.Size - half) / f.Size);
            if (state.Complete) { return; }
            await Task.Delay(BlobPoll);
        }
    }

    // How often to ask whether the machine has fetched a blob yet.
    public TimeSpan BlobPoll { get; init; } = TimeSpan.FromMilliseconds(400);

    // Read as the spec says (v1-sessions.md "Malformed records"): a field the app cannot read is
    // absent, so an unreadable count reads as 0 received and the file is sent again from the start.
    static AttachmentState Read(JsonElement e) => e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        ? throw new Local.HostErrorException("the machine's answer about the attachment was empty")
        : Received.Read<AttachmentState>(e);

    public void Refresh()
    {
        var e = deliveries.Get(Key);
        if (e is { State: DeliveryState.Done } && sent is { } s && s.Request == e.Request && handled != e.Request)
        {
            handled = e.Request;
            if (e.Outcome == "delivered")
            {
                // What went comes out of the box, and only that: anything typed meanwhile stays.
                settingText = true;
                try { if (text.StartsWith(s.Text, StringComparison.Ordinal)) { Text = text[s.Text.Length..].TrimStart(); } }
                finally { settingText = false; }
                foreach (var f in s.Files) { Attachments.Remove(f); }
                ShowCheck();
            }
            else if (e.Outcome == "unconfirmed")
            {
                // The host has let them go with the reply (and deleted any blob): sent again, they are new.
                foreach (var f in s.Files) { f.Id = Guid.NewGuid().ToString(); f.Offered = false; f.Blob = null; }
            }
            sent = null;
        }
        if (e is { State: DeliveryState.Done, Outcome: "busy" } && !pausedAfterBusy && handled == e.Request && e.Request != busyFor)
        {
            var request = e.Request;
            busyFor = request;
            pausedAfterBusy = true;
            _ = Task.Delay(2000).ContinueWith(_ => deliveries.Ui.Post(() => { pausedAfterBusy = false; busyGone = request; Refresh(); }), TaskScheduler.Default);
        }
        // A busy refusal is a moment's news: once the pause has passed, the line goes (#1193).
        receipt.Show(e is { State: DeliveryState.Done, Outcome: "busy" } && e.Request == busyGone ? null : e, preparing > 0 && !Pending ? new ReceiptLine("reading", "Reading the attachment…", null, Tone.Progress, ReceiptMark.Ring, null) : null);
        Raise(nameof(Held));
        Raise(nameof(HeldText));
        Raise(nameof(Pending));
        Raise(nameof(SendRest));
        Raise(nameof(SendStill));
        Raise(nameof(ShowStatus));
        Raise(nameof(CanSend));
        Raise(nameof(SendEnabled));
        Raise(nameof(CanAttach));
        Raise(nameof(ShowAttach));
        Raise(nameof(ShowResend));
    }

    string? busyFor;
    string? busyGone;

    // What a reply's progress or outcome says: the receipt under the box (Receipt; ui.md §6.5,
    // decided on #617). Its files going; then nothing on the line through the stages and a
    // delivery (Send's ring, then its check, say those), words for a stall or a wait; any other
    // outcome keeps its words. stalled: whether the state it is in has stalled (Receipt).
    ReceiptLine Describe(Delivery e, bool stalled)
    {
        static ReceiptLine Said(string key, string text, Tone tone) => new(key, text, null, tone, ReceiptMark.None, $"Reply: {text}");
        switch (e.State)
        {
            case DeliveryState.Sending when e.Progress is { Total: > 0 } p && p.Sent < p.Total:
                var count = (e.Payload as List<ReplyAttachment>)?.Count ?? 1;
                var what = $"Sending {(count == 1 ? "the attachment" : $"{count} attachments")}…";
                // Said once, not at every percent.
                return new("upload", $"{what} {100 * p.Sent / p.Total}%", null, Tone.Progress, ReceiptMark.Ring, $"Reply: {what}");
            case DeliveryState.Sending or DeliveryState.Waiting:
                return ReceiptWords.InFlight(answer: false, e, machineName, stalled);
            case DeliveryState.Failed:
                return Said("failed", $"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad);
        }
        var outcome = e.Outcome is { } o && OutcomeText.ContainsKey(o) ? o : "unconfirmed";
        if (outcome == "delivered") { return new("delivered", "", null, Tone.None, ReceiptMark.Check, "Reply delivered"); }
        var t = OutcomeText[outcome];
        if (outcome == "refused" && e.Reason is { } r) { t = $"The machine refused this reply: {r}."; }
        if (outcome == "unconfirmed" && e.Reason is { } u) { t += $" ({u})"; }
        return Said(outcome, t, outcome == "refused" ? Tone.Bad : Tone.Warn);
    }
}
