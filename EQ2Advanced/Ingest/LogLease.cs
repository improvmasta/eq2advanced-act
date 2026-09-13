using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace EQ2Advanced.Ingest
{
    /// <summary>
    /// A claim on one logfile: only one plugin instance may tail it.
    ///
    /// THIS IS A CORRECTNESS GUARD, NOT TIDINESS. Until now each ACT followed a
    /// different file, so two instances tailing the same log could not happen.
    /// Reading every log makes it the default: two ACTs on one PC, or the
    /// stable uploader and this test build enabled side by side, and every one
    /// of them finds all four logs.
    ///
    /// The reason that is not merely wasteful is the server's line dedupe
    /// (`backend/pipeline/live.py`): its key is the occurrence ordinal of a line
    /// WITHIN ITS BATCH, which is only equivalent to a per-second ordinal while
    /// every sender cuts batches the same way. Two senders that started at
    /// different offsets do not, so the same second can arrive cut two ways and
    /// a genuine repeated hit — the third `Reyfiler hits ... for 0` in one
    /// second — gets swallowed as a duplicate of the first. That is silent
    /// UNDER-counting of a real parse, which is the one failure mode this
    /// plugin is built to never have.
    ///
    /// So: one tailer per file, decided by a lease file next to the cursor.
    /// It is deliberately in the SHARED cursor directory rather than a per
    /// channel one — a lease the other build cannot see protects nothing.
    ///
    /// Crash safety comes from the heartbeat plus the owning process's identity
    /// (pid AND its start time, because Windows reuses pids). A lease whose
    /// owner is gone is takeable at once; one merely gone quiet is takeable
    /// after <see cref="StaleSeconds"/>.
    /// </summary>
    public sealed class LogLease : IDisposable
    {
        /// <summary>How long a lease outlives its last heartbeat. Comfortably
        /// more than the uploader's tick, so a busy send never looks dead.</summary>
        public const int StaleSeconds = 60;

        private readonly string _directory;
        private readonly string _path;
        private readonly string _key;
        private readonly int _pid;
        private readonly long _started;
        private bool _held;

        public LogLease(string directory, string logPath)
        {
            _directory = directory;
            _key = LogCursorStore.KeyFor(logPath);
            _path = Path.Combine(directory, _key + ".lease");
            _pid = Process.GetCurrentProcess().Id;
            _started = ProcessStartTicks(_pid) ?? 0;
        }

        public bool Held => _held;

        /// <summary>Who holds it, when somebody else does — shown on the tab so
        /// "why is this log not uploading" has an answer on screen.</summary>
        public string HeldByOther { get; private set; }

        /// <summary>Take the lease, or renew one already held. False means
        /// somebody else has it and <see cref="HeldByOther"/> says who.</summary>
        public bool Acquire()
        {
            using (var mutex = new Mutex(false, "EQ2Advanced-LogLease-" + _key))
            {
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
                try
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var current = Read();
                    if (current != null && !Mine(current) && !Expired(current, now))
                    {
                        HeldByOther = "ACT process " + current.Pid;
                        _held = false;
                        return false;
                    }
                    Write(now);
                    HeldByOther = null;
                    _held = true;
                    return true;
                }
                catch
                {
                    // A lease that cannot be written must not stop the upload:
                    // the hazard it guards against needs TWO tailers, and being
                    // unable to write the file is not evidence of a second one.
                    HeldByOther = null;
                    _held = true;
                    return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>Let it go, so another instance can pick the log up without
        /// waiting out <see cref="StaleSeconds"/>.</summary>
        public void Release()
        {
            if (!_held) return;
            _held = false;
            try
            {
                using (var mutex = new Mutex(false, "EQ2Advanced-LogLease-" + _key))
                {
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        var current = Read();
                        if (current != null && Mine(current)) File.Delete(_path);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch { /* a lease left behind expires on its own */ }
        }

        public void Dispose() => Release();

        private sealed class Record
        {
            public int Pid;
            public long Started;
            public long Heartbeat;
        }

        private bool Mine(Record r) => r.Pid == _pid && r.Started == _started;

        private static bool Expired(Record r, long now)
        {
            if (now - r.Heartbeat > StaleSeconds) return true;
            // The owner died without releasing: its pid is gone, or has been
            // reused by something that started at a different time.
            var started = ProcessStartTicks(r.Pid);
            return started == null || started.Value != r.Started;
        }

        private static long? ProcessStartTicks(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return p.StartTime.ToUniversalTime().Ticks;
            }
            catch { return null; }
        }

        private Record Read()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var lines = File.ReadAllLines(_path);
                if (lines.Length < 3) return null;
                return new Record
                {
                    Pid = int.Parse(lines[0], CultureInfo.InvariantCulture),
                    Started = long.Parse(lines[1], CultureInfo.InvariantCulture),
                    Heartbeat = long.Parse(lines[2], CultureInfo.InvariantCulture),
                };
            }
            catch { return null; }
        }

        private void Write(long now)
        {
            Directory.CreateDirectory(_directory);
            var text = _pid.ToString(CultureInfo.InvariantCulture) + "\n"
                     + _started.ToString(CultureInfo.InvariantCulture) + "\n"
                     + now.ToString(CultureInfo.InvariantCulture) + "\n";
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }
    }
}
