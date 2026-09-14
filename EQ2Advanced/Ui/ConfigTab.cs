using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
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
        private CheckBox _autoUpdate;
        private Button _checkUpdates;
        private Label _updateStatus;
        private System.Windows.Forms.Timer _updateCheckTimer;

        // --- multi-log (test track only; see Core/Channel.cs) ---
        private CheckBox _multiLog;
        private CheckBox _onlyWhenFighting;
        private ListView _logs;
        private System.Windows.Forms.Timer _logRefresh;
        /// <summary>Set while the list is being rebuilt, so redrawing a row does
        /// not read as the user ticking it.</summary>
        private bool _rebuilding;

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
            _updateCheckTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _updateCheckTimer.Tick += (s, e) => {
                _updateCheckTimer.Stop();
                CheckForUpdate(false);
            };
            if (_settings.AutoUpdateCheck) _updateCheckTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _uploader.StatusChanged -= OnUploaderStatus;
                if (_logRefresh != null) { _logRefresh.Stop(); _logRefresh.Dispose(); }
                _updateCheckTimer?.Dispose();
            }
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

        private static Control Gap() => new Panel { Dock = DockStyle.Top, Height = 10 };

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
            var pairing = new GroupBox {
                Text = "Connect your account", Dock = DockStyle.Top, Height = 150,
                Padding = new Padding(12, 8, 12, 10),
            };
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
                "Get the combat log key from Account → Keys. One key covers every character on this PC."));
            pairing.Controls.Add(_pairedAs);
            pairing.Controls.Add(_tokenWarning);

            // --- 2. uploading ---
            var uploading = new GroupBox {
                Text = "Live combat upload", Dock = DockStyle.Top, Height = 166,
                Padding = new Padding(12, 8, 12, 10),
            };
            _live = new CheckBox
            {
                Text = "Upload combat logs while ACT is running",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.LiveUpload,
            };
            _live.CheckedChanged += OnLiveToggled;
            _shareChat = new CheckBox
            {
                Text = "Include group and raid chat during fights",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.ShareChat,
            };
            _shareChat.CheckedChanged += OnShareChatToggled;
            _sharePublicChat = new CheckBox
            {
                Text = "Share General, LFG, and Auction chat publicly",
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
                Dock = DockStyle.Top, Height = 20, Text = "Manage raid sharing",
            };
            manageShares.LinkClicked += (s, e) => Open(_settings.Host + "/groups");
            var privacyLink = new LinkLabel
            {
                Dock = DockStyle.Top, Height = 20, Text = "What gets sent, in full — eq2advanced.com/privacy",
            };
            privacyLink.LinkClicked += (s, e) => Open(_settings.Host + "/privacy");
            uploading.Controls.Add(manageShares);
            uploading.Controls.Add(_openSite);
            uploading.Controls.Add(_status);
            uploading.Controls.Add(Note(
                "Fights appear as they end. Stop uploading or close ACT to finish the session."));
            uploading.Controls.Add(_live);

            var chat = new GroupBox {
                Text = "Chat options", Dock = DockStyle.Top, Height = 155,
                Padding = new Padding(12, 8, 12, 10),
            };
            chat.Controls.Add(privacyLink);
            chat.Controls.Add(Note(
                "Tells, guild chat, officer chat, and /say are never uploaded."));
            chat.Controls.Add(_sharePublicChat);
            chat.Controls.Add(_shareChat);

            // --- 3. import old logs ---
            var backfill = new GroupBox {
                Text = "Upload older logs", Dock = DockStyle.Top, Height = 145,
                Padding = new Padding(12, 8, 12, 10),
            };
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
                "Select one or more old EQ2 logs. Already uploaded lines are skipped."));
            backfill.Controls.Add(_backfillProgress);
            backfill.Controls.Add(_importStatus);

            var updates = new GroupBox {
                Text = "Updates", Dock = DockStyle.Top, Height = 110,
                Padding = new Padding(12, 8, 12, 10),
            };
            _autoUpdate = new CheckBox {
                Text = "Check for updates when ACT starts", Dock = DockStyle.Top,
                Height = 24, Checked = _settings.AutoUpdateCheck,
            };
            _autoUpdate.CheckedChanged += (s, e) => {
                _settings.AutoUpdateCheck = _autoUpdate.Checked;
                _settings.Save();
                if (_autoUpdate.Checked) _updateCheckTimer?.Start();
                else _updateCheckTimer?.Stop();
            };
            _checkUpdates = new Button { Text = "Check now", Dock = DockStyle.Top,
                                          Height = 28, Width = 110 };
            _checkUpdates.Click += (s, e) => CheckForUpdate(true);
            _updateStatus = new Label { Dock = DockStyle.Top, Height = 22,
                                         ForeColor = SystemColors.GrayText };
            updates.Controls.Add(_updateStatus);
            updates.Controls.Add(_checkUpdates);
            updates.Controls.Add(_autoUpdate);

            root.Controls.Add(updates);
            root.Controls.Add(Gap());
            root.Controls.Add(backfill);
            root.Controls.Add(Gap());
            // Compiled into BOTH builds and only shown in one. `Channel.MultiLog`
            // is a constant, so the stable build warns that the call is
            // unreachable — which is the point: the panel still type-checks
            // against every change made to the stable half of this file.
#pragma warning disable 162
            if (Channel.MultiLog) { root.Controls.Add(BuildMultiLog()); root.Controls.Add(Gap()); }
#pragma warning restore 162
            root.Controls.Add(chat);
            root.Controls.Add(Gap());
            root.Controls.Add(uploading);
            root.Controls.Add(Gap());
            root.Controls.Add(pairing);
            Controls.Add(root);

            ApplyPairedState();
        }

        private async void CheckForUpdate(bool manual)
        {
            if (!_checkUpdates.Enabled) return;
            _checkUpdates.Enabled = false;
            if (manual) _updateStatus.Text = "Checking…";
            try
            {
                var route = Channel.MultiLog ? "/api/plugin/multi" : "/api/plugin";
                var raw = await Task.Run(() => {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var web = new WebClient())
                        return web.DownloadString(_settings.Host + route);
                });
                if (IsDisposed) return;
                var release = new JavaScriptSerializer()
                    .Deserialize<System.Collections.Generic.Dictionary<string, object>>(raw);
                if (release == null || !release.ContainsKey("available")
                    || !true.Equals(release["available"]))
                    throw new InvalidOperationException("No release is available.");
                var version = release.ContainsKey("version") ? release["version"] as string : null;
                Version current, next;
                if (!Version.TryParse(Channel.Version, out current)
                    || !Version.TryParse(version, out next))
                    throw new InvalidOperationException("Release version is invalid.");
                if (next <= current) {
                    if (manual) _updateStatus.Text = "Already up to date.";
                    return;
                }
                var releaseKey = (Channel.MultiLog ? "multi:" : "stable:") + version;
                if (_settings.SkippedUpdate == releaseKey) {
                    if (manual) _updateStatus.Text = "This release was skipped.";
                    return;
                }
                var notes = release.ContainsKey("notes") ? release["notes"] as string : null;
                var result = ShowUpdatePrompt(version, notes ?? "");
                if (result == DialogResult.Yes) Open(_settings.Host + "/account#account-downloads");
                if (result == DialogResult.No) {
                    _settings.SkippedUpdate = releaseKey;
                    _settings.Save();
                    _updateStatus.Text = "Release " + version + " skipped.";
                }
            }
            catch (Exception ex) {
                if (manual && !IsDisposed) _updateStatus.Text = "Update check failed: " + ex.Message;
            }
            finally { if (!IsDisposed) _checkUpdates.Enabled = true; }
        }

        private DialogResult ShowUpdatePrompt(string version, string notes)
        {
            using (var popup = new Form {
                Text = "EQ2Advanced update", Width = 520, Height = 320,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false,
            }) {
                var heading = new Label {
                    Text = "EQ2Advanced " + version + " available", Dock = DockStyle.Top,
                    Height = 35, Padding = new Padding(12, 10, 0, 0),
                    Font = new Font(Font, FontStyle.Bold),
                };
                var changes = new TextBox {
                    Text = notes, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                    BackColor = SystemColors.Control,
                };
                var guidance = new Label {
                    Text = "Get the DLL from Account. Close ACT, replace the DLL, then reopen ACT.",
                    Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 6, 0, 0),
                };
                var actions = new FlowLayoutPanel {
                    Dock = DockStyle.Bottom, Height = 43, FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8, 5, 8, 0),
                };
                var later = new Button { Text = "Later", Width = 84, DialogResult = DialogResult.Cancel };
                var skip = new Button { Text = "Skip this release", Width = 125,
                                        DialogResult = DialogResult.No };
                var get = new Button { Text = "Get update", Width = 90,
                                       DialogResult = DialogResult.Yes };
                actions.Controls.Add(later);
                actions.Controls.Add(skip);
                actions.Controls.Add(get);
                popup.Controls.Add(changes);
                popup.Controls.Add(guidance);
                popup.Controls.Add(actions);
                popup.Controls.Add(heading);
                popup.AcceptButton = get;
                popup.CancelButton = later;
                return popup.ShowDialog(FindForm());
            }
        }

        /// <summary>
        /// The test track's own panel: which logs were found, what each one is
        /// doing, and the two switches that decide it.
        ///
        /// It exists because multi-log upload is the one thing in this plugin a
        /// raider cannot verify by looking at the site — four characters that
        /// all logged the same pull look identical to one character logging it,
        /// until you notice the numbers. The list says, per log, whether the
        /// plugin is sending it, watching it, or deliberately leaving it alone,
        /// and why.
        /// </summary>
        private GroupBox BuildMultiLog()
        {
            var group = new GroupBox {
                Text = "Multiple characters (test build)", Dock = DockStyle.Top, Height = 295,
                Padding = new Padding(12, 8, 12, 10),
            };

            _logs = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
            };
            _logs.Columns.Add("Character", 110);
            _logs.Columns.Add("State", 340);
            _logs.Columns.Add("Lines sent", 90, HorizontalAlignment.Right);
            _logs.ItemChecked += OnLogChecked;

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            var addFolder = new Button { Text = "Add log folder...", Dock = DockStyle.Left,
                                         Width = 140 };
            addFolder.Click += OnAddLogFolder;
            buttons.Controls.Add(addFolder);

            _onlyWhenFighting = new CheckBox
            {
                Text = "Hold each character's log until they fight",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.OnlyWhenFighting,
            };
            _onlyWhenFighting.CheckedChanged += (s, e) =>
            {
                _settings.OnlyWhenFighting = _onlyWhenFighting.Checked;
                _settings.Save();
            };
            _multiLog = new CheckBox
            {
                Text = "Follow every EQ2 log in the selected folders",
                Dock = DockStyle.Top,
                Height = 24,
                Checked = _settings.MultiLog,
            };
            _multiLog.CheckedChanged += OnMultiLogToggled;

            group.Controls.Add(_logs);
            group.Controls.Add(buttons);
            group.Controls.Add(Note(
                "Held lines stay in the log and upload when that character fights."));
            group.Controls.Add(_onlyWhenFighting);
            group.Controls.Add(_multiLog);

            _logRefresh = new System.Windows.Forms.Timer { Interval = 1000 };
            _logRefresh.Tick += (s, e) => RefreshLogs();
            _logRefresh.Start();
            return group;
        }

        /// <summary>Redraw the log list from the uploader's own view of it.
        /// Rows are matched on path so ticking a box does not fight the timer.</summary>
        private void RefreshLogs()
        {
            if (_logs == null || IsDisposed) return;
            var found = _uploader.Discovered;
            var statuses = new System.Collections.Generic.Dictionary<string, LogStatus>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var status in _uploader.LogStatuses()) statuses[status.Path] = status;

            _rebuilding = true;
            try
            {
                _logs.BeginUpdate();
                var seen = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var log in found)
                {
                    seen.Add(log.Path);
                    var item = Row(log.Path);
                    if (item == null)
                    {
                        item = new ListViewItem(log.Character) { Tag = log.Path };
                        item.SubItems.Add("");
                        item.SubItems.Add("");
                        _logs.Items.Add(item);
                    }
                    var uploads = _settings.Uploads(log.Character);
                    if (item.Checked != uploads) item.Checked = uploads;
                    item.Text = log.Character;

                    LogStatus status;
                    var known = statuses.TryGetValue(log.Path, out status);
                    item.SubItems[1].Text = !uploads ? "Excluded"
                        : known ? status.Message
                        : log.Stale ? "Quiet — nothing has been written for a while"
                        : log.Live ? "Starting..."
                        : "Idle";
                    item.SubItems[2].Text = known && status.LinesSent > 0
                        ? status.LinesSent.ToString("N0") : "";
                    item.ForeColor = known && status.IsError ? Color.Firebrick
                                   : uploads ? SystemColors.ControlText
                                   : SystemColors.GrayText;
                }
                for (var i = _logs.Items.Count - 1; i >= 0; i--)
                    if (!seen.Contains((string)_logs.Items[i].Tag)) _logs.Items.RemoveAt(i);
            }
            finally
            {
                _logs.EndUpdate();
                _rebuilding = false;
            }
        }

        private ListViewItem Row(string path)
        {
            foreach (ListViewItem item in _logs.Items)
                if (string.Equals((string)item.Tag, path, StringComparison.OrdinalIgnoreCase))
                    return item;
            return null;
        }

        private void OnLogChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_rebuilding) return;
            _settings.SetUploads(e.Item.Text, e.Item.Checked);
        }

        private void OnMultiLogToggled(object sender, EventArgs e)
        {
            _settings.MultiLog = _multiLog.Checked;
            _settings.Save();
            // The choice is made once per run of the worker, so an uploader that
            // is already going has to be turned over for it to take.
            if (_uploader.IsRunning)
            {
                _uploader.Stop(closeSession: false);
                _uploader.Start();
            }
        }

        private void OnAddLogFolder(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Pick an EverQuest II logs folder (a second install, say).",
                ShowNewFolderButton = false,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.AddLogFolder(dialog.SelectedPath);
                RefreshLogs();
            }
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
