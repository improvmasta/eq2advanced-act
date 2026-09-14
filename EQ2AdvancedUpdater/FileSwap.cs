using System;
using System.Collections.Generic;
using System.IO;

namespace EQ2AdvancedUpdater
{
    internal static class FileSwap
    {
        private static readonly string[] Files = { "EQ2Advanced.dll", "EQ2AdvancedUpdater.exe" };

        internal static void Apply(string folder, string stage)
        {
            var backup = Path.Combine(stage, "backup");
            Directory.CreateDirectory(backup);
            var moved = new List<string>();
            var installed = new List<string>();
            try
            {
                foreach (var name in Files)
                {
                    var source = Path.Combine(stage, name);
                    if (!File.Exists(source)) throw new FileNotFoundException("Staged file missing", source);
                    var target = Path.Combine(folder, name);
                    var original = Path.Combine(backup, name);
                    if (File.Exists(target)) { File.Move(target, original); moved.Add(name); }
                    File.Copy(source, target);
                    installed.Add(name);
                }
            }
            catch
            {
                foreach (var name in installed) File.Delete(Path.Combine(folder, name));
                for (var i = moved.Count - 1; i >= 0; i--)
                {
                    var name = moved[i];
                    var target = Path.Combine(folder, name);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(Path.Combine(backup, name), target);
                }
                throw;
            }
            try { Directory.Delete(backup, true); }
            catch (IOException) { /* installed files are valid; keep the backup */ }
        }
    }
}
