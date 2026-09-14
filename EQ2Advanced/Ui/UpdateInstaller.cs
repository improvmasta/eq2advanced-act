using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace EQ2Advanced.Ui
{
    /// <summary>Downloads and verifies a release, then starts the helper from
    /// LocalAppData. The helper waits for ACT to exit before touching either
    /// file in the plugin folder.</summary>
    internal static class UpdateInstaller
    {
        private const int MaxZipBytes = 8 * 1024 * 1024;
        private const int MaxDllBytes = 4 * 1024 * 1024;
        private static readonly string[] Files = { "EQ2Advanced.dll", "EQ2AdvancedUpdater.exe" };
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static string StatusFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EQ2Advanced", "UploaderUpdates", "status.json");

        internal static Task<string> StageAsync(string host, string route, string folder,
                                                string version, string track,
                                                string zipHash, string dllHash)
        {
            return Task.Run(() => Stage(host, route, folder, version, track, zipHash, dllHash));
        }

        private static string Stage(string host, string route, string folder,
                                    string version, string track, string zipHash, string dllHash)
        {
            if (!File.Exists(Path.Combine(folder, "EQ2AdvancedUpdater.exe")))
                throw new FileNotFoundException(
                    "The updater helper is missing. Extract both files from the Account ZIP beside EQ2Advanced.dll once.");
            byte[] zip;
            using (var web = new WebClient())
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                zip = web.DownloadData(host + route);
            }
            if (zip.Length == 0 || zip.Length > MaxZipBytes || Hash(zip) != zipHash)
                throw new InvalidDataException("The downloaded package did not match the published checksum.");
            var root = Path.GetDirectoryName(StatusFile);
            Directory.CreateDirectory(root);
            var stage = Path.Combine(root, track + "-" + version + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count != Files.Length ||
                        archive.Entries.Any(e => !Files.Contains(e.FullName, StringComparer.Ordinal)))
                        throw new InvalidDataException("The update package contains unexpected files.");
                    foreach (var name in Files)
                    {
                        var entry = archive.GetEntry(name);
                        if (entry == null || entry.Length == 0 || entry.Length > MaxDllBytes)
                            throw new InvalidDataException("The update package is incomplete.");
                        using (var source = entry.Open())
                        using (var output = File.Create(Path.Combine(stage, name)))
                            source.CopyTo(output);
                    }
                }
                if (Hash(File.ReadAllBytes(Path.Combine(stage, "EQ2Advanced.dll"))) != dllHash)
                    throw new InvalidDataException("The DLL did not match the published checksum.");
                File.WriteAllText(StatusFile, Json.Serialize(new { ok = (bool?)null, version, track }));
                var start = new ProcessStartInfo(Path.Combine(stage, "EQ2AdvancedUpdater.exe")) {
                    Arguments = Process.GetCurrentProcess().Id + " " + Quote(folder) + " "
                                + Quote(stage) + " " + Quote(StatusFile),
                    WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true,
                };
                var process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("Could not start the updater.");
                process.Dispose();
                return stage;
            }
            catch
            {
                try { File.Delete(StatusFile); } catch { }
                try { Directory.Delete(stage, true); } catch { }
                throw;
            }
        }

        internal static void RequestRestart(string stage)
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            File.WriteAllText(Path.Combine(stage, "restart.json"), Json.Serialize(new { exe, args }));
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
