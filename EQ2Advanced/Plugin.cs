using System;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using EQ2Advanced.Core;
using EQ2Advanced.Ingest;
using EQ2Advanced.Net;
using EQ2Advanced.Ui;

namespace EQ2Advanced
{
    /// <summary>
    /// ACT entry point for the eq2advanced uploader.
    ///
    /// The plugin does one job: get the combat log ACT is already reading up to
    /// eq2advanced.com, with the raider in control of who can see it. All the
    /// parsing lives on the server — this end deliberately understands nothing
    /// about EQ2 combat beyond "a line starts with (epoch)".
    /// </summary>
    public class EQ2AdvancedPlugin : IActPluginV1
    {
        public const string Version = "0.2.3";

        private Label _actStatus;
        private Settings _settings;
        private Uploader _uploader;
        private ConfigTab _tab;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _actStatus = pluginStatusText;
            pluginScreenSpace.Text = "eq2advanced";

            // Anything thrown out of here leaves ACT showing an enabled plugin
            // with an empty tab and no clue why. Catch it and PUT IT ON THE TAB:
            // the tab is the only surface the user is looking at, and a blank
            // one is indistinguishable from "this plugin does nothing".
            try
            {
                _settings = Settings.Load();

                var api = new ApiClient(_settings);
                _uploader = new Uploader(_settings, api);
                _uploader.StatusChanged += OnStatus;

                _tab = new ConfigTab(_settings, api, _uploader);
                pluginScreenSpace.Controls.Add(_tab);

                // Resume where we left off: someone who ticked "upload as I play"
                // expects it to still be on next time ACT starts, and the offset we
                // persisted means the raid picks up rather than restarting.
                if (_settings.LiveUpload && _settings.Paired) _uploader.Start();

                _actStatus.Text = _settings.Paired
                    ? "eq2advanced ready."
                    : "eq2advanced loaded — pair this device on its tab.";
            }
            catch (Exception ex)
            {
                ShowFailure(pluginScreenSpace, ex);
            }
        }

        private void ShowFailure(TabPage tab, Exception ex)
        {
            var text = "eq2advanced failed to start.\r\n\r\n" + ex + "\r\n\r\n"
                     + "Copy this and send it on — it says exactly what went wrong.";
            try { if (_actStatus != null) _actStatus.Text = "eq2advanced FAILED: " + ex.Message; }
            catch { }
            try
            {
                tab.Controls.Add(new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    Dock = DockStyle.Fill,
                    Text = text,
                });
            }
            catch { /* if even this fails, the status label above is all we have */ }
        }

        public void DeInitPlugin()
        {
            if (_uploader != null)
            {
                _uploader.StatusChanged -= OnStatus;
                // Don't close the session on the way out: ACT restarts mid-raid,
                // and closing would end the night on the site and start the next
                // batch in a new one. A genuinely finished session is closed by
                // the switch on the tab, or by the server's own idle timeout.
                try { _uploader.Stop(closeSession: false); } catch { }
                _uploader = null;
            }
            if (_tab != null) { _tab.Dispose(); _tab = null; }
            if (_actStatus != null) _actStatus.Text = "eq2advanced unloaded.";
        }

        private void OnStatus(UploaderStatus status)
        {
            var label = _actStatus;
            if (label == null) return;
            try
            {
                if (label.IsDisposed) return;
                label.BeginInvoke((Action)(() =>
                {
                    if (!label.IsDisposed) label.Text = "eq2advanced: " + status.Message;
                }));
            }
            catch { /* ACT is tearing the form down */ }
        }
    }
}
