using System.IO;
using System.Text.RegularExpressions;

namespace EQ2Advanced.Ingest
{
    /// <summary>
    /// Facts read off an EverQuest II log file's NAME.
    ///
    /// EQ2 writes one log per character, `eq2log_&lt;Character&gt;.txt`, and that is
    /// where the plugin learns whose raid it is sending. Nobody is asked: the
    /// device token belongs to the account, so switching to an alt just means
    /// ACT starts reading a different file and the next batch carries a
    /// different name. The server creates a character the first time it sees
    /// one, which is why an alt's first raid needs no setup at all.
    /// </summary>
    internal static class EqLog
    {
        private static readonly Regex NameRe =
            new Regex(@"^eq2log_(?<name>[A-Za-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The character a log belongs to, or null if the file isn't
        /// named the way EQ2 names them (a renamed or hand-made file).</summary>
        public static string CharacterFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string file;
            try { file = Path.GetFileName(path); }
            catch { return null; }
            var m = NameRe.Match(file ?? "");
            if (!m.Success) return null;
            var name = m.Groups["name"].Value;
            // The server stores names capitalised; send what it will store so a
            // log named eq2log_bobby.txt doesn't create a second "bobby".
            return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
        }
    }
}
