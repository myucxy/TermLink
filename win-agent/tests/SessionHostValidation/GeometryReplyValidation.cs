using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TermLink.LocalIpc;
using TermLink.SessionHost;

internal static class GeometryReplyValidation
{
    internal static void Run()
    {
        ValidateDurableHostGeometry();
        ValidateTraditionalReplies();
        ValidateAssignedColors();
        ValidateExtendedQueries();
        foreach (var item in new[]
        {
            ("14", 4), ("14;2", 4), ("15", 5), ("16", 6), ("18", 8), ("19", 9),
            ("016", 6), ("18;2", 8), ("16;999;0;1", 6), ("19;", 9)
        })
        {
            var query = Bytes("\x1b[" + item.Item1 + "t");
            var reply = Bytes("\x1b[" + item.Item2 + ";30;120t");
            for (var split = 0; split <= query.Length; split++)
            {
                using var router = new OuterTerminalReplySuppressor();
                router.ObserveOutput(query.Take(split).ToArray(), split);
                router.ObserveOutput(query.Skip(split).ToArray(), query.Length - split);
                Check(router.PendingCount == 1, "split query expectation: " + item.Item1);
                var arrow = Bytes("\x1b[A");
                Check(router.Filter(arrow, arrow.Length).SequenceEqual(arrow), "arrow preserved");
                var wrongKind = Bytes("\x1b[3;30;120t");
                Check(router.Filter(wrongKind, wrongKind.Length).SequenceEqual(wrongKind), "different report preserved");
                var malformed = Bytes("\x1b[" + item.Item2 + ";0;120t");
                Check(router.Filter(malformed, malformed.Length).SequenceEqual(malformed), "invalid size preserved");
                var decisions = new List<bool>();
                foreach (var value in reply)
                    router.FilterFragment(new[] { value }, 1, passed => decisions.Add(passed));
                Check(decisions.Count == reply.Length && decisions.All(passed => !passed), "reply must not acquire local control");
                Check(router.PendingCount == 0, "one query consumes exactly one expectation");
                Check(router.Filter(reply, reply.Length).SequenceEqual(reply), "unprompted repeated bytes preserved");
            }
        }

        foreach (var query in new[] { "\x1b[?16t", "\x1b[16:2t", "\x1b[8;30;120t", "\x1b[16;65535t" })
        {
            using var router = new OuterTerminalReplySuppressor();
            var bytes = Bytes(query);
            router.ObserveOutput(bytes, bytes.Length);
            Check(router.PendingCount == 0, "non-query must not create geometry expectations");
        }

        foreach (var split in Enumerable.Range(0, 7))
        {
            var replies = new List<byte[]>();
            using var router = new OuterTerminalReplySuppressor();
            using var authority = new TerminalCoreAuthority(120, 30, b => replies.Add(b.ToArray()), router.RegisterAuthorityReply);
            var session = Guid.NewGuid();
            var query = Bytes("\x1b[018t");
            var sequence = 1UL;
            foreach (var part in new[] { query.Take(split).ToArray(), query.Skip(split).ToArray() })
            {
                authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, sequence, part));
                Check(router.ProjectOutput(part, part.Length, sequence, () => true), "projection");
                sequence++;
            }
            Check(replies.Count == 1 && replies[0].SequenceEqual(Bytes("\x1b[8;30;120t")), "authority geometry");
            Check(router.PendingCount == 1, "no duplicate authority/projection expectation");
            var outerSize = Bytes("\x1b[8;40;150t");
            Check(router.Filter(outerSize, outerSize.Length).Length == 0, "different outer geometry must be suppressed");
            Check(router.PendingCount == 0, "geometry expectation retired");
        }

        using (var router = new OuterTerminalReplySuppressor())
        {
            var queries = Bytes("\x1b[16t\x1b[14t\x1b[19t");
            router.ObserveOutput(queries, queries.Length);
            foreach (var reply in new[] { "\x1b[9;60;200t", "\x1b[6;20;10t", "\x1b[4;600;1200t" })
            {
                var bytes = Bytes(reply);
                Check(router.Filter(bytes, bytes.Length).Length == 0, "reordered distinct geometry replies");
            }
            Check(router.PendingCount == 0, "all geometry kinds retired");
            var query = Bytes("\x1b[16t");
            Check(!router.ProjectOutput(query, query.Length, 100, () => false), "failed projection");
            Check(router.PendingCount == 0, "failed projection must not suppress future input");
        }

        var ptyReplies = new List<byte[]>();
        using (var router = new OuterTerminalReplySuppressor())
        using (var authority = new TerminalCoreAuthority(120, 30, b => ptyReplies.Add(b.ToArray()), router.RegisterAuthorityReply))
        {
            var session = Guid.NewGuid();
            var output = Bytes("\x1b[?2026$p\x1b[16t\x1b[?1016$p\x1b[c\x1b[18t");
            authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, 1, output));
            Check(router.ProjectOutput(output, output.Length, 1, () => true), "Claude startup projection");
            foreach (var reply in ptyReplies)
                Check(router.Filter(reply, reply.Length).Length == 0, "authority duplicate");
            var cellReply = Bytes("\x1b[6;20;10t");
            Check(router.Filter(cellReply, cellReply.Length).Length == 0 && router.PendingCount == 0, "Claude cell reply");
            authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, 2, Bytes("\x1b[!pSHELL_ALIVE")));
        }
    }

    private static void ValidateDurableHostGeometry()
    {
        var writes = new List<byte[]>();
        using var authority = new TerminalCoreAuthority(80,24, value => writes.Add(value.ToArray()), null);
        var session = Guid.NewGuid();
        var geometry = new TermLink.TerminalCore.NativeTerminalHostGeometryV1(9,18,1920,1080,0,0);
        authority.ApplyDurable(new LocalFrame(LocalFrameType.HostGeometry, session, 1, geometry.Encode()));
        Check(writes.Count == 0 && authority.LastAppliedSequence == 1, "geometry state wrote unsolicited terminal input");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, 2, Bytes("\x1b[14t\x1b[16t")));
        Check(writes.Count == 2 && writes[0].SequenceEqual(Bytes("\x1b[4;432;720t")) &&
            writes[1].SequenceEqual(Bytes("\x1b[6;18;9t")), "durable geometry reply mismatch");
        using var restored = TermLink.TerminalCore.NativeTerminalCoreV2.ImportSnapshot(authority.ExportSnapshot());
        var reply = restored.ApplyOutputWithHostEffectsV2(3, Bytes("\x1b[19t"));
        Check(reply.Records.Single().Payload.SequenceEqual(Bytes("\x1b[9;60;213t")), "authority geometry lost during restore");
    }

    private static void ValidateTraditionalReplies()
    {
        using (var router = new OuterTerminalReplySuppressor()) {
            router.Expect(Bytes("\x1b[16383*{"));
            Check(router.Filter(Bytes("\x1b[384*{"), 7).Length == 0, "Outer macro-space difference leaked");
            Check(router.PendingCount == 0, "Macro-space expectation retained");
        }
        foreach (var value in new[] {
            (Bytes("\x1b[0n"), new byte[] { 0x9b, (byte)'0', (byte)'n' }),
            (Bytes("\x1bP!|00000000\x1b\\"), new byte[] { 0x90 }.Concat(Bytes("!|00000000")).Append((byte)0x9c).ToArray()),
            (Bytes("\x1bP!|00000000\x1b\\"), new byte[] { 0x90 }.Concat(Bytes("!|00000000\x1b\\")).ToArray()),
            (Bytes("\x1bP!|00000000\x1b\\"), Bytes("\x1bP!|00000000").Append((byte)0x9c).ToArray()),
            (Bytes("\x1b/Z"), Bytes("\x1b/Z")) })
        {
            for (var split = 0; split <= value.Item2.Length; split++) {
                using var router = new OuterTerminalReplySuppressor(); router.Expect(value.Item1);
                var text = Encoding.UTF8.GetBytes("Лܐݝ"); // UTF-8 continuation bytes 9b, 90 and 9d are text.
                var passed = new List<byte>(); foreach (var b in text) passed.AddRange(router.Filter(new[] { b }, 1));
                Check(passed.SequenceEqual(text), "C1 filter swallowed UTF-8 text");
                Check(router.Filter(value.Item2.Take(split).ToArray(), split).Length == 0, "split C1 prefix leaked");
                Check(router.Filter(value.Item2.Skip(split).ToArray(), value.Item2.Length - split).Length == 0, "C1 reply delivered twice");
                Check(router.PendingCount == 0, "C1 reply did not consume expectation");
            }
        }
    }

    private static void ValidateAssignedColors()
    {
        var output = Bytes("\x1b]17;#123456\x1b\\\x1b]17;?\x1b\\\x1b[1;2;3,|\x1b]10;#abcdef\x1b\\\x1b]4;2;?\x1b\\\x1b]10;?\x1b\\\x1bP$q1,|\x1b\\");
        for (var split = 0; split <= output.Length; split++) {
            var replies = new List<byte[]>(); var forwarded = new List<byte[]>();
            using var router = new OuterTerminalReplySuppressor(terminalReplyPassThrough: forwarded.Add);
            using var authority = new TerminalCoreAuthority(20, 5, bytes => replies.Add(bytes.ToArray()), router.RegisterAuthorityReply);
            var session = Guid.NewGuid(); ulong sequence = 1;
            foreach (var part in new[] { output.Take(split).ToArray(), output.Skip(split).ToArray() }) {
                authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, sequence, part));
                Check(router.ProjectOutput(part, part.Length, sequence++, () => true), "color projection failed");
            }
            Check(replies.Count == 4 && router.PendingCount == 4, "color replies duplicated or lost");
            foreach (var reply in replies) Check(router.Filter(reply, reply.Length).Length == 0, "outer color reply reached input: split=" + split + "; reply=" + Convert.ToHexString(reply));
            Check(forwarded.Count == 0 && router.PendingCount == 0, "color reply forwarding/ownership diverged");
        }
    }

    private static void ValidateExtendedQueries()
    {
        var fixtures = new[] {
            ("\u001b[1$w", "\u001bP1$u2;3;1;A;@;@;0;2;L;BBAA\u001b\\"),
            ("\u001b[2$w", "\u001bP2$u4/12\u001b\\"),
            ("\u001b[2;2$u", "\u001bP2$s0;2;100;0;0\u001b\\"),
            ("\u001b[=c", "\u001bP!|FFFFFFFF\u001b\\"),
            ("\u001b[0x", "\u001b[2;1;1;120;120;1;0x"),
            ("\u001b[1x", "\u001b[3;1;1;120;120;1;0x"),
            ("\u001b[?15n", "\u001b[?10n"),
            ("\u001b[?25n", "\u001b[?20n"),
            ("\u001b[\"v", "\u001b[50;160;1;1;1\"w"),
            ("\u001b[42;1;1;1;1;1*y", "\u001bP42!~ABCD\u001b\\"),
        };
        foreach (var (queryText, replyText) in fixtures)
        {
            var query = Bytes(queryText);
            for (var split = 0; split <= query.Length; split++)
            {
                var replies = new List<byte[]>();
                using var router = new OuterTerminalReplySuppressor();
                using var authority = new TerminalCoreAuthority(80, 24, b => replies.Add(b.ToArray()), router.RegisterAuthorityReply);
                var session = Guid.NewGuid();
                var sequence = 1UL;
                foreach (var part in new[] { query.Take(split).ToArray(), query.Skip(split).ToArray() })
                {
                    authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, session, sequence, part));
                    router.ProjectOutput(part, part.Length, sequence++, () => true);
                }
                Check(replies.Count == 1 && router.PendingCount == 1, "one extended authority reply");
                foreach (var malformed in new[] {
                    "\u001bP1$u2;3;1;A;@;F;0;2;L;BBAA\u001b\\", "\u001bP2$u513\u001b\\",
                    "\u001bP2$s0;2;101;0;0\u001b\\",
                    "\u001bP!|GGGGGGGG\u001b\\", "\u001b[2;1x", "\u001b[2;1;;128;128;1;0x",
                    "\u001b[50;160;1;1;0\"w", "\u001b[50;160;1;1\"w", "\u001b[?13;0n",
                    "\u001bP42!~G000\u001b\\"
                })
                {
                    var invalid = Bytes(malformed);
                    Check(router.Filter(invalid, invalid.Length).SequenceEqual(invalid), "malformed extended bytes preserved");
                }
                var wrong = Bytes("\u001bP43!~ABCD\u001b\\");
                Check(router.Filter(wrong, wrong.Length).SequenceEqual(wrong), "unrelated checksum request preserved");
                var reply = Bytes(replyText);
                var decisions = new List<bool>();
                foreach (var value in reply) router.FilterFragment(new[] { value }, 1, passed => decisions.Add(passed));
                Check(decisions.Count == reply.Length && decisions.All(passed => !passed), "extended outer reply must not become user input");
                Check(router.PendingCount == 0, "extended reply retired once");
            }
        }
        using var statuses = new OuterTerminalReplySuppressor();
        statuses.Expect(Bytes("\u001b[?13n"));
        var keyboard = Bytes("\u001b[?27;0;0;5n");
        Check(statuses.Filter(keyboard, keyboard.Length).SequenceEqual(keyboard), "printer expectation cannot swallow keyboard report");
    }

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Geometry replies: " + message);
    }
}
