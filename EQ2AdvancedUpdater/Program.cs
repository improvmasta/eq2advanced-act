using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace EQ2AdvancedUpdater
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static int Main(string[] args)
        {
            if (args.Length != 4 || !int.TryParse(args[0], out var actPid)) return 2;
            var folder = Path.GetFullPath(args[1]);
            var stage = Path.GetFullPath(args[2]);
            var status = Path.GetFullPath(args[3]);
            try
            {
                // ACT owns the loaded DLL. Nothing is touched until its process exits.
                try { Process.GetProcessById(actPid).WaitForExit(); }
                catch (ArgumentException) { /* ACT already exited. */ }
                FileSwap.Apply(folder, stage);
                WriteStatus(status, true, null);
                RestartIfRequested(stage, status);
                return 0;
            }
            catch (Exception ex)
            {
                try { WriteStatus(status, false, ex.Message); } catch { }
                return 1;
            }
        }

        private static void WriteStatus(string path, bool ok, string error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(new { ok, error }));
        }

        private static void RestartIfRequested(string stage, string status)
        {
            var path = Path.Combine(stage, "restart.json");
            if (!File.Exists(path)) return;
            try
            {
                var request = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                var exe = request["exe"] as string;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    throw new FileNotFoundException("ACT executable not found", exe);
                var start = new ProcessStartInfo(exe) {
                    WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false,
                };
                if (request.TryGetValue("args", out var raw) && raw is object[] values)
                    foreach (var value in values)
                        start.Arguments += " " + Quote(Convert.ToString(value));
                Process.Start(start);
            }
            catch (Exception ex)
            {
                WriteStatus(status, true, "Installed, but ACT did not reopen: " + ex.Message);
            }
        }

        private static string Quote(string value)
        {
            var output = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') {
                    output.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                output.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
