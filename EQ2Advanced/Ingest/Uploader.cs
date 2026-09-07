using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Advanced_Combat_Tracker;
using EQ2Advanced.Core;
using EQ2Advanced.Net;

namespace EQ2Advanced.Ingest
{
    public class UploaderStatus
    {
        public bool Running;
        public string Message = "Idle.";
        public bool IsError;
        public int LinesSent;
        public int Duplicates;
        public int? SessionId;
        public DateTime? LastSendUtc;
    }

    /// <summary>
    /// The worker: tails the log ACT is reading and posts it to eq2advanced.
    ///
    /// Failure model, which is most of the design: <b>the log file is the
    /// queue</b>. The read cursor only moves past lines the server has
    /// acknowledged, so a dropped connection, a restarted server or a closed
    /// laptop lid all recover by rewinding and reading the same bytes again.
    /// Nothing is buffered in memory that a crash could lose, and there is no
    /// spool file to corrupt, go stale, or fill a disk.
    ///
    /// Three things that invariant depends on, each of which was once wrong:
    ///
    /// * <b>Rewind to the BATCH, not to the read.</b> One `Read()` yields many
    ///   batches, and the cursor is already past all of them by the time the
    ///   first one is sent. Rewinding to `tail.Offset` therefore skipped every
    ///   unsent batch in that read — silently, and worst exactly when it
    ///   mattered, because a tail catching up reads 4 MB at a time. A raid lost
    ///   the first 191 seconds of a kill to it. `LineBatch.StartOffset` is the
    ///   only offset to rewind to.
    ///
    /// * <b>A resend gets a new batch id.</b> The id makes one REQUEST
    ///   idempotent (ApiClient's own retries), not one stretch of log. Out here
    ///   the lines are re-read and re-cut, so a resend may carry more than the
    ///   attempt that failed — and the server answers a known id from its record
    ///   without storing anything. Line-level dedupe covers the overlap instead.
    ///
    /// * <b>Only auth is fatal.</b> Anything else that refuses a batch is
    ///   waited out. An uploader that returns mid-raid uploads nothing for the
    ///   rest of the night; one that backs off catches up by design.
    /// </summary>
    public class Uploader
    {
        private readonly Settings _settings;
        private readonly ApiClient _api;
        private readonly LogCursorStore _cursors;
        private Thread _thread;
        private CancellationTokenSource _cancel;
        private string _activeCharacter;

        private readonly object _lock = new object();
        private UploaderStatus _status = new UploaderStatus();

        /// <summary>Raised on the worker thread; the UI marshals it.</summary>
        public event Action<UploaderStatus> StatusChanged;

        public Uploader(Settings settings, ApiClient api)
        {
            _settings = settings;
            _api = api;
            var configDir = Path.GetDirectoryName(JsonStore.ConfigPath);
            _cursors = new LogCursorStore(Path.Combine(configDir, "EQ2Advanced.Cursors"));
        }

        /// <summary>Read live, not captured once at Run()/BackfillOne() start —
        /// flipping either checkbox mid-raid takes effect on the very next line
        /// without restarting the uploader or the tail.</summary>
        private bool ChatGate(string line) =>
            ChatFilter.ShouldSend(line, _settings.ShareChat, _settings.SharePublicChat);

        public UploaderStatus Status { get { lock (_lock) return Copy(_status); } }

        private static UploaderStatus Copy(UploaderStatus s) => new UploaderStatus
        {
            Running = s.Running, Message = s.Message, IsError = s.IsError,
            LinesSent = s.LinesSent, Duplicates = s.Duplicates,
            SessionId = s.SessionId, LastSendUtc = s.LastSendUtc,
        };

        private void Report(string message, bool isError = false, Action<UploaderStatus> mutate = null)
        {
            UploaderStatus snapshot;
            lock (_lock)
            {
                _status.Message = message;
                _status.IsError = isError;
                if (mutate != null) mutate(_status);
                snapshot = Copy(_status);
            }
            var handler = StatusChanged;
            if (handler != null) handler(snapshot);
        }

        public bool IsRunning { get { lock (_lock) return _status.Running; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_status.Running) return;
                _status.Running = true;
            }
            _cancel = new CancellationTokenSource();
            _thread = new Thread(() => Run(_cancel.Token))
            {
                IsBackground = true,
                Name = "eq2advanced-uploader",
            };
            _thread.Start();
        }

        /// <summary>Stop tailing. `closeSession` tells the server the night is
        /// over, which triggers the rebuild that makes the streamed session
        /// identical to an uploaded file — worth doing on a clean stop, and
        /// worth skipping when ACT is just closing (the server closes a quiet
        /// session by itself after 30 minutes).</summary>
        public void Stop(bool closeSession)
        {
            Thread thread;
            lock (_lock)
            {
                if (!_status.Running) return;
                _status.Running = false;
                thread = _thread;
            }
            if (_cancel != null) _cancel.Cancel();
            if (thread != null && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(5)))
                Report("Uploader did not stop cleanly.", true);
            if (closeSession)
            {
                try { _api.Done(_activeCharacter ?? _settings.CharacterName,
                                CancellationToken.None); }
                catch (Exception ex) { Report("Could not close the session: " + ex.Message, true); }
            }
            Report(closeSession ? "Stopped. The raid is finished on the site." : "Stopped.");
        }

        private static string CurrentLogPath()
        {
            try { return ActGlobals.oFormActMain.LogFilePath; }
            catch { return null; }
        }

        /// <summary>ACT scans the existing EQ2 log for its latest zone when it
        /// starts, including when EQ2 was already running. That is the one line
        /// this file tail can miss when it deliberately starts at EOF.</summary>
        private static string CurrentZone()
        {
            try
            {
                var main = ActGlobals.oFormActMain;
                var property = main.GetType().GetProperty("CurrentZone");
                var zone = property == null ? null
                    : Convert.ToString(property.GetValue(main, null));
                if (string.IsNullOrWhiteSpace(zone)
                    || string.Equals(zone.Trim(), "Unknown Zone",
                                     StringComparison.OrdinalIgnoreCase))
                    return null;
                return zone.Trim();
            }
            catch { return null; }
        }

        private void Run(CancellationToken cancel)
        {
            var tail = new LogTail(_settings.WindowSeconds, ChatGate);
            string openPath = null;
            string character = null;
            var failures = 0;               // consecutive failed sends, for the backoff

            Report("Waiting for ACT to open an EverQuest II log...");

            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var path = CurrentLogPath();
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        Report("Waiting for ACT to open an EverQuest II log...");
                        if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) break;
                        continue;
                    }

                    if (!string.Equals(path, openPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Every logfile has its own durable acknowledged point.
                        // A genuinely new file starts at EOF; one this plugin
                        // has followed before resumes independently of whatever
                        // the account's other ACT processes are uploading.
                        var savedOffset = _cursors.Load(path);
                        var migratedLegacyCursor = false;
                        if (!savedOffset.HasValue
                            && string.Equals(path, _settings.LastLogPath,
                                             StringComparison.OrdinalIgnoreCase)
                            && _settings.LastOffset > 0)
                        {
                            // One-time migration from the pre-multibox cursor.
                            savedOffset = _settings.LastOffset;
                            migratedLegacyCursor = true;
                        }
                        if (savedOffset.HasValue)
                        {
                            tail.Resume(path, savedOffset.Value);
                            if (migratedLegacyCursor)
                            {
                                // Resume normalizes a stale offset past EOF to
                                // zero. Persist that safe value, never the raw
                                // legacy value, before new bytes are held only
                                // in memory.
                                _cursors.Save(path, tail.Offset);
                            }
                        }
                        else
                        {
                            tail.Open(path, fromEnd: true);
                            // Bytes that predate enabling this tail are skipped
                            // deliberately and therefore safe to resume past.
                            // Persist the boundary now, before a held first
                            // second exists only in memory.
                            _cursors.Save(path, tail.Offset);
                        }
                        openPath = path;
                        // whose log this is, straight off the filename — ACT
                        // switches files when you switch characters, so this
                        // follows alts without anybody configuring anything
                        character = EqLog.CharacterFromPath(path);
                        _activeCharacter = character;
                        _settings.CharacterName = character ?? "";
                        Report(character != null
                            ? "Uploading as " + character + " (" + Path.GetFileName(path) + ")."
                            : "Following " + Path.GetFileName(path)
                              + " — can't tell which character, so it can't be sent. "
                              + "EQ2 names logs eq2log_<character>.txt.");
                    }

                    // Nothing to attribute the lines to: hold rather than guess.
                    // The server would 422 anyway, and a wrong name would file
                    // somebody's raid under a character that doesn't exist.
                    if (character == null)
                    {
                        if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) break;
                        continue;
                    }

                    var stalled = false;
                    var batches = tail.Read();
                    foreach (var batch in batches)
                    {
                        if (cancel.IsCancellationRequested) break;
                        try
                        {
                            // A fresh batch id per attempt, deliberately. The id
                            // makes ONE request idempotent, and ApiClient already
                            // uses it that way for its own transport retries. Out
                            // here the lines are re-READ and re-CUT, so a resend
                            // can legitimately carry more of the log than the
                            // attempt that failed — and the server replays a known
                            // batch id by returning the stored answer and storing
                            // nothing, which would drop every extra line on the
                            // floor. Re-sending under a new id is safe because the
                            // dedupe is per LINE, and a line's occurrence ordinal
                            // is stable however the batches fall: the log second is
                            // part of the line, so identical strings only ever
                            // occur inside one second, and a second is never split.
                            var result = _api.SendBatch(Guid.NewGuid().ToString(), "live",
                                                        character, batch.Lines,
                                                        CurrentZone(), cancel);
                            failures = 0;
                            tail.Commit(batch.EndOffset);
                            _cursors.Save(path, batch.EndOffset);
                            // Retain the old fields only as a downgrade/migration
                            // aid. They are no longer saved on every batch: doing
                            // so is what made several ACT processes overwrite one
                            // another's only cursor.
                            _settings.LastLogPath = path;
                            _settings.LastOffset = batch.EndOffset;
                            Report(Describe(result), false, s =>
                            {
                                s.LinesSent += result.Accepted;
                                s.Duplicates += result.Duplicates;
                                s.SessionId = result.SessionId ?? s.SessionId;
                                s.LastSendUtc = DateTime.UtcNow;
                            });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Rewind to THIS batch's first byte — not to wherever
                            // the read finished. The lines after it in this read
                            // have not been sent either, and stepping over them is
                            // how a raid loses three minutes of a kill in silence.
                            tail.Rewind(batch.StartOffset);

                            // Auth is the only refusal a retry cannot fix: the
                            // pairing is gone and a human has to mint a token.
                            // Everything else — a restarting server, a proxy
                            // hiccup, a 4xx from something in the middle — is
                            // worth waiting out. The log file is the queue, so a
                            // stalled uploader catches up the moment it can, and
                            // an uploader that gave up mid-raid does not.
                            var api = ex as ApiException;
                            if (api != null && (api.StatusCode == 401 || api.StatusCode == 403))
                            {
                                Report(ex.Message, true);
                                lock (_lock) _status.Running = false;
                                return;
                            }

                            failures++;
                            stalled = true;
                            Report("Holding " + batch.Lines.Count + " lines to resend — "
                                   + ex.Message, true);
                            break;
                        }
                    }

                    var anomaly = tail.TakeAnomaly();
                    if (anomaly != null) Report(anomaly, true);

                    // Normal cadence while things are working; back off while they
                    // are not, so a server that is down is not hammered every 0.5s.
                    var wait = stalled
                        ? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))))
                        : TimeSpan.FromSeconds(Math.Max(0.25, _settings.CadenceSeconds));
                    if (cancel.WaitHandle.WaitOne(wait)) break;
                }
            }
            catch (OperationCanceledException) { /* Stop() */ }
            catch (Exception ex) { Report("Uploader stopped: " + ex.Message, true); }
            finally { lock (_lock) _status.Running = false; }
        }

        private static string Describe(BatchResult result)
        {
            if (result.Replayed) return "Resent a batch the server already had.";
            var text = "Sent " + result.Accepted + " line" + (result.Accepted == 1 ? "" : "s");
            if (result.Duplicates > 0) text += " (" + result.Duplicates + " already known)";
            return text + ".";
        }

        // ---- backfill: push logs that were written while we weren't running ----

        /// <summary>Where a bulk import has got to, for the UI.</summary>
        public class ImportProgress
        {
            public int FileIndex;        // 1-based
            public int FileCount;
            public string FileName;
            public long BytesSent;       // within the current file
            public long BytesTotal;      // of the current file
            public int LinesSent;        // across the whole import
            public int Duplicates;       // across the whole import
            public int? SessionId;       // the session the current file landed in
            public string Character;     // whose log this file is
            public string Note;          // set when a file is skipped or fails
        }

        /// <summary>
        /// Send existing log files end to end, one at a time, closing a session
        /// after each so every log becomes its own night on the site.
        ///
        /// Deliberately SEQUENTIAL and paced. The server allows one batch in
        /// flight per device token and answers a second one with 429, so
        /// parallelism would buy nothing but retries; and someone importing
        /// years of logs should not look like an attack on their own server. A
        /// short pause between batches keeps a bulk import from monopolising the
        /// box that is also serving the website.
        ///
        /// Overlap is free: the server drops lines it already has, so importing
        /// a night that was partly streamed live does not double-count it, and a
        /// re-run after a cancel picks up cheaply.
        ///
        /// One bad file does not stop the import — it is reported through
        /// `progress` and the run moves on.
        /// </summary>
        public void BackfillMany(IList<string> paths, Action<ImportProgress> progress,
                                 CancellationToken cancel)
        {
            var linesSent = 0;
            var duplicates = 0;

            for (var i = 0; i < paths.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var path = paths[i];
                var state = new ImportProgress
                {
                    FileIndex = i + 1,
                    FileCount = paths.Count,
                    FileName = Path.GetFileName(path),
                    LinesSent = linesSent,
                    Duplicates = duplicates,
                };

                try
                {
                    var info = new FileInfo(path);
                    state.BytesTotal = info.Exists ? info.Length : 0;
                    if (!info.Exists || info.Length == 0)
                    {
                        state.Note = info.Exists ? "empty, skipped" : "missing, skipped";
                        if (progress != null) progress(state);
                        continue;
                    }
                    // Per FILE, not per import: a logs folder spans alts, and
                    // each one has to land under its own character.
                    state.Character = EqLog.CharacterFromPath(path);
                    if (state.Character == null)
                    {
                        state.Note = "skipped — not named eq2log_<character>.txt, "
                                   + "so there's no way to tell whose raid it is";
                        if (progress != null) progress(state);
                        continue;
                    }
                    if (progress != null) progress(state);

                    BackfillOne(path, state, ref linesSent, ref duplicates, progress, cancel);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A log that will not upload is worth saying out loud, but it
                    // is not a reason to abandon the other 40 files.
                    state.Note = "failed: " + ex.Message;
                    if (progress != null) progress(state);
                }
            }
        }

        private void BackfillOne(string path, ImportProgress state, ref int linesSent,
                                 ref int duplicates, Action<ImportProgress> progress,
                                 CancellationToken cancel)
        {
            var tail = new LogTail(_settings.WindowSeconds, ChatGate) { BulkMode = true };
            tail.Resume(path, 0);

            var fileFailures = 0;
            var finished = false;
            while (!finished)
            {
                cancel.ThrowIfCancellationRequested();
                // Only the LAST read may drain the second being held: flushing it
                // at an arbitrary 4 MB read boundary would split that second
                // across two batches, and the server's per-batch occurrence
                // ordinals would then swallow real repeated lines as duplicates.
                finished = tail.AtEnd;
                var batches = finished ? tail.Drain() : tail.Read();
                foreach (var batch in batches)
                {
                    cancel.ThrowIfCancellationRequested();
                    BatchResult result;
                    try
                    {
                        result = _api.SendBatch(Guid.NewGuid().ToString(), "backfill",
                                                state.Character, batch.Lines, null, cancel);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // An import is long and unattended, so one blip must not
                        // cost the rest of the file. Rewind to this batch's own
                        // first byte and let the loop read it again; give up on
                        // the file only once it is clearly not a blip, and let
                        // BackfillMany report it and move to the next log.
                        var api = ex as ApiException;
                        var fatal = api != null
                                    && (api.StatusCode == 401 || api.StatusCode == 403);
                        if (fatal || ++fileFailures > 4) throw;
                        tail.Rewind(batch.StartOffset);
                        // the rewind put bytes back in front of the cursor, so
                        // this pass is no longer the last one
                        finished = false;
                        state.Note = "retrying — " + ex.Message;
                        if (progress != null) progress(state);
                        Wait(TimeSpan.FromSeconds(Math.Pow(2, fileFailures)), cancel);
                        break;
                    }
                    fileFailures = 0;
                    tail.Commit(batch.EndOffset);
                    linesSent += result.Accepted;
                    duplicates += result.Duplicates;
                    state.SessionId = result.SessionId ?? state.SessionId;
                    state.BytesSent = batch.EndOffset;
                    state.LinesSent = linesSent;
                    state.Duplicates = duplicates;
                    if (progress != null) progress(state);
                    Pace(cancel);
                }

                var anomaly = tail.TakeAnomaly();
                if (anomaly != null)
                {
                    state.Note = anomaly;
                    if (progress != null) progress(state);
                }
            }

            // close this log's session before the next file opens a new one,
            // so a folder of logs becomes a list of nights rather than one blob
            var closed = _api.Done(state.Character, cancel);
            if (closed.HasValue) state.SessionId = closed;
            state.Note = "done";
            if (progress != null) progress(state);
        }

        /// <summary>Breathe between batches during a bulk import.</summary>
        private void Pace(CancellationToken cancel)
        {
            var ms = _settings.ImportPauseMs;
            if (ms <= 0) return;
            if (cancel.WaitHandle.WaitOne(ms)) throw new OperationCanceledException();
        }

        /// <summary>Back off, and treat a cancel during the wait as a cancel.</summary>
        private static void Wait(TimeSpan span, CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(span)) throw new OperationCanceledException();
        }
    }
}
