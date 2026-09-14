using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AiCad.Ai;
using AiCad.Config;
using AiCad.Execution;
using AiCad.Json;
using AiCad.Model;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AiCad.UI
{
    /// <summary>
    /// The docked chat panel. It never touches the drawing itself: it turns a
    /// sentence into a validated plan, then hands that plan to the AICADDRAW
    /// command, which runs in proper document context.
    /// </summary>
    public class AiPaletteControl : UserControl
    {
        private RichTextBox _log;
        private TextBox _input;
        private Button _sendButton;
        private Button _settingsButton;
        private CheckBox _pickPoint;
        private Label _status;

        private readonly List<ChatTurn> _history = new List<ChatTurn>();
        private AiCadConfig _config;
        private bool _busy;

        private const int MaxHistoryTurns = 6;
        private const int MaxHistoryChars = 4000;

        public AiPaletteControl()
        {
            _config = AiCadConfig.Load();
            BuildUi();
            WriteSystem("AiCad ready. Provider: " + DescribeProvider());
            WriteSystem("Describe what to draw, for example: " +
                        "\"800 x 600 x 250 enclosure with 3 DIN rails and a gland plate\".");
            if (string.IsNullOrEmpty(_config.ActiveKey))
            {
                WriteError("No API key configured. Click Settings, or put one in:");
                WriteError("  " + _config.LoadedFrom);
                WriteError("Try AICADTEST meanwhile - it draws a sample with no API call.");
            }
            else
            {
                WriteSystem("Settings file: " + _config.LoadedFrom);
            }
        }

        // ------------------------------------------------------------- layout

        private void BuildUi()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;
            MinimumSize = new Size(260, 240);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Padding = new Padding(6);

            // Row 0 - toolbar
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = false;
            bar.Margin = new Padding(0, 0, 0, 4);

            _settingsButton = new Button();
            _settingsButton.Text = "Settings";
            _settingsButton.AutoSize = true;
            _settingsButton.Click += OnSettingsClick;
            bar.Controls.Add(_settingsButton);

            Button clearButton = new Button();
            clearButton.Text = "Clear";
            clearButton.AutoSize = true;
            clearButton.Click += delegate { _log.Clear(); _history.Clear(); };
            bar.Controls.Add(clearButton);

            _pickPoint = new CheckBox();
            // Off by default so a request needs no command-line interaction at all.
            _pickPoint.Text = "Pick placement point";
            _pickPoint.Checked = false;
            _pickPoint.AutoSize = true;
            _pickPoint.Margin = new Padding(10, 6, 0, 0);
            bar.Controls.Add(_pickPoint);

            root.Controls.Add(bar, 0, 0);

            // Row 1 - transcript
            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = SystemColors.Window;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Segoe UI", 8.5f);
            _log.DetectUrls = false;
            root.Controls.Add(_log, 0, 1);

            // Row 2 - input
            _input = new TextBox();
            _input.Dock = DockStyle.Fill;
            _input.Multiline = true;
            _input.Height = 62;
            _input.Font = new Font("Segoe UI", 9f);
            _input.KeyDown += OnInputKeyDown;
            _input.Margin = new Padding(0, 4, 0, 4);
            root.Controls.Add(_input, 0, 2);

            // Row 3 - send + status
            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.ColumnCount = 2;
            footer.RowCount = 1;
            footer.AutoSize = true;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _status = new Label();
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.AutoSize = false;
            _status.Text = "";
            footer.Controls.Add(_status, 0, 0);

            _sendButton = new Button();
            _sendButton.Text = "Draw  (Ctrl+Enter)";
            _sendButton.AutoSize = true;
            _sendButton.Click += OnSendClick;
            footer.Controls.Add(_sendButton, 1, 0);

            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
        }

        private string DescribeProvider()
        {
            try
            {
                return ProviderFactory.Create(_config).DisplayName;
            }
            catch (Exception)
            {
                return _config.Provider;
            }
        }

        // ------------------------------------------------------------ actions

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                OnSendClick(sender, EventArgs.Empty);
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            using (SettingsForm form = new SettingsForm(_config))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    _config = AiCadConfig.Load();
                    WriteSystem("Settings saved. Provider: " + DescribeProvider());
                }
            }
        }

        private void OnSendClick(object sender, EventArgs e)
        {
            if (_busy) return;
            string message = _input.Text.Trim();
            if (message.Length == 0) return;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                WriteError("No drawing is open.");
                return;
            }

            // Re-read every time: settings may have been changed in the
            // standalone app since this palette was created.
            _config = AiCadConfig.Load();
            if (string.IsNullOrEmpty(_config.ActiveKey))
            {
                WriteError("No API key configured for provider '" + _config.Provider + "'.");
                WriteError("  " + _config.LoadedFrom);
                return;
            }

            _input.Clear();
            WriteUser(message);
            WriteSystem(DescribeProvider() + ", max tokens " +
                        _config.MaxTokens.ToString(CultureInfo.InvariantCulture) + ".");
            SetBusy(true, "Asking " + DescribeProvider() + "...");

            DrawingSnapshot snapshot;
            try
            {
                snapshot = DrawingInfo.Capture(doc);
            }
            catch (Exception ex)
            {
                WriteError("Could not read the drawing: " + ex.Message);
                SetBusy(false, "");
                return;
            }

            string systemPrompt = PromptBuilder.BuildSystemPrompt(_config, snapshot, Capabilities.UsageCatalogue(),
                    ExampleStore.Format(ExampleStore.Select(message, 2), 12000));
            List<ChatTurn> history = new List<ChatTurn>(_history);
            IAiProvider provider = ProviderFactory.Create(_config);

            ThreadPool.QueueUserWorkItem(delegate
            {
                AiResponse response = provider.RequestPlan(systemPrompt, history, message);

                // One automatic repair round-trip when the plan fails validation.
                DrawPlan plan = null;
                OpIssues issues = null;
                if (response.Success)
                {
                    plan = DrawPlan.FromJson(response.Plan);
                    issues = PlanValidator.Validate(plan, Capabilities.Registry);
                    if (!issues.Ok && plan.Ops.Count > 0)
                    {
                        string repair = "The plan you returned was rejected by the validator:\n" +
                                        string.Join("\n", issues.Errors.ToArray()) +
                                        "\nReturn a corrected plan for the same request.";
                        List<ChatTurn> repairHistory = new List<ChatTurn>(history);
                        repairHistory.Add(new ChatTurn("user", message));
                        repairHistory.Add(new ChatTurn("assistant", Shorten(response.Plan.ToString())));
                        AiResponse second = provider.RequestPlan(systemPrompt, repairHistory, repair);
                        if (second.Success)
                        {
                            DrawPlan retryPlan = DrawPlan.FromJson(second.Plan);
                            OpIssues retryIssues = PlanValidator.Validate(retryPlan, Capabilities.Registry);
                            if (retryIssues.Ok)
                            {
                                response = second;
                                plan = retryPlan;
                                issues = retryIssues;
                            }
                        }
                    }
                }

                AiResponse capturedResponse = response;
                DrawPlan capturedPlan = plan;
                OpIssues capturedIssues = issues;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        OnResponse(message, capturedResponse, capturedPlan, capturedIssues);
                    }));
                }
                catch (Exception)
                {
                    // Palette closed while the request was in flight.
                }
            });
        }

        private void OnResponse(string userMessage, AiResponse response, DrawPlan plan, OpIssues issues)
        {
            SetBusy(false, "");

            if (!response.Success)
            {
                WriteError(response.Error);
                return;
            }

            if (!string.IsNullOrEmpty(plan.Notes)) WriteAssistant(plan.Notes);

            if (plan.Ops.Count == 0)
            {
                WriteSystem("No geometry was produced.");
                return;
            }

            if (!issues.Ok)
            {
                WriteError("Plan rejected - nothing was drawn:");
                for (int i = 0; i < issues.Errors.Count; i++) WriteError("  " + issues.Errors[i]);
                return;
            }

            RememberTurn(userMessage, response.Plan);

            PlanQueue.Set(plan, _pickPoint.Checked);
            WriteSystem(plan.Describe() + " - drawing...");

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                WriteError("No drawing is open.");
                return;
            }

            // Runs in real command context, where the editor can prompt for a point.
            doc.SendStringToExecute("_.AICADDRAW ", true, false, true);
        }

        private void RememberTurn(string userMessage, JsonValue plan)
        {
            _history.Add(new ChatTurn("user", userMessage));
            _history.Add(new ChatTurn("assistant", Shorten(plan.ToString())));
            while (_history.Count > MaxHistoryTurns * 2) _history.RemoveAt(0);
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= MaxHistoryChars) return s;
            return s.Substring(0, MaxHistoryChars) + " ...(truncated)";
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _sendButton.Enabled = !busy;
            _input.Enabled = !busy;
            _status.Text = status;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        // -------------------------------------------------------------- log

        public void WriteUser(string text) { Append("You: " + text, Color.FromArgb(0, 90, 160), true); }
        public void WriteAssistant(string text) { Append(text, SystemColors.WindowText, false); }
        public void WriteSystem(string text) { Append(text, Color.FromArgb(100, 100, 100), false); }
        public void WriteError(string text) { Append(text, Color.FromArgb(180, 30, 30), false); }

        public void WriteResult(string text) { Append(text, Color.FromArgb(20, 120, 60), false); }

        private void Append(string text, Color color, bool bold)
        {
            if (_log == null) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { Append(text, color, bold); })); }
                catch (Exception) { }
                return;
            }

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.SelectionFont = new Font(_log.Font, bold ? FontStyle.Bold : FontStyle.Regular);
            _log.AppendText(text + Environment.NewLine);
            _log.SelectionColor = _log.ForeColor;
            _log.ScrollToCaret();
        }
    }

    /// <summary>
    /// Hand-off between the modeless palette and the AICADDRAW command. Holds at
    /// most one plan; the command consumes it.
    /// </summary>
    public static class PlanQueue
    {
        private static readonly object Gate = new object();
        private static DrawPlan _plan;
        private static bool _askForPoint;

        public static void Set(DrawPlan plan, bool askForPoint)
        {
            lock (Gate)
            {
                _plan = plan;
                _askForPoint = askForPoint;
            }
        }

        public static DrawPlan Take(out bool askForPoint)
        {
            lock (Gate)
            {
                DrawPlan plan = _plan;
                askForPoint = _askForPoint;
                _plan = null;
                return plan;
            }
        }
    }
}
