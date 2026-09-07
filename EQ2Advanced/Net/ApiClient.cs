using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using EQ2Advanced.Core;

namespace EQ2Advanced.Net
{
    public class BatchResult
    {
        public int Accepted;
        public int Duplicates;
        public int? SessionId;
        public bool Replayed;
    }

    /// <summary>Server said no in a way that retrying will not fix.</summary>
    public class ApiException : Exception
    {
        public int StatusCode;
        public ApiException(int status, string message) : base(message) { StatusCode = status; }
    }

    /// <summary>
    /// The eq2advanced ingest contract, in C#. This is a transliteration of the
    /// server's reference client (backend/tools/simulate_live.py) and the two
    /// must stay in step:
    ///
    ///   GET  /api/ingest/hello          which account, what is receiving
    ///   POST /api/ingest/batch          gzip JSON {batch_id, mode, character, lines}
    ///   POST /api/ingest/backfill/done  close that character's session
    ///
    /// The token belongs to an ACCOUNT, and every batch names the character it
    /// came from — read off the log file, so alts need no setup and one pairing
    /// covers them all.
    ///
    /// A device token SENDS LOGS and does nothing else — it cannot read a parse
    /// back and it cannot change who sees one. Sharing is decided on the site.
    ///
    /// Auth is always `Authorization: Bearer &lt;device token&gt;`. Everything is
    /// synchronous and called from the uploader's own thread; nothing here
    /// touches the UI.
    /// </summary>
    public class ApiClient
    {
        private readonly Settings _settings;

        public ApiClient(Settings settings) { _settings = settings; }

        static ApiClient()
        {
            // ACT runs on .NET Framework 4.8, whose default ServicePointManager
            // protocol list can still exclude TLS 1.2 depending on the machine's
            // registry. eq2advanced is TLS-only, so without this the very first
            // request fails with an opaque "connection was closed" on exactly
            // the older Windows boxes most likely to be running EQ2.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { /* older runtime without the enum value; nothing to do */ }
        }

        private HttpWebRequest NewRequest(string path, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create(_settings.Host + path);
            req.Method = method;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "eq2advanced-act/" + EQ2AdvancedPlugin.Version;
            req.Headers["Authorization"] = "Bearer " + _settings.Token;
            req.KeepAlive = true;
            return req;
        }

        private static string ReadAll(WebResponse resp)
        {
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        /// <summary>
        /// One request, with the retry policy the contract calls for: 429 means
        /// another batch is in flight for this token + character stream, so
        /// wait out Retry-After and try the SAME batch again (it is idempotent
        /// per batch_id, so a resend costs nothing and replays the stored answer). Transport
        /// errors back off; a 4xx that is not 429 is final and is surfaced.
        /// </summary>
        private string Send(string path, string method, object payload, CancellationToken cancel)
        {
            byte[] body = null;
            if (payload != null)
            {
                var json = Encoding.UTF8.GetBytes(JsonStore.Serialize(payload));
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                        gz.Write(json, 0, json.Length);
                    body = ms.ToArray();
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                var req = NewRequest(path, method);
                if (body != null)
                {
                    req.ContentType = "application/json";
                    req.Headers["Content-Encoding"] = "gzip";
                    req.ContentLength = body.Length;
                    using (var stream = req.GetRequestStream())
                        stream.Write(body, 0, body.Length);
                }
                try
                {
                    using (var resp = req.GetResponse())
                        return ReadAll(resp);
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    if (resp == null)
                    {
                        // no answer at all: the raid PC's network, the box, or
                        // the proxy. Keep the batch and try again shortly.
                        if (attempt >= 4) throw;
                        Wait(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancel);
                        continue;
                    }
                    var status = (int)resp.StatusCode;
                    var text = ReadAll(resp);
                    if (status == 429)
                    {
                        var after = 2.0;
                        double parsed;
                        if (double.TryParse(resp.Headers["Retry-After"], out parsed)) after = parsed;
                        Wait(TimeSpan.FromSeconds(after), cancel);
                        continue;
                    }
                    if (status >= 500 && attempt < 4)
                    {
                        Wait(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancel);
                        continue;
                    }
                    throw new ApiException(status, Describe(status, text));
                }
            }
        }

        private static void Wait(TimeSpan span, CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(span)) throw new OperationCanceledException();
        }

        /// <summary>
        /// Turn a server error into something a raider can act on. The status
        /// codes that matter here all mean a specific, fixable thing.
        /// </summary>
        private static string Describe(int status, string text)
        {
            var detail = Detail(text);
            switch (status)
            {
                case 401:
                    return "This device is not paired (the token was revoked, or the site moved). "
                         + "Mint a new token on eq2advanced.com and pair again.";
                case 404:
                    return detail ?? "The server did not recognise something in that request.";
                case 413:
                    return "That batch was too large for the server.";
                default:
                    return detail ?? ("HTTP " + status);
            }
        }

        private static string Detail(string text)
        {
            var obj = JsonStore.Deserialize(text) as Dictionary<string, object>;
            object detail;
            if (obj != null && obj.TryGetValue("detail", out detail) && detail != null)
                return detail.ToString();
            return null;
        }

        // ---- typed calls ----

        private static Dictionary<string, object> Obj(object value)
            => value as Dictionary<string, object>;

        /// <summary>Pairing check: returns the account this token uploads to, and
        /// whether anything is currently receiving.</summary>
        public string Hello(out int? openSession, CancellationToken cancel)
        {
            var root = Obj(JsonStore.Deserialize(Send("/api/ingest/hello", "GET", null, cancel)));
            openSession = null;
            object v;
            if (root != null && root.TryGetValue("session", out v) && v != null)
                openSession = Convert.ToInt32(v);
            if (root != null && root.TryGetValue("account", out v) && v != null)
                return Convert.ToString(v);
            return null;
        }

        /// <summary>Send one batch of verbatim log lines for `character`.
        /// `zone` seeds a live tail which began after EQ2's login zone line;
        /// later zone changes remain verbatim lines.</summary>
        public BatchResult SendBatch(string batchId, string mode, string character,
                                     IList<string> lines, string zone,
                                     CancellationToken cancel)
        {
            var payload = new Dictionary<string, object>
            {
                { "batch_id", batchId },
                { "mode", mode },
                { "lines", lines },
            };
            if (!string.IsNullOrEmpty(character)) payload["character"] = character;
            if (!string.IsNullOrEmpty(zone)) payload["zone"] = zone;
            var root = Obj(JsonStore.Deserialize(Send("/api/ingest/batch", "POST", payload, cancel)));
            var result = new BatchResult();
            if (root == null) return result;
            object v;
            if (root.TryGetValue("accepted", out v)) result.Accepted = Convert.ToInt32(v);
            if (root.TryGetValue("duplicates", out v)) result.Duplicates = Convert.ToInt32(v);
            if (root.TryGetValue("session_id", out v) && v != null) result.SessionId = Convert.ToInt32(v);
            if (root.TryGetValue("replayed", out v)) result.Replayed = Convert.ToBoolean(v);
            return result;
        }

        /// <summary>Close the open session. The server then rebuilds it from raw
        /// through the bulk parser, which is what makes a streamed night
        /// identical to an uploaded file.</summary>
        public int? Done(string character, CancellationToken cancel)
        {
            var body = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(character)) body["character"] = character;
            var root = Obj(JsonStore.Deserialize(Send("/api/ingest/backfill/done", "POST",
                                                      body, cancel)));
            object v;
            if (root != null && root.TryGetValue("session_id", out v) && v != null)
                return Convert.ToInt32(v);
            return null;
        }

        /// <summary>The raid's page on the site, for the "open in browser" link.</summary>
        public string SessionUrl(int sessionId) => _settings.Host + "/sessions/" + sessionId;
    }
}
