using System;

namespace EQ2Advanced.Core
{
    /// <summary>
    /// Everything the plugin remembers between ACT restarts. Persisted as one
    /// JSON blob (see <see cref="JsonStore"/>).
    ///
    /// The device token is DPAPI-wrapped on disk (see <see cref="TokenProtector"/>)
    /// rather than written bare — the same tradeoff ACT's own config accepts for
    /// everything else, but a stolen config file or a support-thread paste no
    /// longer hands out a working credential by itself. It is still not a
    /// password: the token belongs to the account, can only SEND log lines,
    /// cannot read anything back, and is revocable from the site. Each batch
    /// still names its logfile's character. Who can see the resulting raids is
    /// decided on eq2advanced.com and is deliberately none of this plugin's
    /// business.
    /// </summary>
    public class Settings
    {
        public string Host = "https://eq2advanced.com";
        public string Token = "";

        /// <summary>Send new log lines as they are written.</summary>
        public bool LiveUpload = false;

        /// <summary>Opt-in: also send group and raid chat that falls near a
        /// fight (the server trims it to the fight window). Off by default;
        /// example use: loot-call tracking. See <see cref="Ingest.ChatFilter"/>.</summary>
        public bool ShareChat = false;

        /// <summary>Opt-in: also send the public General/LFG/Auction channels,
        /// which feed eq2advanced.com's community chat page. Off by default —
        /// independent of <see cref="ShareChat"/>: contributing your raid's own
        /// group/raid chat and contributing to the site's shared chat page are
        /// separate decisions. See <see cref="Ingest.ChatFilter"/>.</summary>
        public bool SharePublicChat = false;

        /// <summary>Character name last reported by /ingest/hello — shown so it
        /// is obvious WHICH character a paired token is uploading as.</summary>
        public string CharacterName = "";

        // --- tuning; rarely touched, but here rather than hard-coded ---

        /// <summary>Log seconds per batch. Batches are cut on log-second
        /// boundaries and this is the width of one; the server's line dedupe
        /// keys on an occurrence ordinal WITHIN a batch, so a second split
        /// across two batches would double-count.
        ///
        /// This is a PACKING rule, not a delay: a completed second is emitted
        /// the moment the next one appears, and the window only decides whether
        /// two seconds that were both ready travel in one request or two. So it
        /// stays at 2 — it costs the live path nothing and it halves the
        /// request count on a backfill.</summary>
        public int WindowSeconds = 2;

        /// <summary>Real seconds between sends.
        ///
        /// The site's live meter is meant to feel like ACT's own, and this is
        /// the biggest single term in why it did not: at 2.0s a hit waited an
        /// average of a second here before anything was even sent. Halving it
        /// twice costs 2 requests a second of a couple of KB each — the read is
        /// a file-length check and sends nothing when nothing was written — and
        /// buys back most of the gap. Below ~0.5s there is nothing left to win:
        /// a second cannot be sent until the next one starts, which is a floor
        /// this setting cannot move (see LogTail).</summary>
        public double CadenceSeconds = 0.5;

        /// <summary>Milliseconds to wait between batches while importing old
        /// logs. Importing years of raids is thousands of requests, and the box
        /// on the other end is also serving the website — this keeps a bulk
        /// import polite. 0 disables it.</summary>
        public int ImportPauseMs = 150;

        /// <summary>Which generation of the TUNING defaults this config was
        /// written with. Not a file-format version — everything else here is a
        /// choice somebody made and is never overwritten.
        ///
        /// The tuning fields are the exception, because nobody chose them: they
        /// were shipped, they are saved verbatim into everyone's config on the
        /// first batch, and a new default would therefore reach nobody who
        /// already has the plugin — which is precisely the people whose meter
        /// feels slow. So a config written by an older build has its tuning
        /// brought up to this build's, once, and is then left alone: change one
        /// by hand afterwards and it stays changed.</summary>
        public int TuningGeneration = 0;

        /// <summary>The generation the defaults above belong to. Bump it in the
        /// same commit that changes one of them.</summary>
        public const int CurrentTuning = 1;

        // --- Legacy single-log cursor, retained only to migrate an existing
        // install into LogCursorStore. New acknowledgements are persisted per
        // logfile so several ACT instances cannot overwrite each other. ---
        public string LastLogPath = "";
        public long LastOffset = 0;

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Host)) Host = "https://eq2advanced.com";
            Host = Host.Trim().TrimEnd('/');
            Token = (Token ?? "").Trim();
            CharacterName = CharacterName ?? "";
            if (WindowSeconds < 1) WindowSeconds = 1;
            if (WindowSeconds > 60) WindowSeconds = 60;
            if (CadenceSeconds < 0) CadenceSeconds = 0;
            if (CadenceSeconds > 60) CadenceSeconds = 60;
            if (ImportPauseMs < 0) ImportPauseMs = 0;
            if (ImportPauseMs > 5000) ImportPauseMs = 5000;
        }

        public bool Paired => !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(Host);

        /// <summary>False means the token currently on disk is NOT DPAPI
        /// ciphertext — either a pre-DPAPI config that hasn't been re-saved yet,
        /// or <see cref="TokenProtector.Protect"/> failing on this machine (see
        /// its doc comment for why that happens). Set on <see cref="Load"/> from
        /// what was actually on disk, and again on every <see cref="Save"/>;
        /// <see cref="Ui.ConfigTab"/> surfaces it rather than leaving it silent.
        /// Not persisted — it describes the file, not a preference.</summary>
        public bool TokenProtectedAtRest { get; private set; } = true;

        public static Settings Load()
        {
            var s = JsonStore.Load<Settings>();
            var raw = s.Token;
            s.TokenProtectedAtRest = TokenProtector.IsProtected(raw);
            s.Token = TokenProtector.Unprotect(raw);
            s.AdoptNewTuning();
            s.Normalize();
            return s;
        }

        /// <summary>Take this build's tuning defaults if the stored config
        /// predates them. See <see cref="TuningGeneration"/>.</summary>
        public void AdoptNewTuning()
        {
            if (TuningGeneration >= CurrentTuning) return;
            var fresh = new Settings();
            WindowSeconds = fresh.WindowSeconds;
            CadenceSeconds = fresh.CadenceSeconds;
            ImportPauseMs = fresh.ImportPauseMs;
            TuningGeneration = CurrentTuning;
        }

        public void Save()
        {
            Normalize();
            var plain = Token;
            try
            {
                Token = TokenProtector.Protect(plain, out var ok);
                TokenProtectedAtRest = ok;
                JsonStore.Save(this);
            }
            finally { Token = plain; }
        }

        /// <summary>
        /// Parse an `eq2advanced://pair?host=...&amp;token=...` payload — what the
        /// QR code on the site's Characters page encodes. Pasting the bare token
        /// is also accepted, and leaves the host alone.
        /// </summary>
        public bool ApplyPairPayload(string text, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) { error = "Nothing to pair with."; return false; }

            if (!text.StartsWith("eq2advanced://", StringComparison.OrdinalIgnoreCase))
            {
                // a bare token: no scheme, no spaces, and long enough to be one
                if (text.Contains(" ") || text.Length < 20)
                {
                    error = "That does not look like a token or a pairing link.";
                    return false;
                }
                Token = text;
                Save();
                return true;
            }

            var q = text.IndexOf('?');
            if (q < 0) { error = "Pairing link has no token in it."; return false; }
            string host = null, token = null;
            foreach (var pair in text.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair.Substring(0, eq);
                var val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                if (key == "host") host = val;
                else if (key == "token") token = val;
            }
            if (string.IsNullOrEmpty(token)) { error = "Pairing link has no token in it."; return false; }
            // The link carries the host because the site can move boxes; trusting
            // it is the whole point of pairing rather than typing a URL.
            if (!string.IsNullOrEmpty(host)) Host = host;
            Token = token;
            Save();
            return true;
        }
    }
}
