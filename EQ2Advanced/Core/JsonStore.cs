using System;
using System.IO;
using System.Web.Script.Serialization;
using Advanced_Combat_Tracker;

namespace EQ2Advanced.Core
{
    /// <summary>
    /// Loads/saves a single JSON settings blob under ACT's config folder using the
    /// framework's built-in JavaScriptSerializer (no external dependency).
    /// </summary>
    internal static class JsonStore
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        /// <summary>ACT's own config folder — where every channel's settings,
        /// cursors and leases live.</summary>
        public static string ConfigDirectory
        {
            get
            {
                string dir;
                try { dir = ActGlobals.oFormActMain.AppDataFolder.FullName; }
                catch { dir = AppDomain.CurrentDomain.BaseDirectory; }
                return Path.Combine(dir, "Config");
            }
        }

        /// <summary>This BUILD's settings. Per channel, so a test build cannot
        /// write its half-finished state into the config the stable uploader
        /// reads on its next start.</summary>
        public static string ConfigPath
            => Path.Combine(ConfigDirectory, Channel.ConfigFileName);

        /// <summary>The stable build's settings, read once to seed a fresh test
        /// track config. See <see cref="Channel.StableConfigFileName"/>.</summary>
        public static string StableConfigPath
            => Path.Combine(ConfigDirectory, Channel.StableConfigFileName);

        /// <summary>
        /// Acknowledged offsets and tail leases, SHARED BY EVERY CHANNEL and
        /// deliberately not derived from <see cref="ConfigPath"/>.
        ///
        /// A per-channel cursor directory would mean the stable build and the
        /// test build each keep their own idea of how far the server has read a
        /// given log — so enabling both would have them tail the same file from
        /// two different offsets, cutting the same log second into batches two
        /// different ways. The server's line dedupe keys on a line's ordinal
        /// within its batch, so that is not a duplicate-and-drop, it is a real
        /// repeated hit silently swallowed. One directory, one cursor per file,
        /// one lease per file (`LogLease`) — and the two builds coexist.
        /// </summary>
        public static string CursorDirectory
            => Path.Combine(ConfigDirectory, "EQ2Advanced.Cursors");

        public static bool Exists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        public static T Load<T>() where T : new() => LoadFrom<T>(ConfigPath);

        public static T LoadFrom<T>(string path) where T : new()
        {
            try
            {
                if (File.Exists(path))
                {
                    var obj = Ser.Deserialize<T>(File.ReadAllText(path));
                    if (obj != null) return obj;
                }
            }
            catch { /* fall through to defaults */ }
            return new T();
        }

        public static void Save<T>(T obj)
        {
            try
            {
                var path = ConfigPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Ser.Serialize(obj));
            }
            catch { /* best-effort persistence */ }
        }

        public static string Serialize(object obj) => Ser.Serialize(obj);

        public static object Deserialize(string json)
        {
            try { return Ser.DeserializeObject(json); }
            catch { return null; }
        }
    }
}
