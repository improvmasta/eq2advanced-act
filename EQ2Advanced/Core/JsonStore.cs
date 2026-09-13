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

        /// <summary>The plugin's settings. One file, whichever build wrote it:
        /// the test track replaces the stable DLL rather than sitting beside
        /// it, so it inherits the pairing instead of asking for it again.</summary>
        public static string ConfigPath
            => Path.Combine(ConfigDirectory, Channel.ConfigFileName);

        /// <summary>Acknowledged offsets and tail leases. Swapping one build for
        /// the other therefore resumes every log exactly where the server had
        /// got to, rather than starting them over at EOF.</summary>
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
