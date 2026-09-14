using System;
using System.Collections.Generic;
using System.Threading;
using EQ2Advanced.Core;
using EQ2Advanced.Net;

namespace EQ2Advanced.Ingest
{
    /// <summary>What one followed log is doing, for the tab.</summary>
    public class LogStatus
    {
        public string Path;
        public string Character;
        public string Message = "";
        public bool Running;
        public bool IsError;
        /// <summary>Reading, but holding the bytes back until this character
        /// does something — see <see cref="Participation"/>.</summary>
        public bool Withholding;
        public int LinesSent;
        public int Duplicates;
        public int? SessionId;
    }

    /// <summary>
    /// One EQ2 logfile, followed on its own thread.
    ///
    /// A THREAD PER LOG, not a round-robin over one. `ApiClient` is synchronous
    /// with a 30-second timeout, so a single shared thread would let one boxed
    /// character's stalled send freeze the other three for half a minute — in
    /// the middle of the pull they are all in. The server is built for this: its
    /// in-flight lock is keyed `(token, character)` precisely so "different
    /// boxed characters on one account must not stall each other"
    /// (`backend/routers/ingest_api.py`), and every durable thing this class
    /// touches — cursor, lease — is already per logfile.
    ///
    /// Everything about the failure model is inherited from <see cref="Uploader"/>
    /// and unchanged: the log file is the queue, a failed batch rewinds to its
    /// own first byte, and only auth is fatal. What is new here is the GATE.
    ///
    /// <b>The gate withholds; it never queues and never skips.</b> While this
    /// character is not fighting, batches are read (so their lines can be
    /// judged) and simply not sent — and the acknowledged cursor stays where it
    /// was. The unsent bytes therefore live in the log file, exactly like every
    /// other unsent byte in this plugin, and the moment the character does
    /// something the tail rewinds to the acknowledged mark and the whole
    /// build-up goes out. That is what makes the gate safe to have on by
    /// default: being wrong about "not fighting" costs latency, not log.
    ///
    /// The one thing it must not do is hold on forever — a parked mule would
    /// pin its cursor at yesterday. Past <see cref="Settings.WithholdMaxBytes"/>
    /// the withheld run IS abandoned, deliberately and out loud, and the cursor
    /// moves to the end of the last complete BATCH rather than to the read
    /// cursor: a batch boundary is a line boundary and a second boundary, and
    /// resuming mid-line is how this plugin's worst bug announced itself.
    /// </summary>
    public sealed class LogStream
    {
        /// <summary>Renew the lease well inside <see cref="LogLease.StaleSeconds"/>,
        /// so a slow send never makes a live tailer look dead to another ACT.</summary>
        private const int LeaseRenewSeconds = 15;

        private readonly Settings _settings;
        private readonly ApiClient _api;
        private readonly LogCursorStore _cursors;
        private readonly LogLease _lease;
        private readonly Func<string, bool> _chatGate;
        private readonly Func<string, string> _zoneHint;
        private readonly Action _changed;

        private readonly object _lock = new object();
        private LogStatus _status;

        private Thread _thread;
        private CancellationTokenSource _cancel;

        public string Path { get; }
        public string Character { get; }

        /// <summary>Set when the server refused this stream's token. Auth is an
        /// ACCOUNT fact, not a log one, so <see cref="Uploader"/> reads this and
        /// takes every other stream down with it — retrying the other three
        /// logs against a revoked token is just noise on the tab.</summary>
        public bool AuthFailed { get; private set; }

        public LogStream(Settings settings, ApiClient api, LogCursorStore cursors,
                         string cursorDirectory, string path, string character,
                         Func<string, bool> chatGate, Func<string, string> zoneHint,
                         Action changed)
        {
            _settings = settings;
            _api = api;
            _cursors = cursors;
            _chatGate = chatGate;
            _zoneHint = zoneHint;
            _changed = changed;
            Path = path;
            Character = character;
            _lease = new LogLease(cursorDirectory, path);
            _status = new LogStatus { Path = path, Character = character, Message = "Waiting." };
        }

        public LogStatus Snapshot()
        {
            lock (_lock)
                return new LogStatus
                {
                    Path = _status.Path, Character = _status.Character,
                    Message = _status.Message, Running = _status.Running,
                    IsError = _status.IsError, Withholding = _status.Withholding,
                    LinesSent = _status.LinesSent, Duplicates = _status.Duplicates,
                    SessionId = _status.SessionId,
                };
        }

        public bool IsRunning { get { lock (_lock) return _status.Running; } }

        private void Report(string message, bool isError = false,
                            Action<LogStatus> mutate = null)
        {
            lock (_lock)
            {
                _status.Message = message;
                _status.IsError = isError;
                if (mutate != null) mutate(_status);
            }
            var changed = _changed;
            if (changed != null) changed();
        }

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
                Name = "eq2advanced-log-" + Character,
            };
            _thread.Start();
        }

        /// <summary>Stop following this log. `closeSession` finishes the night on
        /// the site for THIS character only — the others keep streaming.</summary>
        public void Stop(bool closeSession)
        {
            Thread thread;
            lock (_lock)
            {
                if (!_status.Running) { _lease.Release(); return; }
                _status.Running = false;
                thread = _thread;
            }
            if (_cancel != null) _cancel.Cancel();
            if (thread != null && thread.IsAlive) thread.Join(TimeSpan.FromSeconds(5));
            if (closeSession)
            {
                try { _api.Done(Character, CancellationToken.None); }
                catch (Exception ex) { Report("Could not close the session: " + ex.Message, true); }
            }
            _lease.Release();
            Report(closeSession ? "Stopped — the raid is finished on the site." : "Stopped.");
        }

        private void Run(CancellationToken cancel)
        {
            var tail = new LogTail(_settings.WindowSeconds, _chatGate);
            var participation = new Participation(Character);
            var failures = 0;
            var leaseRenewed = DateTime.MinValue;
            // The end of the newest COMPLETE batch this tail has produced. The
            // only offset the withhold cap may ever skip to; see the class
            // remarks for why the read cursor is not.
            long lastBatchEnd = -1;
            var wasOpen = false;

            try
            {
                Resume(tail);

                while (!cancel.IsCancellationRequested)
                {
                    if ((DateTime.UtcNow - leaseRenewed).TotalSeconds >= LeaseRenewSeconds)
                    {
                        if (!_lease.Acquire())
                        {
                            Report("Another ACT is already uploading this log ("
                                   + _lease.HeldByOther + "). Leaving it alone.", true);
                            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(20))) break;
                            continue;
                        }
                        leaseRenewed = DateTime.UtcNow;
                    }

                    var batches = tail.Read();
                    foreach (var batch in batches)
                    {
                        foreach (var line in batch.Lines) participation.Observe(line);
                        lastBatchEnd = batch.EndOffset;
                    }

                    var open = !_settings.OnlyWhenFighting
                               || participation.Active(_settings.FightingIdleSeconds);

                    if (_settings.OnlyWhenFighting && open && !wasOpen
                        && tail.Offset > tail.Acked)
                    {
                        // Just joined in. Everything withheld while this
                        // character stood around is still in the file, and the
                        // acknowledged mark is exactly where it starts — so one
                        // rewind replays the lot, including the opening seconds
                        // of the pull that tripped the gate. Re-read it next
                        // tick rather than sending a batch list that no longer
                        // describes the tail's position.
                        tail.Rewind(tail.Acked);
                        lastBatchEnd = -1;
                        wasOpen = true;
                        Report("Fighting — catching up.", false, s => s.Withholding = false);
                        continue;
                    }
                    wasOpen = open;

                    var stalled = false;
                    if (!open)
                    {
                        Exception publicError = null;
                        if (_settings.SharePublicChat)
                        {
                            foreach (var batch in batches)
                            {
                                var publicLines = new List<string>();
                                foreach (var line in batch.Lines)
                                {
                                    string body;
                                    if (ChatFilter.TrySplitBody(line, out body)
                                        && ChatFilter.Classify(body) == ChatChannel.Public)
                                        publicLines.Add(line);
                                }
                                if (publicLines.Count == 0) continue;
                                try
                                {
                                    _api.SendPublicChat(Character, publicLines, cancel);
                                    failures = 0;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    // The combat cursor is intentionally held.
                                    // Rewind the read cursor to this complete
                                    // batch so a failed relay is retried from
                                    // the log file, never an in-memory queue.
                                    tail.Rewind(batch.StartOffset);
                                    failures++;
                                    stalled = true;
                                    publicError = ex;
                                    break;
                                }
                            }
                        }
                        Withhold(tail, ref lastBatchEnd);
                        if (publicError != null)
                            Report("Public chat retrying: " + publicError.Message, true);
                    }
                    else
                    {
                        foreach (var batch in batches)
                        {
                            if (cancel.IsCancellationRequested) break;
                            if (!Send(tail, batch, ref failures, cancel, out stalled)) break;
                        }
                    }
                    if (AuthFailed) return;

                    var anomaly = tail.TakeAnomaly();
                    if (anomaly != null) Report(anomaly, true);

                    var wait = stalled
                        ? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))))
                        : TimeSpan.FromSeconds(Math.Max(0.25, _settings.CadenceSeconds));
                    if (cancel.WaitHandle.WaitOne(wait)) break;
                }
            }
            catch (OperationCanceledException) { /* Stop() */ }
            catch (Exception ex) { Report("Stopped: " + ex.Message, true); }
            finally
            {
                lock (_lock) _status.Running = false;
                var changed = _changed;
                if (changed != null) changed();
            }
        }

        /// <summary>Pick this log up where the server left off. Identical to the
        /// single-log path's rule: a log this plugin has followed before resumes
        /// from its own durable cursor, a new one starts at EOF and says so on
        /// disk before any byte is held only in memory.</summary>
        private void Resume(LogTail tail)
        {
            var saved = _cursors.Load(Path);
            if (saved.HasValue)
            {
                tail.Resume(Path, saved.Value);
            }
            else
            {
                tail.Open(Path, fromEnd: true);
                _cursors.Save(Path, tail.Offset);
            }
            Report("Following " + System.IO.Path.GetFileName(Path) + ".");
        }

        /// <summary>Hold everything read so far, or give up on it once the
        /// build-up is past the cap.</summary>
        private void Withhold(LogTail tail, ref long lastBatchEnd)
        {
            var held = tail.Offset - tail.Acked;
            if (held > _settings.WithholdMaxBytes && lastBatchEnd > tail.Acked)
            {
                var batchEnd = lastBatchEnd;
                // Giving up on the build-up. Move to a BATCH boundary, never the
                // read cursor: the read cursor can sit mid-line, and a cursor
                // that resumes mid-line is what turned one lost send into a
                // decapitated line on the server.
                tail.Commit(batchEnd);
                _cursors.Save(Path, batchEnd);
                lastBatchEnd = batchEnd;
                Report("Not fighting — skipped " + (held / (1024 * 1024))
                       + " MB of log for " + Character + ".", false,
                       s => s.Withholding = true);
                return;
            }
            Report(held > 0
                ? "Watching — holding " + (held / 1024) + " KB until " + Character
                  + " joins in." + (_settings.SharePublicChat ? " Public chat is separate." : "")
                : "Watching — " + Character + " is not fighting.",
                false, s => s.Withholding = true);
        }

        /// <summary>Send one batch. False means stop working through this read.</summary>
        private bool Send(LogTail tail, LineBatch batch, ref int failures,
                          CancellationToken cancel, out bool stalled)
        {
            stalled = false;
            try
            {
                var result = _api.SendBatch(Guid.NewGuid().ToString(), "live", Character,
                                            batch.Lines, _zoneHint(Path), cancel);
                failures = 0;
                tail.Commit(batch.EndOffset);
                _cursors.Save(Path, batch.EndOffset);
                Report(Describe(result), false, s =>
                {
                    s.Withholding = false;
                    s.LinesSent += result.Accepted;
                    s.Duplicates += result.Duplicates;
                    s.SessionId = result.SessionId ?? s.SessionId;
                });
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                tail.Rewind(batch.StartOffset);
                var api = ex as ApiException;
                if (api != null && (api.StatusCode == 401 || api.StatusCode == 403))
                {
                    AuthFailed = true;
                    Report(ex.Message, true);
                    return false;
                }
                failures++;
                stalled = true;
                Report("Holding " + batch.Lines.Count + " lines to resend — " + ex.Message,
                       true);
                return false;
            }
        }

        private static string Describe(BatchResult result)
        {
            if (result.Replayed) return "Resent a batch the server already had.";
            var text = "Sent " + result.Accepted + " line" + (result.Accepted == 1 ? "" : "s");
            if (result.Duplicates > 0) text += " (" + result.Duplicates + " already known)";
            return text + ".";
        }
    }
}
