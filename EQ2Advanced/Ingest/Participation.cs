using System;
using System.Text.RegularExpressions;

namespace EQ2Advanced.Ingest
{
    /// <summary>
    /// Is the character who owns this log actually FIGHTING, or just standing
    /// near a fight?
    ///
    /// An EQ2 log records what its client could see, which is why one raider's
    /// log parses a whole raid — and why a parked alt in the same zone writes
    /// down everybody else's pulls too. Uploading that log adds a second
    /// observation of fights its owner had no part in: a zone run whose every
    /// encounter belongs to other people, and a Live page offering four
    /// sessions of the same raid.
    ///
    /// The plugin cannot parse combat and should not try — the server owns that
    /// and proves it in its test suite. But it does not have to. EQ2 writes the
    /// LOGGING character as `YOU` / `YOUR &lt;Ability&gt;`, always, and their pets
    /// and attributed abilities as `&lt;Name&gt;'s ...`. That is a string test on a
    /// line that is already in hand, and it answers the only question being
    /// asked here: did this character DO anything.
    ///
    /// Two levels, deliberately:
    ///
    /// * an OWN ACTION starts participation — the verb set mirrors the server's
    ///   `_CONTRIBUTED` (`backend/pipeline/zoneruns.py`), which is damage,
    ///   heals, wards, cures, power and swings, so a raid-healing alt that
    ///   never deals a point of damage is not mistaken for a parked one;
    /// * being HIT only extends it. A toon eating the edge of a raid AoE while
    ///   parked is not in the fight, and neither is a buffbot — but somebody
    ///   who was fighting a second ago and is now only taking damage still is.
    /// </summary>
    public sealed class Participation
    {
        /// <summary>`YOU hit`, `YOU try` (a miss is still a swing), and every
        /// `YOUR &lt;Ability&gt; ...` line — damage, heal, ward, cure, power feed,
        /// rez. `YOU are ...` is deliberately absent: it is a state message
        /// ("YOU are no longer stunned"), not an action.</summary>
        private static readonly Regex OwnActionRe = new Regex(
            @"^(?:YOUR\s|YOU\s(?:hit|try)\b|You\s(?:prepare|summon|begin\scasting)\b)",
            RegexOptions.Compiled);

        /// <summary>Something landing on this character. `hits YOUR` is a ward
        /// or a pet taking it.</summary>
        private static readonly Regex IncomingRe = new Regex(
            @"(?:hits?|heals|hit)\sYOU(?:R|RSELF)?\b", RegexOptions.Compiled);

        /// <summary>`Bobby's Throat Gash hits ...`, `Bobby's blighted horde hits ...`
        /// — an ability or a pet the server will roll up onto this character.
        /// Built per instance because it needs the character's name.</summary>
        private readonly Regex _possessiveRe;

        private long _lastOwnAction = -1;   // log seconds, not wall clock
        private long _lastIncoming = -1;
        private long _lastSeen = -1;

        public Participation(string character)
        {
            _possessiveRe = string.IsNullOrEmpty(character) ? null
                : new Regex(@"^" + Regex.Escape(character) + @"'s\s", RegexOptions.Compiled);
        }

        /// <summary>The log second of the last line handed to <see cref="Observe"/>.
        /// Time is measured in LOG seconds rather than wall clock so a tail that
        /// is catching up after a disconnect judges a pull by when it happened,
        /// not by when it was read.</summary>
        public long LastSecond => _lastSeen;

        /// <summary>The log second this character last did something. -1 if
        /// never.</summary>
        public long LastOwnAction => _lastOwnAction;

        public void Observe(string line)
        {
            if (!ChatFilter.TrySplitBody(line, out var body)) return;
            var second = SecondOf(line);
            if (second > _lastSeen) _lastSeen = second;

            if (OwnActionRe.IsMatch(body)
                || (_possessiveRe != null && _possessiveRe.IsMatch(body)))
            {
                if (second > _lastOwnAction) _lastOwnAction = second;
                return;
            }
            if (IncomingRe.IsMatch(body) && second > _lastIncoming) _lastIncoming = second;
        }

        /// <summary>Fighting, as of the newest line seen. `idleSeconds` is how
        /// long after the last own action participation survives — it is a
        /// stream-level question ("is this character raiding tonight"), not a
        /// per-pull one, so it is minutes rather than the server's 7s encounter
        /// gap. Cutting a stream between two pulls of the same raid would be
        /// the same bug as cutting one mid-fight, only harder to notice.</summary>
        public bool Active(int idleSeconds)
        {
            if (_lastOwnAction < 0) return false;
            // Being hit extends, never starts: the clock runs from the last own
            // action, but incoming damage after it keeps the stream open.
            var last = Math.Max(_lastOwnAction, _lastIncoming > _lastOwnAction
                                                ? _lastIncoming : _lastOwnAction);
            return _lastSeen - last <= idleSeconds;
        }

        private static readonly Regex PrefixRe =
            new Regex(@"^\((\d{10})\)", RegexOptions.Compiled);

        private long SecondOf(string line)
        {
            var m = PrefixRe.Match(line);
            return m.Success ? long.Parse(m.Groups[1].Value) : _lastSeen;
        }
    }
}
