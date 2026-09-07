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

        public static string ConfigPath
        {
            get
            {
                string dir;
                try { dir = ActGlobals.oFormActMain.AppDataFolder.FullName; }
                catch { dir = AppDomain.CurrentDomain.BaseDirectory; }
                return Path.Combine(dir, "Config", "EQ2Advanced.json");
            }
        }

        public static T Load<T>() where T : new()
        {
            try
            {
                var path = ConfigPath;
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
