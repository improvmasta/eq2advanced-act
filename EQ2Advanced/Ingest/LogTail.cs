using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EQ2Advanced.Ingest
{
    /// <summary>One batch's worth of lines, and the exact byte range of the log
    /// file they came out of.
    ///
    /// The range is per BATCH, not per read. It used to be per read — every
    /// batch a <see cref="LogTail.Read"/> produced was stamped with the cursor
    /// as it stood after the whole read — and that is what let
    /// <see cref="Uploader"/> "rewind" a failed batch to a point PAST the lines
    /// it had not sent yet. One raid lost 191 seconds that way: a 4 MB catch-up
    /// read, one failed send, and everything after it in that read was stepped
    /// over in silence. A batch owns its own bytes, so rewinding to
    /// <see cref="StartOffset"/> re-reads exactly the lines that did not land.
    /// </summary>
    public class LineBatch
    {
        public List<string> Lines = new List<string>();
        /// <summary>Byte offset of the first line in this batch.</summary>
        public long StartOffset;
        /// <summary>Byte offset just past the last line in this batch.</summary>
        public long EndOffset;
        /// <summary>Log timestamp of the last line, for the status display.</summary>
        public long LastTs;
        /// <summary>Bytes this batch covers — the batches are contiguous, so the
        /// range is also the size, and that is what the size cap measures.</summary>
        public long Bytes => EndOffset - StartOffset;
    }

    /// <summary>
    /// Reads new lines out of the EQ2 log file ACT is watching, and cuts them
    /// into batches on log-SECOND boundaries.
    ///
    /// Two decisions worth keeping:
    ///
    /// * We tail the FILE, not ACT's BeforeLogLineRead event. ACT hands its
    ///   plugins a line it has already touched (EQ2's embedded link markup is
    ///   the visible case), and the server's parser was written and golden-tested
    ///   against the bytes EQ2 actually wrote. Sending anything else would make
    ///   a streamed night differ from the same file uploaded, which is exactly
    ///   the property the server proves in its test suite. ACT is still the
    ///   source of WHICH file to read — it already solved log discovery.
    ///
    /// * A second is never split across two batches. The server's line dedupe
    ///   keys on (occurrence ordinal within the batch, line), so two identical
    ///   hits in the same second are distinguished by their ordinal — and if
    ///   that second arrived in two pieces, both pieces would call their line
    ///   "occurrence 0" and one real hit would be swallowed as a duplicate.
    ///   Lines from the newest second are therefore held back until a later
    ///   second appears, or the log goes quiet (<see cref="IdleFlushSeconds"/>).
    /// </summary>
    public class LogTail
    {
        private static readonly Regex PrefixRe = new Regex(@"^\((\d{10})\)", RegexOptions.Compiled);

        /// <summary>How long the newest second may sit unsent while nothing new
        /// is written. Below the server's 7s encounter gap, so holding a second
        /// back never delays a fight card.
        ///
        /// This is the END of a pull: mid-fight the next second always arrives
        /// and releases the held one immediately, but when the log goes quiet
        /// the last second of the fight sits here — which is exactly the moment
        /// everybody is reading the meter. 1.5s still leaves a wide margin
        /// under the 7s gap.</summary>
        public const double IdleFlushSeconds = 1.5;

        /// <summary>Hard ceilings on one batch, so a request can never be one the
        /// server refuses. `Emit` packs CONSECUTIVE seconds without limit — the
        /// window compares against the last second added, not the first — so a
        /// busy raid minute is one batch until something stops it, and a 24-man
        /// pull has produced 9,000-line batches in the field. The server caps a
        /// body at 2 MB gzipped and answers 413, which is final: the batch would
        /// never send, however often it was retried.
        ///
        /// Both limits cut on a SECOND boundary like every other cut here, so
        /// they cannot break the dedupe contract (see the class remarks).
        /// 1 MB of raw log gzips to well under the server's cap.</summary>
        public const int MaxBatchLines = 5000;
        public const long MaxBatchBytes = 1024 * 1024;

        private readonly int _windowSeconds;

        /// <summary>Optional per-line gate applied before a line ever enters a
        /// batch — null sends everything (the default, and what every existing
        /// `tools/tailcheck` scenario still exercises). `Uploader` passes
        /// `ChatFilter.ShouldSend`, read against live settings, so private chat
        /// is dropped here rather than merely left unstored on the server.
        /// Filtering happens strictly after the byte-offset bookkeeping below,
        /// so a dropped line still costs nothing to the cursor: it advances past
        /// those bytes exactly as if the game had never written them.</summary>
        private readonly System.Func<string, bool> _shouldSend;
        private string _path;
        private long _offset;
        private string _partial = "";        // bytes after the last newline
        private long _partialStart;          // byte offset _partial begins at
        private readonly List<string> _held = new List<string>();  // newest second, not yet safe to send
        private long _heldSecond = -1;
        private long _heldStart;             // byte range of the held second
        private long _heldEnd;
        private DateTime _heldSince = DateTime.MinValue;

        public LogTail(int windowSeconds, System.Func<string, bool> shouldSend = null)
        {
            _windowSeconds = Math.Max(1, windowSeconds);
            _shouldSend = shouldSend;
        }

        public string Path => _path;
        public long Offset => _offset;

        /// <summary>How far the SERVER has acknowledged, which is the only thing
        /// that makes a byte safe to move past. The read cursor runs ahead of it
        /// by design — a read pulls up to 4 MB and yields many batches — and the
        /// distance between the two is precisely the log that would be lost if
        /// something rewound to the wrong place. <see cref="Commit"/> moves it;
        /// <see cref="Rewind"/> refuses to go past it.</summary>
        private long _acked;

        /// <summary>Set when the cursor was asked to do something that cannot be
        /// right — to rewind to a point the server has never acknowledged. The
        /// tail refuses and re-reads from the acknowledged mark instead, because
        /// skipping quietly is how the one bug this class has ever had cost a
        /// raid three minutes of a kill. <see cref="Uploader"/> surfaces it as an
        /// error on the tab.</summary>
        public string Anomaly { get; private set; }

        public string TakeAnomaly()
        {
            var value = Anomaly;
            Anomaly = null;
            return value;
        }

        /// <summary>Reading a finished file rather than following a live one.
        /// Turns off the idle flush: wall-clock time means nothing here, and a
        /// slow read that tripped it would split a second across two batches at
        /// whatever byte the read happened to stop on. The final second goes out
        /// via <see cref="Drain"/> once the file is exhausted.</summary>
        public bool BulkMode;

        /// <summary>True once every byte written so far has been read out.</summary>
        public bool AtEnd
        {
            get
            {
                try { return _offset >= new FileInfo(_path).Length; }
                catch { return true; }
            }
        }

        /// <summary>
        /// Point at a log file. `fromEnd` starts at the current end of file —
        /// switching live upload on means "from now on", not "replay today".
        /// </summary>
        public void Open(string path, bool fromEnd)
        {
            _path = path;
            _partial = "";
            _held.Clear();
            _heldSecond = -1;
            _offset = 0;
            if (fromEnd)
            {
                try { _offset = new FileInfo(path).Length; }
                catch { _offset = 0; }
            }
            _partialStart = _offset;
            _acked = _offset;
        }

        /// <summary>Resume a file we were already tailing (ACT restart, or the
        /// same session re-enabled). A file shorter than our offset was rotated
        /// or replaced, so start it over.</summary>
        public void Resume(string path, long offset)
        {
            Open(path, false);
            long length;
            try { length = new FileInfo(path).Length; }
            catch { length = 0; }
            _offset = offset > length ? 0 : offset;
            _partialStart = _offset;
            _acked = _offset;
        }

        /// <summary>Roll the cursor back to `offset` — used when a batch failed
        /// to send, so the same lines are read again next time. The log file is
        /// the spool: nothing is queued in memory or on disk, and a batch is
        /// only "done" once the server has it.
        ///
        /// A rewind may never land past <see cref="Commit"/> — the acknowledged
        /// mark. That is the whole invariant, and the bug this guard exists for
        /// slipped through a weaker version of it: the caller rewound to the
        /// cursor's own position, which moves nothing forward and looks
        /// perfectly innocent, yet the cursor was 4 MB of unsent log ahead of
        /// what the server had. Measuring against the ACK instead catches it.
        /// Refuse and re-read from the mark; re-sending a line the server
        /// already has costs one counted duplicate, and losing one costs a raid
        /// its parse.</summary>
        public void Rewind(long offset)
        {
            if (offset > _acked)
            {
                Anomaly = "Refused to skip " + (offset - _acked) + " bytes the server "
                        + "never acknowledged; re-reading them instead. Nothing was lost, "
                        + "but this is a bug — please report it.";
                offset = _acked;
            }
            _offset = offset;
            _partial = "";
            _partialStart = offset;
            _held.Clear();
            _heldSecond = -1;
        }

        /// <summary>The server has this far. Called with a batch's `EndOffset`
        /// once it is safely stored, and nothing may ever rewind past it.</summary>
        public void Commit(long offset)
        {
            if (offset > _acked) _acked = offset;
        }

        private static long SecondOf(string line, long fallback)
        {
            var m = PrefixRe.Match(line);
            return m.Success ? long.Parse(m.Groups[1].Value) : fallback;
        }

        /// <summary>
        /// Read whatever the game has written since last time and return the
        /// batches that are safe to send. Returns an empty list when there is
        /// nothing new, or when everything new is still inside the second we
        /// are holding back.
        /// </summary>
        public List<LineBatch> Read()
        {
            var batches = new List<LineBatch>();
            if (string.IsNullOrEmpty(_path)) return batches;

            var text = ReadNewText();
            var complete = new List<TailLine>();
            if (text.Length > 0)
            {
                var buffer = _partial + text;
                // where buffer[0] sits in the file, so every line can carry its
                // own byte range out of here
                var at = _partialStart;
                var start = 0;
                for (var i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] != '\n') continue;
                    var raw = buffer.Substring(start, i - start);
                    // the bytes this line occupies, CR and LF included — a blank
                    // line is dropped but still advances the cursor
                    var width = Encoding.UTF8.GetByteCount(raw) + 1;
                    var line = raw.TrimEnd('\r');
                    if (line.Length > 0)
                        complete.Add(new TailLine { Text = line, Start = at, End = at + width });
                    at += width;
                    start = i + 1;
                }
                // A log being written to ends mid-line as often as not. Keep the
                // tail until its newline arrives rather than sending half a hit.
                _partial = buffer.Substring(start);
                _partialStart = at;
            }

            foreach (var line in complete)
            {
                // Dropped here, never held or batched — but the byte range this
                // line occupies was already folded into `at`/`_partialStart`
                // above, so the read cursor advances past it exactly as if the
                // game had never written it. A resend re-reads the same bytes
                // and re-derives the same drop, deterministically.
                if (_shouldSend != null && !_shouldSend(line.Text)) continue;

                var second = SecondOf(line.Text, _heldSecond < 0 ? 0 : _heldSecond);
                if (_heldSecond < 0)
                {
                    _heldSecond = second;
                    _heldSince = DateTime.UtcNow;
                    _heldStart = line.Start;
                }
                if (second != _heldSecond)
                {
                    // the held second can no longer grow: it is safe to send
                    Emit(batches, _held, _heldSecond, _heldStart, _heldEnd);
                    _held.Clear();
                    _heldSecond = second;
                    _heldSince = DateTime.UtcNow;
                    _heldStart = line.Start;
                }
                _held.Add(line.Text);
                _heldEnd = line.End;
            }

            // nothing newer has arrived for a while: the held second is done too
            if (!BulkMode && _held.Count > 0
                && (DateTime.UtcNow - _heldSince).TotalSeconds >= IdleFlushSeconds)
            {
                Emit(batches, _held, _heldSecond, _heldStart, _heldEnd);
                _held.Clear();
                _heldSecond = -1;
            }

            return batches;
        }

        /// <summary>A complete line and the bytes it came from.</summary>
        private struct TailLine
        {
            public string Text;
            public long Start;
            public long End;
        }

        /// <summary>Send everything, held second included — for closing a session
        /// or finishing a backfill, where nothing more is coming.</summary>
        public List<LineBatch> Drain()
        {
            var batches = Read();
            if (_held.Count > 0)
            {
                Emit(batches, _held, _heldSecond, _heldStart, _heldEnd);
                _held.Clear();
                _heldSecond = -1;
            }
            return batches;
        }

        /// <summary>Append one whole second to the last batch, or start a new one
        /// if that batch already spans the configured window or has grown to the
        /// size the server will refuse.
        ///
        /// Every cut here is on a second boundary, and that is the invariant, not
        /// a coincidence: the server numbers a line's occurrence within the BATCH,
        /// so a second arriving in two pieces would have both pieces claim
        /// occurrence 0 and one real hit would be swallowed as a duplicate.</summary>
        private void Emit(List<LineBatch> batches, List<string> lines, long second,
                          long start, long end)
        {
            if (lines.Count == 0) return;
            var last = batches.Count > 0 ? batches[batches.Count - 1] : null;
            if (last == null
                || second - last.LastTs >= _windowSeconds || second < last.LastTs
                || last.Lines.Count + lines.Count > MaxBatchLines
                || end - last.StartOffset > MaxBatchBytes)
            {
                last = new LineBatch { StartOffset = start };
                batches.Add(last);
            }
            last.Lines.AddRange(lines);
            last.LastTs = second;
            last.EndOffset = end;
        }

        private string ReadNewText()
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists) return "";
                if (info.Length < _offset)
                {
                    // truncated or replaced under us — take it from the top
                    _offset = 0;
                    _partial = "";
                    _partialStart = 0;
                    _acked = 0;
                }
                if (info.Length == _offset) return "";

                // FileShare.ReadWrite|Delete: the game holds this file open and
                // writes to it constantly. Anything stricter fails to open at all.
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_offset, SeekOrigin.Begin);
                    var length = (int)Math.Min(info.Length - _offset, 4 * 1024 * 1024);
                    var buffer = new byte[length];
                    var read = fs.Read(buffer, 0, length);
                    _offset += read;
                    // EQ2 logs are UTF-8; a multi-byte character split across two
                    // reads would otherwise become a replacement char mid-name.
                    return Utf8Safe(buffer, read);
                }
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private string Utf8Safe(byte[] buffer, int count)
        {
            // Find the last lead byte, then keep the whole sequence only if all
            // of it arrived. A partial one is left in the file and re-read next
            // tick — decoding it now would put a replacement character in the
            // middle of somebody's name, and that name is a dictionary key on
            // the server.
            var end = count;
            var i = count - 1;
            var continuations = 0;
            while (i >= 0 && (buffer[i] & 0xC0) == 0x80) { i--; continuations++; }
            if (i >= 0)
            {
                var lead = buffer[i];
                var needed = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
                if (needed > continuations + 1) end = i;
            }
            if (end < count) _offset -= (count - end);
            return Encoding.UTF8.GetString(buffer, 0, end);
        }
    }
}
