using System.Text.RegularExpressions;

namespace EQ2Advanced.Ingest
{
    /// <summary>What kind of governed chat line a body is. `null` from
    /// <see cref="ChatFilter.Classify"/> means "not governed at all" — combat and
    /// system text, which this file never looks at twice.</summary>
    public enum ChatChannel { Group, Raid, Npc, Flavor, Public, Private }

    /// <summary>
    /// The decision that used to live entirely on the server
    /// (`backend/pipeline/redact.py`) now made here, before a line is ever
    /// batched for send: private chat should not merely go unstored, it should
    /// never leave this PC. Ported line-for-line from two Python modules so it
    /// cannot drift from what the server already proves correct in its own test
    /// suite — `backend/tests/test_redaction.py` and `test_chatbus.py` are the
    /// source of truth for every regex and sample line here.
    ///
    /// THIS IS NOT `redact.py`'s TWO-WAY SPLIT. `channel_of` in `redact.py`
    /// answers "may this be STORED in a raid's log", and General/LFG/Auction
    /// answer no there — a raid's stored log has no business carrying public
    /// channel spam. But `pipeline/chatbus.py` reads those same public-channel
    /// lines out of the wire batch BEFORE that storage redaction runs, to power
    /// the site's own public chat page and recruiting board. So this classifier
    /// keeps `Public` as its own bucket rather than folding it into `Private`.
    /// `Private` is what's actually nobody else's business: tells, guild chat,
    /// officer chat, local /say, and any channel this file does not recognize
    /// (default-deny, same direction as redact.py).
    ///
    /// TWO INDEPENDENT OPT-INS, NOT ONE. `Group`/`Raid` and `Public` are gated
    /// by separate settings (see `ShouldSend`) — sending your own raid's group
    /// chat for something like loot tracking is a different decision from
    /// contributing General/LFG/Auction to the site's shared chat page, and
    /// someone may want either without the other. Both default off.
    /// </summary>
    public static class ChatFilter
    {
        // (epoch)[ctime] body — backend/parser/prefix.py's PREFIX_RE.
        private static readonly Regex PrefixRe =
            new Regex(@"^\((?:\d{10})\)\[[^\]]+\] (?<body>.*)$", RegexOptions.Compiled);

        // backend/parser/classify.py: CHAT_PREFIXES + CHAT_RE.
        private static readonly Regex YouSayOrTellRe =
            new Regex(@"^You (?:say|tell)\b", RegexOptions.Compiled);

        // backend/pipeline/redact.py: _SPEAKER_RE / _QUOTED_RE.
        private static readonly Regex SpeakerRe = new Regex(
            @"^\\a(?<kind>PC|NPC) -?\d+ [^\\]*\\/a (?<rest>.*)$", RegexOptions.Compiled);
        private static readonly Regex QuotedRe = new Regex(", \"", RegexOptions.Compiled);

        // backend/pipeline/chatbus.py: _PC_RE / _SELF_RE, restricted to CHANNELS.
        // The relayed form carries the speaker markup and is matched via `rest`
        // (what's left after SpeakerRe strips "\aPC <id> Name\/a "); the
        // self-authored form is the plain "You tell <Chan> (<n>), ..." line.
        private static readonly Regex PublicRestRe = new Regex(
            @"^tells (?:General|LFG|Auction) \(\d+\), """, RegexOptions.Compiled);
        private static readonly Regex PublicSelfRe = new Regex(
            @"^You tell (?:General|LFG|Auction) \(\d+\), """, RegexOptions.Compiled);

        /// <summary>Split a raw log line into its body, or null if the line
        /// doesn't carry the `(epoch)[ctime] ` prefix EQ2 always writes — such a
        /// line is not chat and is never governed by anything below.</summary>
        public static bool TrySplitBody(string line, out string body)
        {
            var m = PrefixRe.Match(line ?? "");
            if (!m.Success) { body = null; return false; }
            body = m.Groups["body"].Value;
            return true;
        }

        /// <summary>Classify an already-split body. Null means "not governed at
        /// all" — the caller should always send it.</summary>
        public static ChatChannel? Classify(string body)
        {
            var governed = body.StartsWith("\\aPC ") || body.StartsWith("\\aNPC ")
                           || YouSayOrTellRe.IsMatch(body);
            if (!governed) return null;

            if (!QuotedRe.IsMatch(body))
                // A governed line with no typed message carries nothing private
                // — boss-flavor text like "Bob Goes Into a Bloodlust!!.".
                return ChatChannel.Flavor;

            var m = SpeakerRe.Match(body);
            if (m.Success)
            {
                var rest = m.Groups["rest"].Value;
                if (m.Groups["kind"].Value == "NPC")
                    // Scripted boss dialogue, including "says in Thexian" — game
                    // content, not anybody's conversation.
                    return rest.StartsWith("says") ? ChatChannel.Npc : ChatChannel.Private;

                if (rest.StartsWith("says to the group,")) return ChatChannel.Group;
                if (rest.StartsWith("says to the raid party,")) return ChatChannel.Raid;
                if (PublicRestRe.IsMatch(rest)) return ChatChannel.Public;
                return ChatChannel.Private;
            }

            // A self-authored line: "You say ..." / "You tell ...".
            if (body.StartsWith("You say to the group,")) return ChatChannel.Group;
            if (body.StartsWith("You say to the raid party,")) return ChatChannel.Raid;
            if (PublicSelfRe.IsMatch(body)) return ChatChannel.Public;
            return ChatChannel.Private;
        }

        /// <summary>The one call site cares about: may this raw log line leave
        /// the PC at all? `includeGroupRaidChat` and `includePublicChat` are the
        /// plugin's two independent opt-in checkboxes; everything else here is
        /// not gated by either.</summary>
        public static bool ShouldSend(string line, bool includeGroupRaidChat, bool includePublicChat)
        {
            if (!TrySplitBody(line, out var body)) return true;
            var channel = Classify(body);
            if (channel == null) return true;
            switch (channel)
            {
                case ChatChannel.Npc:
                case ChatChannel.Flavor:
                    return true;
                case ChatChannel.Public:
                    return includePublicChat;
                case ChatChannel.Group:
                case ChatChannel.Raid:
                    return includeGroupRaidChat;
                default:
                    return false;
            }
        }
    }
}
