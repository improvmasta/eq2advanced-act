using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EQ2Advanced.Ingest;

class Program
{
    static int failures = 0;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) failures++;
    }

    // A raid-shaped log: `n` lines per second, some of them byte-identical
    // within the second (the case the dedupe contract turns on), plus a
    // multi-byte name so the UTF-8 accounting is exercised.
    static List<string> MakeLog(long firstSecond, int seconds, int linesPerSecond)
    {
        var lines = new List<string>();
        for (var s = 0; s < seconds; s++)
        {
            var ts = firstSecond + s;
            for (var i = 0; i < linesPerSecond; i++)
            {
                var who = i % 5 == 0 ? "Zylphäx" : "Reyfiler";   // non-ASCII on purpose
                // every 3rd line is a byte-for-byte repeat inside the second
                var body = i % 3 == 0
                    ? who + " dons the Mail of Souls!"
                    : who + " hits a D'Morte ransacker for " + i + " crushing damage.";
                lines.Add("(" + ts + ")[Sun Aug  9 18:53:47 2026] " + body);
            }
        }
        return lines;
    }

    static string WriteLog(List<string> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "eq2log_Tester_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        return path;
    }

    static void Main()
    {
        var log = MakeLog(1786316000, 40, 120);
        var path = WriteLog(log);
        var total = new FileInfo(path).Length;

        // ---- 1. batches tile the file exactly, and no second is split ----
        {
            var tail = new LogTail(2) { BulkMode = true };
            tail.Resume(path, 0);
            var all = new List<LineBatch>();
            while (!tail.AtEnd) all.AddRange(tail.Read());
            all.AddRange(tail.Drain());

            var contiguous = all[0].StartOffset == 0;
            for (var i = 1; i < all.Count; i++)
                if (all[i].StartOffset != all[i - 1].EndOffset) contiguous = false;
            Check(contiguous, "batch byte ranges are contiguous from 0");
            Check(all[all.Count - 1].EndOffset == total,
                  "last batch ends at EOF (" + all[all.Count - 1].EndOffset + " == " + total + ")");

            var got = all.SelectMany(b => b.Lines).ToList();
            Check(got.Count == log.Count && got.SequenceEqual(log),
                  "every line arrives exactly once, in order (" + got.Count + "/" + log.Count + ")");

            // a log second must never appear in two batches
            var secondsPerBatch = all.Select(b => b.Lines.Select(Second).Distinct().ToList()).ToList();
            var seen = new Dictionary<string, int>();
            var split = false;
            for (var i = 0; i < secondsPerBatch.Count; i++)
                foreach (var s in secondsPerBatch[i])
                {
                    if (seen.ContainsKey(s)) split = true;
                    seen[s] = i;
                }
            Check(!split, "no log second is split across two batches");
            Check(all.All(b => b.Bytes <= LogTail.MaxBatchBytes && b.Lines.Count <= LogTail.MaxBatchLines),
                  "no batch exceeds the size cap (largest " + all.Max(b => b.Lines.Count) + " lines, "
                  + all.Max(b => b.Bytes) + " bytes)");
        }

        // ---- 2. the regression: a send fails mid-read, nothing is lost ----
        // Reads 4 MB at a time, so a catch-up read yields many batches. Send the
        // first two, "fail" the third, rewind to ITS start, and keep going.
        {
            // big enough that one Read() spans several 4 MB reads and many
            // batches — the catch-up shape that lost 191 seconds in the field
            var bigLog = MakeLog(1786320000, 900, 160);
            var bigPath = WriteLog(bigLog);
            Console.WriteLine("  (catch-up log: " + new FileInfo(bigPath).Length / (1024 * 1024) + " MB, "
                              + bigLog.Count + " lines)");

            var tail = new LogTail(2) { BulkMode = true };
            tail.Resume(bigPath, 0);
            var delivered = new List<string>();
            var fired = 0;
            var guard = 0;
            while (!tail.AtEnd && guard++ < 5000)
            {
                var batches = tail.Read();
                var abort = false;
                for (var i = 0; i < batches.Count && !abort; i++)
                {
                    // fail the 3rd batch of every read, twice over the run
                    if (fired < 2 && i == 2)
                    {
                        fired++;
                        tail.Rewind(batches[i].StartOffset);   // the failure path
                        abort = true;
                        break;
                    }
                    delivered.AddRange(batches[i].Lines); tail.Commit(batches[i].EndOffset);
                }
                if (abort) continue;
            }
            foreach (var b in tail.Drain()) delivered.AddRange(b.Lines);

            Check(fired == 2, "the simulated failures actually fired (" + fired + ")");
            Check(delivered.SequenceEqual(bigLog),
                  "after failed batches mid-read, every line still arrives once ("
                  + delivered.Count + "/" + bigLog.Count + ")");
            File.Delete(bigPath);
        }

        // ---- 2b. the ORIGINAL bug, reproduced verbatim ----
        // The uploader used to rewind to `tail.Offset` — the end of the read,
        // which is exactly where the cursor already was, so no forward-motion
        // check would ever have fired. Measuring against the ACKNOWLEDGED mark
        // is what catches it: the cursor is 4 MB of unsent log ahead of it.
        {
            var bigLog = MakeLog(1786330000, 900, 160);
            var bigPath = WriteLog(bigLog);
            var tail = new LogTail(2) { BulkMode = true };
            tail.Resume(bigPath, 0);
            var delivered = new List<string>();
            var fired = false;
            var guard = 0;
            while (!tail.AtEnd && guard++ < 5000)
            {
                var batches = tail.Read();
                var abort = false;
                for (var i = 0; i < batches.Count && !abort; i++)
                {
                    if (!fired && i == 2)
                    {
                        fired = true;
                        tail.Rewind(tail.Offset);   // <-- the old, wrong target
                        abort = true;
                        break;
                    }
                    delivered.AddRange(batches[i].Lines); tail.Commit(batches[i].EndOffset);
                }
            }
            foreach (var b in tail.Drain()) { delivered.AddRange(b.Lines); tail.Commit(b.EndOffset); }
            Check(fired, "the old-target rewind was exercised");
            Check(delivered.SequenceEqual(bigLog),
                  "the original bug's rewind target no longer skips anything ("
                  + delivered.Count + "/" + bigLog.Count + ")");
            Check(tail.TakeAnomaly() != null, "and it is reported rather than silent");
            File.Delete(bigPath);
        }

        // ---- 3. rewinding forwards is refused ----
        {
            var tail = new LogTail(2) { BulkMode = true };
            tail.Resume(path, 0);
            var batches = tail.Read();
            tail.Commit(batches[0].EndOffset);          // only the first batch landed
            var acked = batches[0].EndOffset;
            tail.Rewind(tail.Offset + 100000);          // ask for anything past it
            Check(tail.Offset == acked,
                  "a rewind past the acknowledged mark lands ON the mark ("
                  + tail.Offset + " == " + acked + ")");
            var anomaly = tail.TakeAnomaly();
            Check(anomaly != null, "a forward rewind is reported: " + (anomaly ?? "(nothing)"));
            Check(tail.TakeAnomaly() == null, "the anomaly is taken once, not repeated");
        }

        // ---- 4. resume from a persisted offset lands on a line boundary ----
        {
            var tail = new LogTail(2) { BulkMode = true };
            tail.Resume(path, 0);
            var first = tail.Read();
            var cut = first[first.Count - 1].EndOffset;   // what Settings.LastOffset stores

            var resumed = new LogTail(2) { BulkMode = true };
            resumed.Resume(path, cut);
            var rest = new List<string>();
            var guard = 0;
            while (!resumed.AtEnd && guard++ < 500) foreach (var b in resumed.Read()) rest.AddRange(b.Lines);
            foreach (var b in resumed.Drain()) rest.AddRange(b.Lines);

            var sentFirst = first.SelectMany(b => b.Lines).ToList();
            Check(sentFirst.Concat(rest).SequenceEqual(log),
                  "a resume from the persisted offset continues exactly where it stopped");
            Check(rest.Count > 0 && rest[0].StartsWith("("),
                  "the first line after a resume is whole, not decapitated: "
                  + (rest.Count > 0 ? rest[0].Substring(0, Math.Min(48, rest[0].Length)) : "(none)"));
        }

        // ---- 5. a live tail across a growing file loses nothing ----
        {
            var growing = Path.Combine(Path.GetTempPath(), "eq2log_Grow_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(growing, "", new UTF8Encoding(false));
            var tail = new LogTail(2);
            tail.Open(growing, fromEnd: false);
            var delivered = new List<string>();
            var chunk = 300;
            for (var i = 0; i < log.Count; i += chunk)
            {
                File.AppendAllText(growing,
                    string.Join("\r\n", log.Skip(i).Take(chunk)) + "\r\n", new UTF8Encoding(false));
                foreach (var b in tail.Read()) delivered.AddRange(b.Lines);
            }
            System.Threading.Thread.Sleep(1600);   // past IdleFlushSeconds
            foreach (var b in tail.Read()) delivered.AddRange(b.Lines);
            foreach (var b in tail.Drain()) delivered.AddRange(b.Lines);
            Check(delivered.SequenceEqual(log),
                  "an incrementally-written log arrives complete (" + delivered.Count + "/" + log.Count + ")");
            File.Delete(growing);
        }

        // ---- 6. boxed ACT instances keep independent resume cursors ----
        {
            var cursorDir = Path.Combine(Path.GetTempPath(),
                "eq2advanced-cursors-" + Guid.NewGuid().ToString("N"));
            var firstPath = WriteLog(MakeLog(1786400000, 2, 4));
            var secondPath = WriteLog(MakeLog(1786500000, 2, 4));
            var cursors = new LogCursorStore(cursorDir);

            cursors.Save(firstPath, 100);
            cursors.Save(secondPath, 300);
            cursors.Save(firstPath, 200);
            var reloaded = new LogCursorStore(cursorDir);
            Check(reloaded.Load(firstPath) == 200,
                  "the first boxed character keeps its own resume cursor");
            Check(reloaded.Load(secondPath) == 300,
                  "the second boxed character keeps its own resume cursor");

            // A lagging duplicate process cannot move an acknowledged cursor
            // backwards for the same file generation.
            reloaded.Save(firstPath, 150);
            Check(reloaded.Load(firstPath) == 200,
                  "a second ACT process cannot move one logfile cursor backwards");

            // Exercise the named mutex with independent store instances at the
            // same time, as separate ACT plugin instances do.
            var startCursorWrites = new ManualResetEventSlim(false);
            var writers = Enumerable.Range(0, 20).Select(i =>
            {
                var offset = 201 + i;
                return new Thread(() =>
                {
                    startCursorWrites.Wait();
                    new LogCursorStore(cursorDir).Save(firstPath, offset);
                });
            }).ToList();
            foreach (var writer in writers) writer.Start();
            startCursorWrites.Set();
            foreach (var writer in writers) writer.Join();
            Check(reloaded.Load(firstPath) == 220,
                  "concurrent ACT instances preserve the furthest acknowledgement");

            // Replacing/rotating the file changes its beginning and starts a
            // new generation at byte zero. It must not look like a never-seen
            // file, because that would make the live uploader start at EOF and
            // skip the entire replacement.
            File.WriteAllText(firstPath,
                string.Join("\r\n", MakeLog(1786600000, 2, 4)) + "\r\n",
                new UTF8Encoding(false));
            Check(reloaded.Load(firstPath) == 0,
                  "a replaced logfile resumes losslessly from byte zero");
            reloaded.Save(firstPath, 50);
            Check(reloaded.Load(firstPath) == 50,
                  "a replaced logfile starts a fresh cursor generation");

            // ACT can expose a just-created empty file. Its persisted zero is
            // still the safe resume point after the first log line appears.
            var growingPath = WriteLog(new List<string>());
            File.WriteAllText(growingPath, "", new UTF8Encoding(false));
            reloaded.Save(growingPath, 0);
            File.AppendAllText(growingPath, MakeLog(1786700000, 1, 1)[0] + "\r\n",
                               new UTF8Encoding(false));
            Check(reloaded.Load(growingPath) == 0,
                  "an empty logfile that begins growing resumes from byte zero");

            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(growingPath);
            Directory.Delete(cursorDir, true);
        }

        // ---- 7. ChatFilter classifies exactly what redact.py/chatbus.py do ----
        // Sample lines mirror backend/tests/test_redaction.py's PRIVATE/RETAINED/
        // COMBAT and backend/tests/test_chatbus.py's public-channel lines, so this
        // can't quietly drift from what the server's own test suite pins.
        {
            var never = new[]
            {
                "\\aPC -1 Moklok:Moklok\\/a tells you, \"meet me at the docks\"",
                "You tell Ellea, \"don't tell anyone\"",
                "\\aPC -1 Spades:Spades\\/a says to the guild, \"guild bank is short\"",
                "\\aPC -1 Spades:Spades\\/a says to the officer channel, \"about that member\"",
                "\\aPC 76623932 Rando:Rando\\/a says, \"local chatter\"",
                "You say, \"Some how I dropped from the group.\"",
                "\\aPC -1 Nobody:Nobody\\/a tells Therapy (77), \"my private business\"",
                "\\aPC -1 Nobody:Nobody\\/a whispers something new, \"unanticipated\"",
            };
            var alwaysPublic = new[]
            {
                "\\aPC -1 Evoxx:Evoxx\\/a tells LFG (3), \"looking for group\"",
                "\\aPC -1 Evoxx:Evoxx\\/a tells General (2), \"anyone up\"",
                "\\aPC -1 Evoxx:Evoxx\\/a tells Auction (10), \"WTB fabled\"",
                "You tell General (9), \"over here\"",
            };
            var alwaysNpcOrFlavor = new[]
            {
                "\\aNPC 57166 Zylphax:Zylphax\\/a says, \"Come here!\"",
                "\\aNPC 57166 Zylphax:Zylphax\\/a says in Thexian, \"N'Tal!\"",
                "\\aPC 68371 Ellea:Ellea\\/a blesses Spades with their ancestor's knowledge [Ancestry].",
            };
            var groupRaid = new[]
            {
                "\\aPC 58070227 Aros:Aros\\/a says to the group, \"pull in 5\"",
                "\\aPC -1 Ellea:Ellea\\/a says to the raid party, \"cures on the tank\"",
                "You say to the group, \"lifeburn\"",
                "You say to the raid party, \"rezzing\"",
            };
            var combat = new[]
            {
                "You have entered The Estate of Unrest.",
                "YOU hit a training dummy for 100 crushing damage.",
                "You have killed a training dummy.",
                "You prepare the Teachings of the Underworld.",
            };
            string L(string body) => "(1786316000)[Sun Aug  9 18:53:47 2026] " + body;
            bool Send(string b, bool group, bool pub) => ChatFilter.ShouldSend(L(b), group, pub);

            Check(never.All(b => !Send(b, false, false) && !Send(b, true, true)),
                  "tells/guild/officer/local-say/unknown channels never leave the PC, either checkbox");
            Check(alwaysNpcOrFlavor.All(b => Send(b, false, false) && Send(b, true, true)),
                  "NPC dialogue and no-quote flavor lines are always sent");
            Check(combat.All(b => Send(b, false, false) && Send(b, true, true)),
                  "combat/system lines are never governed at all");

            // The two checkboxes gate independently — each on its own, and both
            // together, without leaking into the other's category.
            Check(groupRaid.All(b => !Send(b, false, false) && !Send(b, false, true)),
                  "group/raid chat withheld with its checkbox off, regardless of the other one");
            Check(groupRaid.All(b => Send(b, true, false) && Send(b, true, true)),
                  "group/raid chat sent once its checkbox is on, regardless of the other one");
            Check(alwaysPublic.All(b => !Send(b, false, false) && !Send(b, true, false)),
                  "General/LFG/Auction withheld with the public-chat checkbox off");
            Check(alwaysPublic.All(b => Send(b, false, true) && Send(b, true, true)),
                  "General/LFG/Auction sent once the public-chat checkbox is on");

            Check(ChatFilter.ShouldSend("garbage with no prefix", false, false),
                  "a line with no timestamp prefix is not chat and is kept");
        }

        // ---- 8. LogTail actually drops what ChatFilter says to drop ----
        {
            var mixed = new List<string>
            {
                "(1786316100)[Sun Aug  9 18:53:47 2026] YOU hit a training dummy for 50 crushing damage.",
                "(1786316100)[Sun Aug  9 18:53:47 2026] \\aPC -1 Moklok:Moklok\\/a tells you, \"secret\"",
                "(1786316101)[Sun Aug  9 18:53:48 2026] \\aPC 1 Aros:Aros\\/a says to the group, \"pull in 5\"",
                "(1786316102)[Sun Aug  9 18:53:49 2026] \\aPC -1 Evoxx:Evoxx\\/a tells General (2), \"anyone up\"",
            };
            var mixedPath = WriteLog(mixed);

            var neither = new LogTail(2, line => ChatFilter.ShouldSend(line, false, false)) { BulkMode = true };
            neither.Resume(mixedPath, 0);
            var gotNeither = new List<string>();
            while (!neither.AtEnd) gotNeither.AddRange(neither.Read().SelectMany(b => b.Lines));
            gotNeither.AddRange(neither.Drain().SelectMany(b => b.Lines));
            Check(gotNeither.Count == 1 && !gotNeither.Any(l => l.Contains("secret")
                    || l.Contains("pull in 5") || l.Contains("anyone up")),
                  "both checkboxes off: only combat survives (" + gotNeither.Count + "/1)");

            var both = new LogTail(2, line => ChatFilter.ShouldSend(line, true, true)) { BulkMode = true };
            both.Resume(mixedPath, 0);
            var gotBoth = new List<string>();
            while (!both.AtEnd) gotBoth.AddRange(both.Read().SelectMany(b => b.Lines));
            gotBoth.AddRange(both.Drain().SelectMany(b => b.Lines));
            Check(gotBoth.Count == 3 && gotBoth.Any(l => l.Contains("pull in 5"))
                    && gotBoth.Any(l => l.Contains("anyone up")) && !gotBoth.Any(l => l.Contains("secret")),
                  "both checkboxes on: group and public chat survive, the tell still never does ("
                  + gotBoth.Count + "/3)");

            // The cursor must land on real, contiguous file offsets regardless of
            // how much of the second's content was filtered out — Uploader.Rewind
            // re-reads from these, and a mid-line offset would decapitate a line.
            Check(both.Offset == new FileInfo(mixedPath).Length,
                  "the read cursor still reaches real EOF when lines are being dropped");

            File.Delete(mixedPath);
        }

        File.Delete(path);
        Console.WriteLine(failures == 0 ? "\nALL PASS" : "\n" + failures + " FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static string Second(string line)
    {
        var close = line.IndexOf(')');
        return close > 0 ? line.Substring(0, close) : line;
    }
}
