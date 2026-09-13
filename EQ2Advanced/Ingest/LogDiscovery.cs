using System;
using System.Collections.Generic;
using System.IO;
using Advanced_Combat_Tracker;

namespace EQ2Advanced.Ingest
{
    /// <summary>One EQ2 logfile on disk, and whether anything is writing to it.</summary>
    public sealed class DiscoveredLog
    {
        public string Path;
        public string Character;
        /// <summary>Length read through an OPEN HANDLE — see
        /// <see cref="LogDiscovery.TrueLength"/>, which is the whole reason this
        /// class exists rather than a list of paths.</summary>
        public long Length;
        /// <summary>Grew since the previous scan, or was written to within
        /// <see cref="LogDiscovery.LiveWindowSeconds"/>.</summary>
        public bool Live;
        /// <summary>Nothing has been written for <see cref="LogDiscovery.StaleSeconds"/>
        /// — the client is closed or that character logged out.</summary>
        public bool Stale;
        public DateTime LastWriteUtc;
    }

    /// <summary>
    /// Finds every EverQuest II log worth following, instead of taking the one
    /// ACT happens to be reading.
    ///
    /// ACT is still where discovery STARTS — it already solved "where does this
    /// person keep EQ2", and the folder its current log sits in is the answer.
    /// What it cannot answer is the boxer's question, because ACT follows one
    /// file: the other three clients are writing their own logs in the same
    /// folder (or in a second install's), and ACT has no opinion about them.
    ///
    /// Layout, which is what the scan is shaped around:
    ///
    ///     &lt;EQ2 install&gt;\logs\&lt;Server&gt;\eq2log_&lt;Character&gt;.txt
    ///
    /// so a root is scanned at its own level AND one directory down — that
    /// covers being handed `...\logs\Wuoshi` (ACT's usual path) and `...\logs`
    /// alike, and picks up a second server folder without being told. Extra
    /// installs are a settings list, because multiboxers really do run several,
    /// and no amount of guessing finds a folder nobody mentioned.
    /// </summary>
    public static class LogDiscovery
    {
        /// <summary>Written to this recently = somebody is playing that character.</summary>
        public const int LiveWindowSeconds = 90;

        /// <summary>Quiet this long = the client is gone. The stream stops (and
        /// releases its lease); the server closes the session by itself after
        /// its own 30-minute idle, so nothing has to be decided here.</summary>
        public const int StaleSeconds = 900;

        /// <summary>A directory tree gone wrong — a logs folder pointed at a
        /// whole drive — must not turn a 5-second scan into a disk crawl.</summary>
        private const int MaxDirectories = 64;
        private const int MaxLogs = 64;

        /// <summary>The folder ACT's current log lives in, or null.</summary>
        public static string ActLogFolder()
        {
            try
            {
                var path = ActGlobals.oFormActMain.LogFilePath;
                if (string.IsNullOrEmpty(path)) return null;
                return Path.GetDirectoryName(path);
            }
            catch { return null; }
        }

        /// <summary>The path ACT itself is following, or null. Only this log may
        /// carry ACT's zone hint on a batch — see <see cref="Uploader"/>.</summary>
        public static string ActLogPath()
        {
            try
            {
                var path = ActGlobals.oFormActMain.LogFilePath;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch { return null; }
        }

        /// <summary>
        /// Every `eq2log_*.txt` under ACT's folder and the extra roots, with
        /// liveness decided against `previousLengths` (path -> length at the
        /// last scan, which this call updates).
        /// </summary>
        public static List<DiscoveredLog> Scan(IEnumerable<string> extraRoots,
                                               Dictionary<string, long> previousLengths)
        {
            var found = new List<DiscoveredLog>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;

            foreach (var dir in Roots(extraRoots))
            {
                if (found.Count >= MaxLogs) break;
                string[] files;
                try { files = Directory.GetFiles(dir, "eq2log_*.txt", SearchOption.TopDirectoryOnly); }
                catch { continue; }
                foreach (var file in files)
                {
                    if (found.Count >= MaxLogs) break;
                    string full;
                    try { full = Path.GetFullPath(file); }
                    catch { continue; }
                    if (!seen.Add(full)) continue;

                    var character = EqLog.CharacterFromPath(full);
                    if (character == null) continue;   // not named the way EQ2 names them

                    var length = TrueLength(full);
                    if (length < 0) continue;          // vanished between listing and opening

                    DateTime written;
                    try { written = File.GetLastWriteTimeUtc(full); }
                    catch { written = DateTime.MinValue; }

                    long before;
                    var grew = previousLengths != null
                               && previousLengths.TryGetValue(full, out before)
                               && length > before;
                    if (previousLengths != null) previousLengths[full] = length;

                    var age = (now - written).TotalSeconds;
                    found.Add(new DiscoveredLog
                    {
                        Path = full,
                        Character = character,
                        Length = length,
                        LastWriteUtc = written,
                        // Growth is the trustworthy half (see TrueLength); the
                        // timestamp is what answers on the FIRST scan, when
                        // there is nothing to compare against yet.
                        Live = grew || age <= LiveWindowSeconds,
                        Stale = !grew && age > StaleSeconds,
                    });
                }
            }

            found.Sort((a, b) => string.Compare(a.Character, b.Character,
                                                StringComparison.OrdinalIgnoreCase));
            return found;
        }

        /// <summary>
        /// The file's real size, read through an open handle.
        ///
        /// `FileInfo.Length` and `Directory.GetFiles` metadata come from the
        /// DIRECTORY ENTRY, and NTFS does not keep that current for a file
        /// something is holding open and appending to — the entry can sit
        /// unchanged for an hour while the game writes megabytes. Every raid
        /// log is exactly that file, so a scan that trusted the cheap number
        /// would decide the busiest client in the house was idle.
        ///
        /// The handle is opened the same way the tail opens one:
        /// ReadWrite|Delete, because the game holds it and anything stricter
        /// cannot open it at all.
        /// </summary>
        public static long TrueLength(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                    return fs.Length;
            }
            catch { return -1; }
        }

        /// <summary>Folders to look in: ACT's own, its parent (so sibling server
        /// folders come along), each configured root, and one level below each.</summary>
        private static List<string> Roots(IEnumerable<string> extraRoots)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string dir)
            {
                if (string.IsNullOrEmpty(dir) || roots.Count >= MaxDirectories) return;
                string full;
                try { full = Path.GetFullPath(dir); }
                catch { return; }
                if (!seen.Add(full)) return;
                try { if (!Directory.Exists(full)) return; }
                catch { return; }
                roots.Add(full);
            }

            var tops = new List<string>();
            var act = ActLogFolder();
            if (act != null)
            {
                tops.Add(act);
                try { tops.Add(Path.GetDirectoryName(act)); } catch { }
            }
            if (extraRoots != null) foreach (var root in extraRoots) tops.Add(root);

            foreach (var top in tops)
            {
                Add(top);
                // one level down, which is where the server folders live
                if (string.IsNullOrEmpty(top)) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(top); }
                catch { continue; }
                foreach (var sub in subs) Add(sub);
            }
            return roots;
        }
    }
}
