using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using EQ2Advanced.Core;
using EQ2Advanced.Ingest;
using EQ2Advanced.Net;

namespace EQ2Advanced.Ui
{
    /// <summary>
    /// The "eq2advanced" tab inside ACT. Three things: pair this PC, send the
    /// log, and push up a log from a night the plugin missed.
    ///
    /// There is deliberately no sharing control here. Who can see a raid is
    /// decided on eq2advanced.com — a standing auto-share per character, or a
    /// tick on the raid itself — and this plugin only sends log lines. The tab
    /// links to the site rather than duplicating that decision in two places.
    /// </summary>
    public class ConfigTab : UserControl
    {
        private readonly Settings _settings;
        private readonly ApiClient _api;
        private readonly Uploader _uploader;

        private TextBox _pairBox;
        private Label _pairedAs;
        private Label _tokenWarning;
        private Button _pairButton;
        private CheckBox _live;
        private CheckBox _shareChat;
        private CheckBox _sharePublicChat;
        private Label _status;
        private LinkLabel _openSite;
        private Button _backfill;
        private Button _cancelImport;
        private ProgressBar _backfillProgress;
        private Label _importStatus;
        private CancellationTokenSource _importCancel;

        private int? _sessionId;
        private string _account;

        public ConfigTab(Settings settings, ApiClient api, Uploader uploader)
        {
            _settings = settings;
            _api = api;
            _uploader = uploader;
            Build();
            _uploader.StatusChanged += OnUploaderStatus;
            RefreshPairingAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _uploader.StatusChanged -= OnUploaderStatus;
            base.Dispose(disposing);
        }

        // ---- layout ----

        private static Label Note(string text) => new Label
        {
            Text = text,
            AutoSize = false,
            Height = 32,
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 2, 0, 6),
        };

        private void Build()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(10);

            // A plain panel, not a TableLayoutPanel: everything in here is a
            // fixed-height band stacked top to bottom, and Dock=Top does that
            // without rows to keep in step. WinForms docks the LAST-added child
            // first, so each group below is added in reverse visual order.
            var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

            // --- 1. pairing ---
            var pairing = new GroupBox { Text = "This device", Dock = DockStyle.Top, Height = 160 };
            _tokenWarning = new Label
            {
                Dock = DockStyle.Top, Height = 32, ForeColor = Color.Firebrick, Visible = false,
                Text = "Windows could not encrypt your token on this PC, so it is saved in "
                     + "plain text (see the config file under ACT's Config folder). Re-pair "
                     + "after fixing whatever blocked it to re-encrypt.",
            };
            _pairedAs = new Label { Dock = DockStyle.Top, Height = 20, Text = "Not paired." };
            var pairRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            _pairBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            _pairButton = new Button { Text = "Pair", Dock = DockStyle.Right, Width = 90 };
            _pairButton.Click += OnPair;
            pairRow.Controls.Add(_pairBox);
            pairRow.Controls.Add(_pairButton);
            pairing.Controls.Add(pairRow);
            pairing.Controls.Add(Note(
                "Paste the pairing code (or the eq2advanced:// link) from the "
                + "Import page on eq2advanced.com. One code covers every character "
                + "you play — the plugin reads the name off the log. It only lets "
                + "this PC send logs."));
            pairing.Controls.Add(_pairedAs);
            pairing.Controls.Add(_tokenWarning);

            // --- 2. uploading ---
            var uploading = new GroupBox { Text = "Uploading", Dock = DockStyle.Top, Height = 254 };
            _live = new CheckBox
            {
                Text = "Send my combat log to eq2advanced as I play",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.LiveUpload,
            };
            _live.CheckedChanged += OnLiveToggled;
            _shareChat = new CheckBox
            {
                Text = "Also send my group and raid chat during fights (optional) "
                     + "— example: loot tracking",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.ShareChat,
            };
            _shareChat.CheckedChanged += OnShareChatToggled;
            _sharePublicChat = new CheckBox
            {
                Text = "Contribute my General/LFG/Auction chat to the site's public chat page (optional)",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.SharePublicChat,
            };
            _sharePublicChat.CheckedChanged += OnSharePublicChatToggled;
            _status = new Label { Dock = DockStyle.Top, Height = 34, Text = "Idle." };
            _openSite = new LinkLabel
            {
                Dock = DockStyle.Top, Height = 20, Text = "Open this raid on eq2advanced.com",
                Visible = false,
            };
            _openSite.LinkClicked += (s, e) => Open(
                _sessionId.HasValue ? _api.SessionUrl(_sessionId.Value) : _settings.Host);
            var manageShares = new LinkLabel
            {
                Dock = DockStyle.Top, Height = 20,
                Text = "Choose who can see your raids on eq2advanced.com",
            };
            manageShares.LinkClicked += (s, e) => Open(_settings.Host + "/import");
            var privacyLink = new LinkLabel
            {
                Dock = DockStyle.Top, Height = 20, Text = "What gets sent, in full — eq2advanced.com/privacy",
            };
            privacyLink.LinkClicked += (s, e) => Open(_settings.Host + "/privacy");
            uploading.Controls.Add(manageShares);
            uploading.Controls.Add(privacyLink);
            uploading.Controls.Add(_openSite);
            uploading.Controls.Add(_status);
            uploading.Controls.Add(Note(
                "Fights appear on the site as each one ends — a raid in progress fills in "
                + "fight by fight. Stop uploading (or close ACT) to finish the night."));
            uploading.Controls.Add(Note(
                "Always sent: combat log, NPC dialogue. Never sent: tells, guild chat, "
                + "officer chat, /say. The two checkboxes below are the only optional part."));
            uploading.Controls.Add(_sharePublicChat);
            uploading.Controls.Add(_shareChat);
            uploading.Controls.Add(_live);

            // --- 3. import old logs ---
            var backfill = new GroupBox { Text = "Import logs you already have",
                                          Dock = DockStyle.Top, Height = 168 };
            _importStatus = new Label { Dock = DockStyle.Bottom, Height = 32, Text = "" };
            _backfillProgress = new ProgressBar { Dock = DockStyle.Bottom, Height = 16, Visible = false };
            var importRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            _backfill = new Button { Text = "Choose log files...", Dock = DockStyle.Left, Width = 150 };
            _backfill.Click += OnBackfill;
            _cancelImport = new Button { Text = "Stop", Dock = DockStyle.Left, Width = 70,
                                         Enabled = false };
            _cancelImport.Click += (s, e) =>
            {
                if (_importCancel != null) _importCancel.Cancel();
                _cancelImport.Enabled = false;
                Say("Stopping after the current batch...");
            };
            importRow.Controls.Add(_cancelImport);
            importRow.Controls.Add(_backfill);
            backfill.Controls.Add(importRow);
            backfill.Controls.Add(Note(
                "Pick as many as you like — a whole folder of old logs is fine. They go up one "
                + "at a time, paced so a big import doesn't swamp the server, and each log "
                + "becomes its own night. Re-sending a log you already uploaded is safe: the "
                + "server keeps only the lines it hasn't seen. Stop and resume any time."));
            backfill.Controls.Add(_backfillProgress);
            backfill.Controls.Add(_importStatus);

            root.Controls.Add(backfill);
            root.Controls.Add(uploading);
            root.Controls.Add(pairing);
            Controls.Add(root);

            ApplyPairedState();
        }

        private static void Open(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch { /* no browser configured; nothing useful to do */ }
        }

        // ---- pairing ----

        private void OnPair(object sender, EventArgs e)
        {
            string error;
            if (!_settings.ApplyPairPayload(_pairBox.Text, out error))
            {
                Say(error, true);
                return;
            }
            _pairBox.Clear();
            Say("Pairing...");
            RefreshPairingAsync();
        }

        /// <summary>Ask the server who this token uploads as, so the tab can say
        /// which character it is about to send a raid for.</summary>
        private void RefreshPairingAsync()
        {
            if (!_settings.Paired) { ApplyPairedState(); return; }
            var thread = new Thread(() =>
            {
                try
                {
                    int? session;
                    var name = _api.Hello(out session, CancellationToken.None);
                    Post(() =>
                    {
                        _account = name;
                        if (session.HasValue) _sessionId = session;
                        _settings.Save();
                        _openSite.Visible = _sessionId.HasValue;
                        Say("Paired.");
                        ApplyPairedState();
                    });
                }
                catch (Exception ex) { Post(() => { Say(ex.Message, true); ApplyPairedState(); }); }
            }) { IsBackground = true, Name = "eq2advanced-hello" };
            thread.Start();
        }

        private void ApplyPairedState()
        {
            var paired = _settings.Paired;
            _live.Enabled = paired;
            _shareChat.Enabled = paired;
            _sharePublicChat.Enabled = paired;
            _backfill.Enabled = paired;
            // The ACCOUNT is what a token belongs to; the character comes off
            // whatever log ACT is reading, so it is shown as live status rather
            // than as a property of the pairing.
            _pairedAs.Text = paired
                ? "Paired to " + _settings.Host
                  + (string.IsNullOrEmpty(_account) ? "" : " as " + _account)
                : "Not paired.";
            _pairButton.Text = paired ? "Re-pair" : "Pair";
            _tokenWarning.Visible = paired && !_settings.TokenProtectedAtRest;
        }

        // ---- live upload ----

        private void OnLiveToggled(object sender, EventArgs e)
        {
            _settings.LiveUpload = _live.Checked;
            _settings.Save();
            if (_live.Checked) _uploader.Start();
            else _uploader.Stop(closeSession: true);
        }

        private void OnShareChatToggled(object sender, EventArgs e)
        {
            _settings.ShareChat = _shareChat.Checked;
            _settings.Save();
        }

        private void OnSharePublicChatToggled(object sender, EventArgs e)
        {
            _settings.SharePublicChat = _sharePublicChat.Checked;
            _settings.Save();
        }

        private void OnUploaderStatus(UploaderStatus status)
        {
            // raised on the worker thread
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => ShowStatus(status))); }
            catch (InvalidOperationException) { /* handle went away mid-post */ }
        }

        private void ShowStatus(UploaderStatus status)
        {
            var text = status.Message;
            if (status.LinesSent > 0)
                text += "  (" + status.LinesSent + " lines sent this session"
                      + (status.Duplicates > 0 ? ", " + status.Duplicates + " already known" : "")
                      + ")";
            _status.Text = text;
            _status.ForeColor = status.IsError ? Color.Firebrick : SystemColors.ControlText;
            _sessionId = status.SessionId ?? _sessionId;
            _openSite.Visible = _sessionId.HasValue;
            if (_live.Checked && !status.Running)
            {
                // the worker gave up (revoked token, say) — put the switch back
                _live.CheckedChanged -= OnLiveToggled;
                _live.Checked = false;
                _settings.LiveUpload = false;
                _settings.Save();
                _live.CheckedChanged += OnLiveToggled;
            }
        }

        private void Say(string message, bool isError = false)
        {
            _status.Text = message;
            _status.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        // ---- backfill ----

        private void OnBackfill(object sender, EventArgs e)
        {
            string[] paths;
            using (var dialog = new OpenFileDialog
            {
                Title = "Choose EverQuest II combat logs to import",
                Filter = "Combat logs (*.txt)|*.txt|All files (*.*)|*.*",
                Multiselect = true,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                paths = dialog.FileNames;
            }
            if (paths.Length == 0) return;

            // Oldest first, so the raids appear on the site in the order they
            // happened rather than the order the file dialog handed them over.
            Array.Sort(paths, (a, b) =>
            {
                try { return System.IO.File.GetLastWriteTimeUtc(a)
                                 .CompareTo(System.IO.File.GetLastWriteTimeUtc(b)); }
                catch { return string.CompareOrdinal(a, b); }
            });

            _importCancel = new CancellationTokenSource();
            _backfill.Enabled = false;
            _cancelImport.Enabled = true;
            _backfillProgress.Visible = true;
            _backfillProgress.Value = 0;
            _importStatus.Text = "Starting import of " + paths.Length
                               + " log" + (paths.Length == 1 ? "" : "s") + "...";

            var cancel = _importCancel.Token;
            var thread = new Thread(() =>
            {
                var failures = 0;
                try
                {
                    _uploader.BackfillMany(paths, p =>
                    {
                        if (p.Note != null && p.Note.StartsWith("failed")) failures++;
                        Post(() => ShowImport(p));
                    }, cancel);
                    Post(() => _importStatus.Text = failures == 0
                        ? "Import finished — " + paths.Length + " log"
                          + (paths.Length == 1 ? "" : "s") + " sent. They're on the site now."
                        : "Import finished, but " + failures + " file"
                          + (failures == 1 ? "" : "s") + " failed (see above).");
                }
                catch (OperationCanceledException)
                {
                    Post(() => _importStatus.Text =
                        "Import stopped. Nothing was lost — run it again and the server "
                        + "skips what it already has.");
                }
                catch (Exception ex) { Post(() => Say(ex.Message, true)); }
                finally
                {
                    Post(() =>
                    {
                        _backfill.Enabled = true;
                        _cancelImport.Enabled = false;
                        _backfillProgress.Visible = false;
                        _openSite.Visible = _sessionId.HasValue;
                    });
                }
            }) { IsBackground = true, Name = "eq2advanced-import" };
            thread.Start();
        }

        private void ShowImport(Uploader.ImportProgress p)
        {
            if (p.SessionId.HasValue) _sessionId = p.SessionId;

            // Progress across the WHOLE import, not just the current file:
            // "73%" that restarts at every file is worse than no bar at all.
            var perFile = p.BytesTotal > 0
                ? Math.Min(1.0, (double)p.BytesSent / p.BytesTotal) : 1.0;
            var overall = p.FileCount > 0
                ? ((p.FileIndex - 1) + perFile) / p.FileCount : 0.0;
            _backfillProgress.Value = Math.Max(0, Math.Min(100, (int)(overall * 100)));

            var text = "Log " + p.FileIndex + " of " + p.FileCount + ": " + p.FileName;
            if (!string.IsNullOrEmpty(p.Character)) text += "  (" + p.Character + ")";
            if (p.Note != null && p.Note != "done") text += "  — " + p.Note;
            text += "\r\n" + p.LinesSent.ToString("N0") + " lines sent";
            if (p.Duplicates > 0) text += ", " + p.Duplicates.ToString("N0") + " already known";
            _importStatus.Text = text;
        }
    }
}
