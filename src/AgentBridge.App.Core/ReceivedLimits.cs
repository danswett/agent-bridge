using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The protocol's limits on the text a session carries (Limits, docs/protocol/v1-sessions.md),
// held to on receipt as a host is held to them on sending. A host that is broken, remote or the
// demo can send more, and WinUI's text layout fails on a long enough word in a RichTextBlock
// (#493), so what the app draws is never more than a conforming host could send. Cut as a host
// cuts: a response keeps its end and says so, which shows "Earlier text not shown"; a question's
// message keeps its start and the question is marked truncated, so it is answered at the machine
// rather than from a part of what it asks.
public static class ReceivedLimits
{
    public static Session Fit(Session s)
    {
        var activity = Fit(s.Activity);
        var question = s.Question is { } q && Limits.KeepStart(q.Message, Limits.QuestionMessage) is { } message && message.Length != q.Message.Length
            ? q with { Message = message, Truncated = true }
            : s.Question;
        return ReferenceEquals(activity, s.Activity) && ReferenceEquals(question, s.Question) ? s : s with { Activity = activity, Question = question };
    }

    // A delegate in full (session.get { id, agent }): its own response and reasoning.
    public static Subagent Fit(Subagent a)
    {
        var (response, cut) = Limits.KeepEnd(a.Response, Limits.Response);
        var (reasoning, thought) = Limits.KeepEnd(a.Reasoning, Limits.Reasoning);
        return cut || thought ? a with { Response = response, ResponseTruncated = cut ? true : a.ResponseTruncated, Reasoning = reasoning } : a;
    }

    static Activity Fit(Activity a)
    {
        var (response, cut) = Limits.KeepEnd(a.Response, Limits.Response);
        var (reasoning, thought) = Limits.KeepEnd(a.Reasoning, Limits.Reasoning);
        var own = a.Own is { } o ? Fit(o) : null;
        if (!cut && !thought && ReferenceEquals(own, a.Own)) { return a; }
        return a with { Response = response, ResponseTruncated = cut ? true : a.ResponseTruncated, Reasoning = reasoning, Own = own };
    }

    static OwnActivity Fit(OwnActivity o)
    {
        var (response, cut) = Limits.KeepEnd(o.Response, Limits.Response);
        var (reasoning, thought) = Limits.KeepEnd(o.Reasoning, Limits.Reasoning);
        return cut || thought ? o with { Response = response, ResponseTruncated = cut ? true : o.ResponseTruncated, Reasoning = reasoning } : o;
    }
}
