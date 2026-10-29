using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Attachments from their first chunk to the staged file Copilot is given, and how the line
// that mentions them is built and confirmed.
public sealed class AttachmentStoreTests : IDisposable
{
    const string Phone = "phone-device";
    const string Laptop = "laptop-device";
    static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Fixture.T0);
    readonly AttachmentStore store;

    public AttachmentStoreTests() => store = New();

    public void Dispose()
    {
        store.Dispose();
        f.Dispose();
    }

    string Staging => Path.Combine(f.Root, "profile", "attachments");

    AttachmentStore New(int maxHeld = 32) => new(new AttachmentOptions { StagingRoot = Staging, Time = time, MaxHeld = maxHeld }, sweep: false);

    static byte[] Png(int size)
    {
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        PngHeader.CopyTo(bytes, 0);
        return bytes;
    }

    static AttachmentStart Start(byte[] bytes, string name = "screenshot.png", string? id = null) => new()
    {
        Attachment = id ?? Guid.NewGuid().ToString(),
        Name = name,
        Type = "image/png",
        Size = bytes.Length,
        Sha256 = Attachments.Base64Url(SHA256.HashData(bytes)),
    };

    static AttachmentChunk Chunk(AttachmentStart start, byte[] bytes, long offset, int length) => new()
    {
        Attachment = start.Attachment,
        Offset = offset,
        Data = Convert.ToBase64String(bytes, (int)offset, length),
    };

    // Sends every chunk, as a client does: one at a time, each from where the host says it is.
    AttachmentStart Upload(byte[] bytes, string name = "screenshot.png", string peer = Phone)
    {
        var start = Start(bytes, name);
        var state = store.Start(peer, start);
        while (!state.Complete)
        {
            var n = (int)Math.Min(AttachmentLimits.Chunk, bytes.Length - state.Received);
            state = store.Chunk(peer, Chunk(start, bytes, state.Received, n));
        }
        return start;
    }

    static IReadOnlyList<AttachmentRef> Refs(params AttachmentStart[] starts) => [.. starts.Select(s => new AttachmentRef { Id = s.Attachment })];

    [Fact]
    public void A_file_arrives_chunk_by_chunk_in_order_and_is_complete_only_when_its_bytes_match_its_sha256()
    {
        var bytes = Png(AttachmentLimits.Bytes);
        var start = Start(bytes);

        Assert.Equal(new AttachmentState(0, false), store.Start(Phone, start));
        Assert.Equal(new AttachmentState(AttachmentLimits.Chunk, false), store.Chunk(Phone, Chunk(start, bytes, 0, AttachmentLimits.Chunk)));
        // A chunk sent again after its response was lost is one the host has: taken once.
        Assert.Equal(new AttachmentState(AttachmentLimits.Chunk, false), store.Chunk(Phone, Chunk(start, bytes, 0, AttachmentLimits.Chunk)));
        // One that skips ahead says where to resume.
        Assert.Equal("expected the chunk at offset 11264", Assert.Throws<ArgumentException>(() => store.Chunk(Phone, Chunk(start, bytes, 2 * AttachmentLimits.Chunk, 10))).Message);
        // Starting again on a new channel says the same, so the client carries on from there.
        Assert.Equal(new AttachmentState(AttachmentLimits.Chunk, false), store.Start(Phone, start));

        var state = new AttachmentState(AttachmentLimits.Chunk, false);
        while (!state.Complete) { state = store.Chunk(Phone, Chunk(start, bytes, state.Received, (int)Math.Min(AttachmentLimits.Chunk, bytes.Length - state.Received))); }
        Assert.Equal(new AttachmentState(bytes.Length, true), state);
    }

    [Fact]
    public void Bytes_that_do_not_match_the_sha256_drop_the_attachment()
    {
        var bytes = Png(100);
        var start = Start(bytes) with { Sha256 = Attachments.Base64Url(SHA256.HashData(Png(100))) };
        store.Start(Phone, start);

        var e = Assert.Throws<ArgumentException>(() => store.Chunk(Phone, Chunk(start, bytes, 0, 100)));

        Assert.Equal("the attachment's bytes do not match its sha256, so it was dropped; send it again", e.Message);
        Assert.Equal(0, store.Count);
        Assert.Contains("is not on the host", store.Claim(Phone, Refs(start)).Problem);
    }

    [Fact]
    public void An_attachment_is_the_sending_devices_alone_to_add_to_use_or_remove()
    {
        var start = Upload(Png(100));

        Assert.Contains("is not on the host", Assert.Throws<InvalidOperationException>(() => store.Chunk(Laptop, Chunk(start, Png(100), 0, 10))).Message);
        Assert.Equal("that attachment id is taken; use a new one", Assert.Throws<ArgumentException>(() => store.Start(Laptop, start)).Message);
        Assert.Contains("is not on the host", store.Claim(Laptop, Refs(start)).Problem);
        Assert.False(store.Discard(Laptop, start.Attachment));

        Assert.True(store.Discard(Phone, start.Attachment));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Starting_an_id_again_with_other_details_or_beyond_the_hosts_limit_fails()
    {
        using var small = New(maxHeld: 2);
        var first = Start(Png(10));
        small.Start(Phone, first);
        Assert.Equal("that attachment was started with a different name, size or sha256", Assert.Throws<ArgumentException>(() => small.Start(Phone, first with { Name = "other.png" })).Message);
        small.Start(Phone, Start(Png(10)));
        Assert.Equal("the host is already holding 2 attachments; send or remove some first", Assert.Throws<InvalidOperationException>(() => small.Start(Phone, Start(Png(10)))).Message);
        Assert.Equal("the attachment is larger than 256 KiB", Assert.Throws<ArgumentException>(() => small.Start(Phone, Start(Png(10)) with { Size = AttachmentLimits.Bytes + 1 })).Message);
    }

    [Fact]
    public void An_attachment_no_reply_uses_within_15_minutes_is_dropped_but_one_still_arriving_is_kept_alive()
    {
        var done = Upload(Png(100));
        var bytes = Png(3 * AttachmentLimits.Chunk);
        var arriving = Start(bytes);
        store.Start(Phone, arriving);

        time.Advance(TimeSpan.FromMinutes(10));
        store.Chunk(Phone, Chunk(arriving, bytes, 0, AttachmentLimits.Chunk));
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Contains("is not on the host", store.Claim(Phone, Refs(done)).Problem);
        Assert.Equal(new AttachmentState(AttachmentLimits.Chunk, false), store.Start(Phone, arriving));
        Assert.Contains("has not finished arriving", store.Claim(Phone, Refs(arriving)).Problem);
    }

    [Fact]
    public void Staged_files_go_in_a_folder_only_this_user_can_open_named_safely_with_the_extension_their_bytes_call_for()
    {
        var shot = Upload(Png(5000), "C:\\Users\\x\\..\\My Screen shot (2).PNG");
        var pasted = Upload(Png(10), "image");
        var notes = Upload("# notes"u8.ToArray(), "../notes v2.md");
        var (claim, problem) = store.Claim(Phone, Refs(shot, pasted, notes));
        Assert.Null(problem);

        var paths = store.Stage(claim!);

        Assert.Equal(3, paths.Count);
        Assert.All(paths, p => Assert.Equal(Path.GetFullPath(Staging), Path.GetDirectoryName(p)));
        Assert.Matches("^[0-9a-f]{32}-My-Screen-shot--2\\.png$", Path.GetFileName(paths[0]));
        Assert.Matches("^[0-9a-f]{32}-image\\.png$", Path.GetFileName(paths[1]));
        Assert.Matches("^[0-9a-f]{32}-notes-v2\\.md$", Path.GetFileName(paths[2]));
        Assert.Equal("# notes"u8.ToArray(), File.ReadAllBytes(paths[2]));
        Assert.All(paths, p => Assert.DoesNotContain(' ', p[Staging.Length..]));
        Assert.True(PrivateFolder.IsPrivate(Staging));
        if (OperatingSystem.IsWindows()) { AssertOnlyMe(Staging); }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Staging));
            foreach (var p in paths) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(p)); }
        }
    }

    [SupportedOSPlatform("windows")]
    static void AssertOnlyMe(string folder)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "inherits access from the folder above");
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal(me, r.IdentityReference));
        Assert.Contains(rules, r => r.FileSystemRights.HasFlag(FileSystemRights.FullControl)
            && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
    }

    [Fact]
    public void A_folder_someone_else_could_read_is_narrowed_before_anything_is_staged_in_it()
    {
        Directory.CreateDirectory(Staging);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(Staging, (UnixFileMode)0b111_101_101); }
        Assert.False(PrivateFolder.IsPrivate(Staging));

        var (claim, _) = store.Claim(Phone, Refs(Upload(Png(10))));
        store.Stage(claim!);

        Assert.True(PrivateFolder.IsPrivate(Staging));
    }

    [Fact]
    public void A_name_that_claims_an_image_or_PDF_its_bytes_are_not_is_refused_and_nothing_is_staged()
    {
        var fake = Upload("not really a picture"u8.ToArray(), "cat.jpg");
        var real = Upload(Png(10));
        var (claim, _) = store.Claim(Phone, Refs(real, fake));

        var e = Assert.Throws<InvalidDataException>(() => store.Stage(claim!));

        Assert.Equal("a file named .jpg that is not a JPEG image is not sent; attach it under another name", e.Message);
        Assert.False(Directory.Exists(Staging) && Directory.EnumerateFiles(Staging).Any());
    }

    [Fact]
    public void A_reply_that_types_nothing_deletes_its_staged_copies_and_leaves_the_attachments_for_another_try()
    {
        var start = Upload(Png(10));
        var (claim, _) = store.Claim(Phone, Refs(start));
        // While a reply holds it, nobody can remove it or send it with another.
        Assert.False(store.Discard(Phone, start.Attachment));
        Assert.Contains("is being sent with another reply", store.Claim(Phone, Refs(start)).Problem);
        var path = Assert.Single(store.Stage(claim!));

        store.Release(claim!);

        Assert.False(File.Exists(path));
        var (again, problem) = store.Claim(Phone, Refs(start));
        Assert.Null(problem);
        Assert.Equal(1, again!.Count);
    }

    [Fact]
    public void A_typed_reply_takes_its_attachments_out_of_memory_and_leaves_the_staged_copy_for_the_sweep()
    {
        var start = Upload(Png(10));
        var (claim, _) = store.Claim(Phone, Refs(start));
        var path = Assert.Single(store.Stage(claim!));

        store.Consume(claim!);

        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(path));
        // A client resending after a lost response is told it is all there, and sends nothing more.
        Assert.Equal(new AttachmentState(10, true), store.Start(Phone, start));
        Assert.Equal("attachment " + start.Attachment[..8] + " went with an earlier reply; attach it again", store.Claim(Phone, Refs(start)).Problem);

        File.SetLastWriteTimeUtc(path, (time.GetUtcNow() - TimeSpan.FromHours(23)).UtcDateTime);
        store.Sweep();
        Assert.True(File.Exists(path));
        File.SetLastWriteTimeUtc(path, (time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime);
        store.Sweep();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_sweep_deletes_only_old_files_it_staged_and_nothing_through_a_linked_folder()
    {
        var old = (time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime;
        string Old(string folder, string name)
        {
            Directory.CreateDirectory(folder);
            var p = Path.Combine(folder, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, old);
            return p;
        }
        var ours = Old(Staging, "0123456789abcdef0123456789abcdef-shot.png");
        var stranger = Old(Staging, "notes.txt");

        store.Sweep();

        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(stranger));

        // The folder replaced by a link to somewhere else: nothing there was staged here.
        var elsewhere = Path.Combine(f.Root, "elsewhere");
        var theirs = Old(elsewhere, "0123456789abcdef0123456789abcdef-report.pdf");
        Directory.Delete(Staging, recursive: true);
        Link(Staging, elsewhere);

        store.Sweep();

        Assert.True(File.Exists(theirs));
        Assert.Throws<IOException>(() => PrivateFolder.Ensure(Staging));
    }

    // A directory link any user can make: a junction on Windows, a symbolic link elsewhere.
    static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "the junction was not made");
    }

    [Theory]
    [InlineData("photo.HEIC", "jpeg", "photo.jpg")]
    [InlineData("scan", "pdf", "scan.pdf")]
    [InlineData("anim.gif", "gif", "anim.gif")]
    [InlineData("a.jpeg", "jpeg", "a.jpeg")]
    [InlineData("IMG_0001.heic", "heic", "IMG_0001.heic")]
    [InlineData("pic.webp", "webp", "pic.webp")]
    [InlineData("report.tar.gz", "text", "report.tar.gz")]
    [InlineData("...", "text", "attachment")]
    [InlineData("weird.ext with space", "text", "weird")]
    [InlineData("名前.txt", "text", "attachment.txt")]
    public void A_staged_name_keeps_a_safe_stem_and_takes_the_extension_the_bytes_call_for(string given, string kind, string expected)
    {
        byte[] bytes = kind switch
        {
            "jpeg" => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2],
            "pdf" => "%PDF-1.7\n"u8.ToArray(),
            "gif" => "GIF89a...."u8.ToArray(),
            "heic" => [0, 0, 0, 24, .. "ftypheic"u8],
            "webp" => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8],
            _ => "plain text"u8.ToArray(),
        };
        Assert.Equal((expected, (string?)null), AttachmentFiles.Name(given, bytes));
    }

    [Fact]
    public void The_line_typed_mentions_each_file_with_an_at_sign_then_the_words_and_refuses_a_path_with_a_space()
    {
        Assert.Equal(@"@C:\s\a.png @C:\s\b.md look at these", CopilotReply.WithAttachments("  look at\nthese ", [@"C:\s\a.png", @"C:\s\b.md"]));
        Assert.Equal(@"@C:\s\a.png", CopilotReply.WithAttachments(" \n", [@"C:\s\a.png"]));
        Assert.Null(CopilotReply.WithAttachments("hi", [@"C:\Users\Dan Smith\a.png"]));

        Assert.True(CopilotReply.Attached([@"C:\s\a.png"], [@"c:\S\A.png", @"C:\other.txt"]));
        Assert.False(CopilotReply.Attached([@"C:\s\a.png", @"C:\s\b.md"], [@"C:\s\a.png"]));
        Assert.True(CopilotReply.Attached([], null));
    }

    [Fact]
    public void A_recorded_prompt_carries_the_paths_copilot_attached_to_it()
    {
        var events = f.CopilotSession("33333333-3333-4333-8333-333333333333", 100);
        // As Copilot CLI 1.x writes one (seen in agent-ha-bridge's replies with attachments).
        Fixture.Append(events, Copilot.Event("user.message", new
        {
            content = @"@C:\s\a.png see",
            attachments = new[] { new { type = "file", path = @"C:\s\a.png", displayName = @"@C:\s\a.png", mentionIndex = 0 } },
            delivery = "idle",
        }, 1));

        var prompt = Assert.Single(CopilotPrompts.Since(events, 0).Prompts);

        Assert.Equal([@"C:\s\a.png"], prompt.Attachments);
    }

    // ---- large files, from the blob store ----

    // Plays the relay's blob store: the file each blob opens to, or a failure, and the blobs
    // deleted once fetched.
    sealed class FakeBlobs : IBlobSource
    {
        public long MaxBytes { get; set; } = AttachmentLimits.BlobBytes;
        public readonly Dictionary<string, byte[]> Files = [];
        public Exception? Failure;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold;
        public readonly List<string> Forgotten = [];

        public async Task FetchAsync(BlobRef blob, long size, Stream output, Action<long> progress, CancellationToken cancellationToken)
        {
            if (Hold) { await Release.Task.WaitAsync(cancellationToken); }
            if (Failure is { } e) { throw e; }
            var bytes = Files[blob.Id];
            await output.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), cancellationToken);
            progress(bytes.Length / 2);
            await output.WriteAsync(bytes.AsMemory(bytes.Length / 2), cancellationToken);
            progress(bytes.Length);
        }

        public Task ForgetAsync(BlobRef blob)
        {
            lock (Forgotten) { Forgotten.Add(blob.Id); }
            return Task.CompletedTask;
        }
    }

    AttachmentStore WithBlobs(FakeBlobs blobs) => new(new AttachmentOptions { StagingRoot = Staging, Time = time }, sweep: false, blobs: blobs);

    static AttachmentStart FromBlob(byte[] bytes, FakeBlobs blobs, string name = "IMG_0001.png", byte[]? contents = null)
    {
        var id = Guid.NewGuid().ToString("N");
        blobs.Files[id] = contents ?? bytes;
        return Start(bytes, name) with { Blob = new BlobRef { Id = id, Key = Attachments.Base64Url(RandomNumberGenerator.GetBytes(32)) } };
    }

    // As a client watches a fetch: attachment.start again until the host has it all.
    static async Task<AttachmentState> FetchedAsync(AttachmentStore s, AttachmentStart start, string peer = Phone)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var state = s.Start(peer, start);
            if (state.Complete) { return state; }
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("the fetch never finished"); }
            await Task.Delay(10);
        }
    }

    string Incoming => Path.Combine(Staging, "incoming");

    static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException($"timed out waiting for {what}"); }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_file_from_the_blob_store_is_fetched_into_a_private_folder_staged_like_any_other_and_deleted_from_the_store()
    {
        var blobs = new FakeBlobs();
        using var s = WithBlobs(blobs);
        var photo = Png(3 * 1024 * 1024 + 7);
        var start = FromBlob(photo, blobs, "IMG 0001.png");

        Assert.Equal(new AttachmentState(0, false), s.Start(Phone, start));
        Assert.Equal(new AttachmentState(photo.Length, true), await FetchedAsync(s, start));
        var fetched = Assert.Single(Directory.GetFiles(Incoming));
        Assert.True(PrivateFolder.IsPrivate(Incoming));
        await UntilAsync(() => { lock (blobs.Forgotten) { return blobs.Forgotten.Count == 1; } }, "the blob to be deleted from the store");
        Assert.Equal([start.Blob!.Id], blobs.Forgotten);

        var (claim, problem) = s.Claim(Phone, Refs(start));
        Assert.Null(problem);
        var staged = Assert.Single(s.Stage(claim!));
        Assert.Matches("^[0-9a-f]{32}-IMG-0001\\.png$", Path.GetFileName(staged));
        Assert.Equal(photo, File.ReadAllBytes(staged));
        if (!OperatingSystem.IsWindows()) { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged)); }

        s.Consume(claim!);
        Assert.False(File.Exists(fetched));
        Assert.True(File.Exists(staged));
        Assert.Equal(new AttachmentState(photo.Length, true), s.Start(Phone, start));
    }

    [Fact]
    public async Task A_fetched_file_that_does_not_match_its_sha256_is_refused_and_deleted_everywhere()
    {
        var blobs = new FakeBlobs();
        using var s = WithBlobs(blobs);
        var start = FromBlob(Png(500_000), blobs, contents: Png(500_000));

        s.Start(Phone, start);
        var e = await WaitForFailureAsync(s, start);

        Assert.Equal("the attachment's bytes do not match its sha256, so it was dropped; send it again", e.Message);
        Assert.Empty(Directory.GetFiles(Incoming));
        await UntilAsync(() => { lock (blobs.Forgotten) { return blobs.Forgotten.Count == 1; } }, "the blob to be deleted from the store");
        Assert.Equal([start.Blob!.Id], blobs.Forgotten);
        Assert.Contains("is not on the host", s.Claim(Phone, Refs(start)).Problem);
    }

    [Fact]
    public async Task A_blob_that_is_not_what_was_sealed_is_refused_with_the_reason_and_one_out_of_reach_is_kept_for_another_try()
    {
        var blobs = new FakeBlobs { Failure = new InvalidDataException("a part of the blob was changed, moved or is not from this blob") };
        using var s = WithBlobs(blobs);
        var tampered = FromBlob(Png(1000), blobs);
        s.Start(Phone, tampered);
        Assert.Equal("the attachment in the blob store is not what was sent (a part of the blob was changed, moved or is not from this blob), so it was dropped; send it again",
            (await WaitForFailureAsync(s, tampered)).Message);
        await UntilAsync(() => { lock (blobs.Forgotten) { return blobs.Forgotten.Count == 1; } }, "the blob to be deleted from the store");
        Assert.Equal([tampered.Blob!.Id], blobs.Forgotten);

        blobs.Failure = new HttpRequestException("storage answered 503 to the download");
        var unreachable = FromBlob(Png(1000), blobs);
        s.Start(Phone, unreachable);
        Assert.Contains("could not fetch the attachment from the blob store (storage answered 503", (await WaitForFailureAsync(s, unreachable)).Message);
        Assert.DoesNotContain(unreachable.Blob!.Id, blobs.Forgotten);
    }

    static async Task<Exception> WaitForFailureAsync(AttachmentStore s, AttachmentStart start)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try { s.Start(Phone, start); }
            catch (InvalidOperationException e) { return e; }
            await Task.Delay(10);
        }
        throw new TimeoutException("the fetch never failed");
    }

    [Fact]
    public void A_large_file_needs_a_blob_store_and_must_fit_what_it_and_the_protocol_allow()
    {
        var blobs = new FakeBlobs { MaxBytes = 0 };
        using var s = WithBlobs(blobs);
        var start = FromBlob(Png(1000), blobs);

        Assert.Equal("this host cannot fetch from the blob store now: it is not connected to a relay that has one", Assert.Throws<InvalidOperationException>(() => s.Start(Phone, start)).Message);
        Assert.Equal("this host cannot fetch from the blob store now: it is not connected to a relay that has one", Assert.Throws<InvalidOperationException>(() => store.Start(Phone, start)).Message);
        blobs.MaxBytes = 1024 * 1024;
        Assert.Equal("the attachment is larger than the 1 MiB this host can fetch", Assert.Throws<ArgumentException>(() => s.Start(Phone, start with { Size = 2 * 1024 * 1024 })).Message);
        Assert.Equal("the attachment is larger than 100 MiB", Assert.Throws<ArgumentException>(() => s.Start(Phone, start with { Size = AttachmentLimits.BlobBytes + 1 })).Message);
        Assert.Equal("blob.key must be 32 bytes, base64url without padding", Assert.Throws<ArgumentException>(() => s.Start(Phone, start with { Blob = start.Blob! with { Key = "short" } })).Message);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public async Task A_large_file_is_the_sending_devices_alone_and_removing_it_stops_its_fetch_and_deletes_what_arrived()
    {
        var blobs = new FakeBlobs { Hold = true };
        using var s = WithBlobs(blobs);
        var start = FromBlob(Png(2000), blobs);
        s.Start(Phone, start);
        await UntilAsync(() => Directory.Exists(Incoming) && Directory.GetFiles(Incoming).Length == 1, "the fetch to start");

        Assert.Contains("is not on the host", s.Claim(Laptop, Refs(start)).Problem);
        Assert.Contains("has not finished arriving", s.Claim(Phone, Refs(start)).Problem);
        Assert.False(s.Discard(Laptop, start.Attachment));
        Assert.True(s.Discard(Phone, start.Attachment));

        await UntilAsync(() => Directory.GetFiles(Incoming).Length == 0, "the fetched file to be deleted");
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void The_sweep_deletes_fetched_files_no_attachment_is_waiting_for_and_nothing_else()
    {
        Directory.CreateDirectory(Incoming);
        var orphan = Path.Combine(Incoming, "0123456789abcdef0123456789abcdef.part");
        var stranger = Path.Combine(Incoming, "notes.txt");
        File.WriteAllText(orphan, "x");
        File.WriteAllText(stranger, "x");

        store.Sweep();

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void The_sweep_never_deletes_the_file_of_a_large_attachment_that_started_while_it_ran()
    {
        var bytes = Png(AttachmentLimits.Bytes + 1);
        var late = Start(bytes, "late.png");
        AttachmentStore? s = null;
        // The sweep logs between its staged and incoming passes: an upload starts and finishes
        // right there, leaving its file closed (an open one could not be deleted on Windows).
        s = new AttachmentStore(new AttachmentOptions { StagingRoot = Staging, Time = time }, log: line =>
        {
            if (!line.StartsWith("removed 1 staged", StringComparison.Ordinal)) { return; }
            var state = s!.Start(Phone, late, direct: true);
            while (!state.Complete) { state = s.Chunk(Phone, Chunk(late, bytes, state.Received, (int)Math.Min(AttachmentLimits.Chunk, bytes.Length - state.Received))); }
        }, sweep: false);
        using var _ = s;
        Directory.CreateDirectory(Staging);
        var old = Path.Combine(Staging, "0123456789abcdef0123456789abcdef-shot.png");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, (time.GetUtcNow() - TimeSpan.FromHours(25)).UtcDateTime);

        s.Sweep();

        Assert.False(File.Exists(old));
        Assert.Single(Directory.GetFiles(Incoming));
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(s.Stage(s.Claim(Phone, Refs(late)).Claim!))));
    }

    // As the app on this machine sends one over its pipe: chunk by chunk, past 256 KiB.
    AttachmentStart UploadDirect(AttachmentStore s, byte[] bytes, AttachmentStart? given = null)
    {
        var start = given ?? Start(bytes, "report.png");
        var state = s.Start(Phone, start, direct: true);
        while (!state.Complete)
        {
            var n = (int)Math.Min(AttachmentLimits.Chunk, bytes.Length - state.Received);
            state = s.Chunk(Phone, Chunk(start, bytes, state.Received, n));
        }
        return start;
    }

    [Fact]
    public void A_file_over_256_KiB_from_a_direct_channel_is_written_to_the_private_folder_as_it_arrives_and_staged_from_there()
    {
        var bytes = Png(AttachmentLimits.Bytes * 3 + 5);
        var start = Start(bytes, "report.png");

        Assert.Equal("the attachment is larger than 256 KiB", Assert.Throws<ArgumentException>(() => store.Start(Phone, start)).Message);
        Assert.Equal("the attachment is larger than 100 MiB", Assert.Throws<ArgumentException>(() => store.Start(Phone, start with { Size = AttachmentLimits.DirectBytes + 1 }, direct: true)).Message);
        store.Start(Phone, start, direct: true);
        store.Chunk(Phone, Chunk(start, bytes, 0, AttachmentLimits.Chunk));
        var part = Assert.Single(Directory.GetFiles(Incoming));
        Assert.Equal(AttachmentLimits.Chunk, new FileInfo(part).Length);
        if (OperatingSystem.IsWindows()) { AssertOnlyMe(Incoming); }
        Assert.True(store.Discard(Phone, start.Attachment));
        Assert.Empty(Directory.GetFiles(Incoming));

        var again = UploadDirect(store, bytes);
        var claim = store.Claim(Phone, Refs(again)).Claim!;
        var staged = Assert.Single(store.Stage(claim));
        Assert.Equal(bytes, File.ReadAllBytes(staged));
        store.Consume(claim);
        Assert.Empty(Directory.GetFiles(Incoming));
    }

    [Fact]
    public void A_direct_file_that_does_not_match_its_sha256_is_dropped_with_what_was_written_and_large_files_share_one_budget_on_disk()
    {
        var bytes = Png(AttachmentLimits.Bytes + 1);
        var wrong = Start(bytes, "report.png") with { Sha256 = Attachments.Base64Url(SHA256.HashData(Png(10))) };

        var e = Assert.Throws<ArgumentException>(() => UploadDirect(store, bytes, wrong));

        Assert.Equal("the attachment's bytes do not match its sha256, so it was dropped; send it again", e.Message);
        Assert.Equal(0, store.Count);
        Assert.Empty(Directory.GetFiles(Incoming));

        using var small = new AttachmentStore(new AttachmentOptions { StagingRoot = Staging, Time = time, MaxHeldBlobBytes = 2L * bytes.Length - 1 }, sweep: false);
        small.Start(Phone, Start(bytes, "a.png"), direct: true);
        Assert.StartsWith("the host is already holding 0 MiB of large attachments", Assert.Throws<InvalidOperationException>(() => small.Start(Phone, Start(bytes, "b.png"), direct: true)).Message);
        // Ones small enough for memory do not count against it.
        small.Start(Phone, Start(Png(100), "c.png"), direct: true);
        Assert.Equal(2, small.Count);
    }
}
