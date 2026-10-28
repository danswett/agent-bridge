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
}
