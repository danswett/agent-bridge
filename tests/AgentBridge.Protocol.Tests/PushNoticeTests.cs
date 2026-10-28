using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

// The encrypted notification payload (docs/protocol/v1-sessions.md, Notifications).
public class PushNoticeTests
{
    static readonly DateTimeOffset At = new(2026, 10, 28, 4, 22, 43, 182, TimeSpan.Zero);
    const string Machine = "53d1997f0a1b2c3d4e5f60718293a4b5";

    static PushMessage Message(string title, string body, NoticeKind kind = NoticeKind.Question) => new()
    {
        Notification = new PushNotification
        {
            Title = title,
            Body = body,
            Navigate = PushNotice.Navigate("https://danswett.github.io/agent-bridge/app/", Machine, "s1", kind == NoticeKind.Finished ? null : "q1"),
            Tag = PushNotice.Tag(Machine, "s1"),
            Silent = kind == NoticeKind.Resolved ? true : null,
            Data = new PushNoticeData { Kind = kind, Machine = Machine, Session = "s1", Question = kind == NoticeKind.Finished ? null : "q1", At = At },
        },
    };

    [Fact]
    public void A_notice_is_a_declarative_web_push_message_carrying_the_machine_session_and_question()
    {
        var json = JsonDocument.Parse(PushNotice.Serialize(PushNotice.Fit(Message("Fix the login", "DEV · waiting on you\nWhich one?")))).RootElement;

        Assert.Equal(8030, json.GetProperty("web_push").GetInt32());
        var n = json.GetProperty("notification");
        Assert.Equal("Fix the login", n.GetProperty("title").GetString());
        Assert.Equal("DEV · waiting on you\nWhich one?", n.GetProperty("body").GetString());
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&session=s1&question=q1", n.GetProperty("navigate").GetString());
        Assert.Equal($"{Machine}:s1", n.GetProperty("tag").GetString());
        Assert.Equal("en", n.GetProperty("lang").GetString());
        Assert.False(n.TryGetProperty("silent", out _));
        var d = n.GetProperty("data");
        Assert.Equal((1, "question", Machine, "s1", "q1", "2026-10-28T04:22:43.182Z"),
            (d.GetProperty("v").GetInt32(), d.GetProperty("kind").GetString(), d.GetProperty("machine").GetString(), d.GetProperty("session").GetString(),
             d.GetProperty("question").GetString(), d.GetProperty("at").GetString()));
    }

    [Fact]
    public void A_resolved_notice_is_silent_and_an_unknown_kind_reads_as_a_question()
    {
        var resolved = JsonDocument.Parse(PushNotice.Serialize(Message("t", "b", NoticeKind.Resolved))).RootElement.GetProperty("notification");
        Assert.True(resolved.GetProperty("silent").GetBoolean());
        Assert.Equal("resolved", resolved.GetProperty("data").GetProperty("kind").GetString());

        var later = JsonSerializer.Deserialize<PushNoticeData>("""{"kind":"reminder","machine":"m","session":"s","at":"2026-10-28T04:22:43.182Z"}""", ProtocolJson.Options)!;
        Assert.Equal(NoticeKind.Question, later.Kind);
    }

    [Fact]
    public void Any_notice_fits_the_payload_limit_and_is_never_cut_mid_character()
    {
        // Emoji are 4 bytes of text and 12 of JSON: 10,000 of them as the body, 500 as the
        // title, is far past every limit however it is counted.
        var emoji = string.Concat(Enumerable.Repeat("😀", 10_000));
        var fitted = PushNotice.Fit(Message(emoji[..1000], emoji));

        var bytes = PushNotice.Serialize(fitted);
        Assert.True(bytes.Length <= PushNotice.MaxBytes, $"{bytes.Length} bytes");
        foreach (var text in new[] { fitted.Notification.Title, fitted.Notification.Body })
        {
            Assert.EndsWith("…", text);
            Assert.True(Encoding.UTF8.GetByteCount(text) > 0);
            // A lone surrogate would not survive a round trip through UTF-8.
            Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
        }
        Assert.Equal(fitted, JsonSerializer.Deserialize<PushMessage>(bytes, ProtocolJson.Options));
    }

    [Fact]
    public void A_short_notice_is_sent_as_it_is()
    {
        var m = Message("Fix the login", "DEV · finished\nDone.", NoticeKind.Finished);
        Assert.Equal(m, PushNotice.Fit(m));
    }

    [Fact]
    public void Each_session_has_one_topic_a_push_service_accepts_and_one_tag()
    {
        var a = PushNotice.Topic(Machine, "s1");
        Assert.Equal(a, PushNotice.Topic(Machine, "s1"));
        Assert.NotEqual(a, PushNotice.Topic(Machine, "s2"));
        Assert.NotEqual(a, PushNotice.Topic("another-machine", "s1"));
        // RFC 8030: at most 32 base64url characters; Apple: a length base64 can have.
        Assert.Equal(32, a.Length);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"'{c}'"));
        Assert.Equal(PushNotice.Tag(Machine, "s1"), PushNotice.Tag(Machine, "s1"));
    }

    [Fact]
    public void A_tap_goes_to_the_app_with_the_machine_session_and_question_escaped()
    {
        Assert.Equal("https://x.example/app/?v=2&host=m&session=a%20b&question=q%26", PushNotice.Navigate("https://x.example/app/?v=2#top", "m", "a b", "q&"));
        Assert.Equal("https://x.example/app/?host=m&session=s", PushNotice.Navigate("https://x.example/app/", "m", "s", null));
    }

    [Fact]
    public void Whitespace_is_flattened_for_a_lock_screen()
    {
        Assert.Equal("Which one? a b", PushNotice.Flatten("  Which one?\n\n  a\tb \r\n"));
        Assert.Equal("", PushNotice.Flatten(null));
    }
}
