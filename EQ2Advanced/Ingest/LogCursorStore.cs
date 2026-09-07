using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EQ2Advanced.Ingest
{
    /// <summary>
    /// One durable acknowledged offset per EQ2 logfile.
    ///
    /// Several ACT processes on one Windows account share the plugin config
    /// directory. A single Settings.LastLogPath/LastOffset pair therefore lets
    /// whichever boxed character writes last erase every other character's
    /// recovery point. Each path gets an independent small cursor file instead.
    ///
    /// Writes are guarded by a named cross-process mutex and keep the furthest
    /// acknowledged offset for the same physical file contents. The fingerprint
    /// of the file's beginning lets a rotated/replaced log safely start over.
    /// </summary>
    public sealed class LogCursorStore
    {
        private readonly string _directory;

        public LogCursorStore(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        /// <summary>The acknowledged offset for this exact logfile, if any.</summary>
        public long? Load(string logPath)
        {
            if (string.IsNullOrEmpty(logPath)) return null;
            var key = PathKey(logPath);
            using (var mutex = OpenMutex(key))
            {
                Take(mutex);
                try
                {
                    var path = CursorPath(key);
                    if (!File.Exists(path)) return null;
                    var record = ReadRecord(path);
                    if (record == null)
                        throw new InvalidDataException(
                            "The saved EQ2 logfile cursor is unreadable: " + path);
                    var fingerprint = Fingerprint(logPath);
                    if (fingerprint == null)
                        throw new IOException(
                            "Could not identify the EQ2 logfile before resuming it: "
                            + logPath);
                    // This path was known but its contents were replaced. Zero
                    // is the only lossless answer. Returning "no cursor" would
                    // make Uploader treat it as a first-time log and start at
                    // EOF, silently skipping the replacement.
                    if (!string.Equals(record.Fingerprint, fingerprint,
                                       StringComparison.Ordinal))
                        return 0;
                    return record.Offset;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>
        /// Persist only bytes the server acknowledged. Two processes following
        /// the same path cannot move the cursor backwards; replacing the file
        /// changes its fingerprint and begins a new cursor generation.
        /// </summary>
        public void Save(string logPath, long offset)
        {
            if (string.IsNullOrEmpty(logPath) || offset < 0) return;
            var fingerprint = Fingerprint(logPath);
            if (fingerprint == null)
                throw new IOException(
                    "Could not identify the EQ2 logfile before saving its cursor: "
                    + logPath);
            var key = PathKey(logPath);
            using (var mutex = OpenMutex(key))
            {
                Take(mutex);
                try
                {
                    Directory.CreateDirectory(_directory);
                    var path = CursorPath(key);
                    var current = ReadRecord(path);
                    if (current != null
                        && string.Equals(current.Fingerprint, fingerprint,
                                         StringComparison.Ordinal))
                        offset = Math.Max(offset, current.Offset);
                    AtomicWrite(path, fingerprint + "\n"
                                      + offset.ToString(CultureInfo.InvariantCulture) + "\n");
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private string CursorPath(string key) => Path.Combine(_directory, key + ".cursor");

        private static Mutex OpenMutex(string key)
            => new Mutex(false, "EQ2Advanced-LogCursor-" + key);

        private static void Take(Mutex mutex)
        {
            // Cursor lookup is a loss boundary: timing out and pretending there
            // is no cursor would start at EOF and skip unsent bytes. A named
            // mutex is released by the OS when its process dies, surfaced as
            // AbandonedMutexException below, so waiting has no stale-lock case.
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
        }

        private static string PathKey(string path)
        {
            string canonical;
            try { canonical = Path.GetFullPath(path).ToUpperInvariant(); }
            catch { canonical = path.ToUpperInvariant(); }
            return Hash(Encoding.UTF8.GetBytes(canonical));
        }

        private static string Fingerprint(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[4096];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) return "empty";
                    // A growing logfile must keep one identity. Hash its first
                    // complete line, not every byte currently inside this
                    // buffer; hashing a short file's whole contents changes on
                    // every append and would discard the very cursor meant to
                    // recover those appends.
                    var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                    var count = newline >= 0 ? newline + 1 : read;
                    if (count == buffer.Length) return Hash(buffer);
                    var exact = new byte[count];
                    Buffer.BlockCopy(buffer, 0, exact, 0, count);
                    return Hash(exact);
                }
            }
            catch { return null; }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                var text = new StringBuilder(digest.Length * 2);
                foreach (var b in digest) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        private sealed class Record
        {
            public string Fingerprint;
            public long Offset;
        }

        private static Record ReadRecord(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                long offset;
                if (lines.Length < 2 || string.IsNullOrEmpty(lines[0])
                    || !long.TryParse(lines[1], NumberStyles.None,
                                      CultureInfo.InvariantCulture, out offset)
                    || offset < 0)
                    return null;
                return new Record { Fingerprint = lines[0], Offset = offset };
            }
            catch { return null; }
        }

        private static void AtomicWrite(string path, string text)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }
    }
}
