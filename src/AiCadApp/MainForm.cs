using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AiCad.Ai;
using AiCad.Config;
using AiCad.Json;
using AiCad.Model;
using AiCad.UI;
// System.Threading.Timer and the WinForms one collide; the UI needs the latter.
using WinFormsTimer = System.Windows.Forms.Timer;

namespace AiCadApp
{
    /// <summary>
    /// The standalone assistant. It owns the conversation and the AI calls, and
    /// hands finished plans to AutoCAD over COM; the in-AutoCAD engine does the
    /// drawing, so all geometry stays validated and undoable in one step.
    /// </summary>
    public class MainForm : Form
    {
        private readonly AcadBridge _acad = new AcadBridge();
        private readonly List<ChatTurn> _history = new List<ChatTurn>();
        private AiCadConfig _config;

        private Label _connectionStatus;
        private Button _connectButton;
        private Button _launchButton;
        private Button _loadEngineButton;
        private Button _sampleButton;
        private Button _settingsButton;
        private ChatView _chat;
        private ChatView _activity;
        private ListBox _chatList;
        private string _currentChatId;
        private bool _loadingChat;

        // What the previous plan drew, so a refinement can replace it in place.
        private List<string> _lastHandles = new List<string>();
        private bool _hasLastInsertion;
        private double _lastInsertionX;
        private double _lastInsertionY;
        private CheckBox _updatePrevious;
        private TextBox _input;
        private Button _drawButton;
        private CheckBox _pickPoint;
        private Label _busyStatus;

        private WinFormsTimer _connectionTimer;
        private WinFormsTimer _resultTimer;

        private string _pendingId;
        private DateTime _pendingSince;
        private string _pendingPlanName;
        private bool _busy;

        // One automatic repair round-trip per request, as in the palette.
        private int _repairAttempts;
        private string _lastUserMessage;
        private JsonValue _lastPlanJson;

        private const int MaxHistoryTurns = 6;
        private const int MaxHistoryChars = 4000;
        private const int ResultTimeoutSeconds = 120;

        public MainForm()
        {
            _config = AiCadConfig.Load();
            BuildUi();

            WriteSystem("AiCad assistant. Provider: " + DescribeProvider());
            WriteSystem("Settings file: " + _config.LoadedFrom);
            if (string.IsNullOrEmpty(_config.ActiveKey))
                WriteError("No API key configured - click Settings.");

            _currentChatId = ChatStore.NewId();
            RefreshChatList();

            _connectionTimer = new WinFormsTimer();
            _connectionTimer.Interval = 3000;
            _connectionTimer.Tick += OnConnectionTick;
            _connectionTimer.Start();

            _resultTimer = new WinFormsTimer();
            _resultTimer.Interval = 300;
            _resultTimer.Tick += OnResultTick;

            RefreshConnection(true);
        }

        // ------------------------------------------------------------ layout

        private void BuildUi()
        {
            Text = "AiCad - AutoCAD drawing assistant";
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = new Font("Segoe UI", 9f);

            // Conversation on the left, everything mechanical on the right.
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Theme.Background;
            root.ColumnCount = 2;
            root.RowCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290f));

            root.Controls.Add(BuildConversation(), 0, 0);
            root.Controls.Add(BuildSidebar(), 1, 0);
            Controls.Add(root);
        }

        /// <summary>Left column: the chat itself and the composer.</summary>
        private Control BuildConversation()
        {
            TableLayoutPanel column = new TableLayoutPanel();
            column.Dock = DockStyle.Fill;
            column.BackColor = Theme.Background;
            column.ColumnCount = 1;
            column.RowCount = 2;
            column.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            column.Margin = new Padding(0);

            _chat = new ChatView(false);
            _chat.Dock = DockStyle.Fill;
            _chat.Margin = new Padding(0);
            _chat.EmptyText = "Describe what to draw.\r\n\r\n" +
                              "\u201cStar-delta starter control circuit as a ladder diagram\u201d\r\n" +
                              "\u201c800 x 600 x 250 enclosure with 3 DIN rails\u201d\r\n" +
                              "\u201cTerminal strip with 12 terminals, wire numbers from W101\u201d";
            column.Controls.Add(_chat, 0, 0);

            Panel composer = new Panel();
            composer.Dock = DockStyle.Fill;
            composer.BackColor = Theme.Background;
            composer.Padding = new Padding(20, 6, 20, 14);
            composer.Height = 148;

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 40;
            footer.BackColor = Theme.Background;

            _pickPoint = new CheckBox();
            _pickPoint.Text = "Pick placement point in AutoCAD";
            _pickPoint.AutoSize = true;
            _pickPoint.Checked = false;
            _pickPoint.ForeColor = Theme.TextMuted;
            _pickPoint.BackColor = Theme.Background;
            _pickPoint.Location = new Point(2, 11);
            footer.Controls.Add(_pickPoint);

            _updatePrevious = new CheckBox();
            _updatePrevious.Text = "Update previous drawing";
            _updatePrevious.AutoSize = true;
            _updatePrevious.Checked = true;
            _updatePrevious.Enabled = false;
            _updatePrevious.ForeColor = Theme.TextMuted;
            _updatePrevious.BackColor = Theme.Background;
            _updatePrevious.Location = new Point(215, 11);
            footer.Controls.Add(_updatePrevious);

            _drawButton = new FlatButton("Draw in AutoCAD", true);
            _drawButton.Width = 160;
            _drawButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _drawButton.Click += OnDrawClick;
            footer.Controls.Add(_drawButton);

            _busyStatus = new Label();
            _busyStatus.AutoSize = false;
            _busyStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _busyStatus.TextAlign = ContentAlignment.MiddleRight;
            _busyStatus.ForeColor = Theme.TextMuted;
            _busyStatus.Size = new Size(230, 30);
            footer.Controls.Add(_busyStatus);

            footer.Resize += delegate
            {
                _drawButton.Location = new Point(footer.Width - _drawButton.Width, 6);
                _busyStatus.Location = new Point(footer.Width - _drawButton.Width - 240, 6);
            };

            Panel inputShell = new Panel();
            inputShell.Dock = DockStyle.Fill;
            inputShell.BackColor = Theme.Background;
            inputShell.Padding = new Padding(14, 11, 14, 11);
            inputShell.Paint += delegate(object sender, PaintEventArgs e)
            {
                Panel host = (Panel)sender;
                Rectangle r = new Rectangle(0, 0, host.Width - 1, host.Height - 1);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = ChatView.Rounded(r, 12))
                using (SolidBrush fill = new SolidBrush(Theme.Surface))
                using (Pen edge = new Pen(Theme.Border))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(edge, path);
                }
            };

            _input = new TextBox();
            _input.Dock = DockStyle.Fill;
            _input.Multiline = true;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = Theme.Surface;
            _input.ForeColor = Theme.TextPrimary;
            _input.Font = new Font("Segoe UI", 10.5f);
            _input.KeyDown += OnInputKeyDown;
            inputShell.Controls.Add(_input);

            composer.Controls.Add(inputShell);
            composer.Controls.Add(footer);
            column.Controls.Add(composer, 0, 1);
            return column;
        }

        /// <summary>Right column: connection, actions and the activity log.</summary>
        private Control BuildSidebar()
        {
            TableLayoutPanel side = new TableLayoutPanel();
            side.Dock = DockStyle.Fill;
            side.BackColor = Theme.Surface;
            side.ColumnCount = 1;
            side.RowCount = 7;
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // connection
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // new chat
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // RECENT
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));           // saved chats
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // actions
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));               // ACTIVITY
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));           // activity log
            side.Margin = new Padding(0);
            side.Padding = new Padding(0);

            _connectionStatus = new Label();
            _connectionStatus.Dock = DockStyle.Fill;
            _connectionStatus.AutoSize = false;
            _connectionStatus.Height = 46;
            _connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
            _connectionStatus.Padding = new Padding(14, 0, 10, 0);
            _connectionStatus.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _connectionStatus.Margin = new Padding(0);
            side.Controls.Add(_connectionStatus, 0, 0);

            Panel newChatHost = new Panel();
            newChatHost.Dock = DockStyle.Fill;
            newChatHost.Height = 46;
            newChatHost.BackColor = Theme.Surface;
            newChatHost.Padding = new Padding(14, 8, 14, 6);
            FlatButton newChat = new FlatButton("+  New chat", true);
            newChat.Dock = DockStyle.Fill;
            newChat.Click += OnNewChatClick;
            newChatHost.Controls.Add(newChat);
            side.Controls.Add(newChatHost, 0, 1);

            side.Controls.Add(SectionHeading("RECENT"), 0, 2);

            _chatList = new ListBox();
            _chatList.Dock = DockStyle.Fill;
            _chatList.BorderStyle = BorderStyle.None;
            _chatList.BackColor = Theme.Surface;
            _chatList.ForeColor = Theme.TextPrimary;
            _chatList.Font = new Font("Segoe UI", 8.25f);
            _chatList.IntegralHeight = false;
            _chatList.Margin = new Padding(8, 0, 8, 8);
            _chatList.SelectedIndexChanged += OnChatSelected;
            side.Controls.Add(_chatList, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.FlowDirection = FlowDirection.TopDown;
            actions.WrapContents = false;
            actions.BackColor = Theme.Surface;
            actions.Padding = new Padding(14, 12, 14, 12);
            actions.Margin = new Padding(0);

            _connectButton = AddButton(actions, "Reconnect", OnConnectClick);
            _launchButton = AddButton(actions, "Start AutoCAD", OnLaunchClick);
            _loadEngineButton = AddButton(actions, "Load engine", OnLoadEngineClick);
            _sampleButton = AddButton(actions, "Draw sample", OnSampleClick);
            _settingsButton = AddButton(actions, "Settings", OnSettingsClick);
            AddButton(actions, "Delete this chat", OnDeleteChatClick);

            side.Controls.Add(actions, 0, 4);

            side.Controls.Add(SectionHeading("ACTIVITY"), 0, 5);

            _activity = new ChatView(true);
            _activity.Dock = DockStyle.Fill;
            _activity.Margin = new Padding(0);
            _activity.EmptyText = "Diagnostics appear here.";
            side.Controls.Add(_activity, 0, 6);

            return side;
        }

        private static Label SectionHeading(string text)
        {
            Label heading = new Label();
            heading.Text = text;
            heading.Dock = DockStyle.Fill;
            heading.AutoSize = false;
            heading.Height = 24;
            heading.TextAlign = ContentAlignment.MiddleLeft;
            heading.Padding = new Padding(16, 0, 0, 0);
            heading.ForeColor = Theme.TextMuted;
            heading.Font = new Font("Segoe UI Semibold", 7.5f);
            heading.Margin = new Padding(0);
            return heading;
        }

        // ------------------------------------------------------- saved chats

        private void OnNewChatClick(object sender, EventArgs e)
        {
            StartNewChat();
        }

        private void StartNewChat()
        {
            SaveCurrentChat();
            _currentChatId = ChatStore.NewId();
            _chat.Clear();
            _history.Clear();
            _repairAttempts = 0;
            _lastUserMessage = null;
            _lastPlanJson = null;
            ForgetPreviousDrawing();
            RefreshChatList();
        }

        /// <summary>Written after every exchange, so nothing is lost on a crash.</summary>
        private void SaveCurrentChat()
        {
            if (string.IsNullOrEmpty(_currentChatId)) return;
            List<ChatMessage> messages = _chat.Snapshot();
            if (messages.Count == 0) return;
            ChatStore.Save(_currentChatId, messages, _history);
        }

        private void RefreshChatList()
        {
            if (_chatList == null) return;
            _loadingChat = true;
            try
            {
                _chatList.Items.Clear();
                List<ChatSummary> saved = ChatStore.List(40);
                int selected = -1;
                for (int i = 0; i < saved.Count; i++)
                {
                    _chatList.Items.Add(saved[i]);
                    if (saved[i].Id == _currentChatId) selected = i;
                }
                if (selected >= 0) _chatList.SelectedIndex = selected;
            }
            finally
            {
                _loadingChat = false;
            }
        }

        private void OnChatSelected(object sender, EventArgs e)
        {
            if (_loadingChat || _busy) return;

            ChatSummary summary = _chatList.SelectedItem as ChatSummary;
            if (summary == null || summary.Id == _currentChatId) return;

            SaveCurrentChat();

            List<ChatMessage> messages;
            List<ChatTurn> history;
            if (!ChatStore.Load(summary.Id, out messages, out history))
            {
                WriteSystem("Could not reopen that conversation.");
                return;
            }

            _currentChatId = summary.Id;
            _chat.Load(messages);
            _history.Clear();
            _history.AddRange(history);
            _repairAttempts = 0;
            _lastUserMessage = null;
            _lastPlanJson = null;
            ForgetPreviousDrawing();
        }

        /// <summary>
        /// Handles belong to one drawing in one session. Switching chats must not
        /// let a later refinement erase geometry from an unrelated request.
        /// </summary>
        private void ForgetPreviousDrawing()
        {
            _lastHandles.Clear();
            _hasLastInsertion = false;
            if (_updatePrevious != null) _updatePrevious.Enabled = false;
        }

        private void OnDeleteChatClick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_currentChatId)) return;
            if (MessageBox.Show(this, "Delete this conversation?", "AiCad",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            ChatStore.Delete(_currentChatId);
            _currentChatId = ChatStore.NewId();
            _chat.Clear();
            _history.Clear();
            RefreshChatList();
        }

        private static Button AddButton(Control parent, string text, EventHandler handler)
        {
            FlatButton button = new FlatButton(text, false);
            button.Width = 258;
            button.Margin = new Padding(0, 0, 0, 6);
            button.Click += handler;
            parent.Controls.Add(button);
            return button;
        }

        // -------------------------------------------------------- connection

        private void OnConnectionTick(object sender, EventArgs e)
        {
            RefreshConnection(false);
        }

        /// <summary>Keeps the banner and buttons in step with AutoCAD's actual state.</summary>
        private void RefreshConnection(bool announce)
        {
            bool wasConnected = _acad.IsConnected;

            if (_acad.IsConnected)
            {
                if (!_acad.IsAlive() && announce) WriteError("Lost the connection to AutoCAD.");
            }
            else
            {
                string error;
                if (_acad.TryConnect(out error) && announce)
                    WriteResult("Connected to AutoCAD.");
            }

            bool connected = _acad.IsConnected;
            if (wasConnected && !connected) WriteError("AutoCAD closed - reconnect when it is running again.");

            if (connected)
            {
                string caption = _acad.Caption;
                _connectionStatus.BackColor = Theme.ConnectedBg;
                _connectionStatus.ForeColor = Theme.ConnectedText;
                _connectionStatus.Text = "Connected  -  " + (caption ?? "AutoCAD");
            }
            else
            {
                _connectionStatus.BackColor = Theme.DisconnectedBg;
                _connectionStatus.ForeColor = Theme.DisconnectedText;
                _connectionStatus.Text = "Not connected  -  start AutoCAD, then click Reconnect";
            }

            _launchButton.Enabled = !connected;
            _loadEngineButton.Enabled = connected;
            _sampleButton.Enabled = connected;
            _drawButton.Enabled = connected && !_busy;
        }

        private void OnConnectClick(object sender, EventArgs e)
        {
            // Always report, including "already connected" - a button that
            // prints nothing reads as a broken button.
            // Connection chatter is diagnostics, so it belongs in the sidebar.
            RefreshConnection(false);
            if (_acad.IsConnected)
                WriteSystem("Connected to " + (_acad.Caption ?? "AutoCAD") + ".");
            else
                WriteSystem("No running AutoCAD found. Start AutoCAD, then click Reconnect.");
        }

        private void OnLaunchClick(object sender, EventArgs e)
        {
            WriteSystem("Starting AutoCAD - this usually takes under a minute...");
            string error;
            if (_acad.Launch(out error))
            {
                WriteSystem("AutoCAD is starting. The banner turns green once it is ready.");
            }
            else
            {
                WriteError("Could not start AutoCAD: " + error);
            }
            RefreshConnection(false);
        }

        /// <summary>
        /// NETLOADs the engine DLL shipped beside this exe. Only needed when the
        /// AutoCAD-side bundle was not installed.
        /// </summary>
        private void OnLoadEngineClick(object sender, EventArgs e)
        {
            string dll = EnginePath();
            if (dll == null)
            {
                WriteError("AiCad.dll was not found next to this application.");
                return;
            }

            try
            {
                // LISP string literals need their backslashes doubled.
                string lisp = dll.Replace("\\", "\\\\");
                _acad.SendCommand("(command \"_.NETLOAD\" \"" + lisp + "\")\n");
                WriteSystem("Asked AutoCAD to load the engine from:");
                WriteSystem("  " + dll);
                WriteSystem("If AutoCAD shows a security warning, choose Load Once.");
            }
            catch (Exception ex)
            {
                WriteError("Could not load the engine: " + ex.Message);
            }
        }

        private static string EnginePath()
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return null;
                string path = Path.Combine(dir, "AiCad.dll");
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnSampleClick(object sender, EventArgs e)
        {
            try
            {
                _acad.SendCommand("_.AICADTEST\n");
                WriteSystem("Sent AICADTEST. Press Enter in AutoCAD to place it at the origin.");
            }
            catch (Exception ex)
            {
                WriteError("Could not run the sample: " + ex.Message);
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

        private string DescribeProvider()
        {
            try { return ProviderFactory.Create(_config).DisplayName; }
            catch (Exception) { return _config.Provider; }
        }

        // ------------------------------------------------------------ drawing

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                OnDrawClick(sender, EventArgs.Empty);
            }
        }

        private void OnDrawClick(object sender, EventArgs e)
        {
            if (_busy) return;

            string message = _input.Text.Trim();
            if (message.Length == 0) return;

            if (!_acad.IsConnected)
            {
                WriteError("Not connected to AutoCAD.");
                return;
            }

            // Settings can also be edited in the in-AutoCAD palette or the file
            // itself, so never trust a cached copy.
            _config = AiCadConfig.Load();
            if (string.IsNullOrEmpty(_config.ActiveKey))
            {
                WriteError("No API key configured for provider '" + _config.Provider + "' - click Settings.");
                return;
            }

            string catalogue = ReadCapabilities();
            if (catalogue == null)
            {
                // The engine is demand-loaded, so it stays dormant until an
                // AICAD command runs. "Draw sample" is the cheapest trigger.
                WriteError("The AutoCAD-side engine has not loaded yet.");
                WriteError("Click \"Draw sample\" once - that wakes it up and proves it works.");
                return;
            }

            _input.Clear();
            WriteUser(message);
            // State the settings actually in force, so a stale or unexpected
            // value can never be silently blamed on the model.
            WriteSystem(DescribeProvider() + ", max tokens " +
                        _config.MaxTokens.ToString(CultureInfo.InvariantCulture) +
                        ", config " + _config.LoadedFrom);
            _repairAttempts = 0;
            _lastUserMessage = message;

            DrawingSnapshot snapshot = _acad.CaptureSnapshot();
            string systemPrompt = PromptBuilder.BuildSystemPrompt(_config, snapshot, catalogue);
            RequestPlan(systemPrompt, new List<ChatTurn>(_history), message);
        }

        private void RequestPlan(string systemPrompt, List<ChatTurn> history, string message)
        {
            SetBusy(true, "Asking " + DescribeProvider() + "...");
            IAiProvider provider = ProviderFactory.Create(_config);

            ThreadPool.QueueUserWorkItem(delegate
            {
                AiResponse response = provider.RequestPlan(systemPrompt, history, message);
                try
                {
                    BeginInvoke(new Action(delegate { OnPlanReceived(systemPrompt, response); }));
                }
                catch (Exception)
                {
                    // Window closed while the request was in flight.
                }
            });
        }

        private void OnPlanReceived(string systemPrompt, AiResponse response)
        {
            if (!response.Success)
            {
                SetBusy(false, "");
                WriteError(response.Error);
                return;
            }

            DrawPlan plan = DrawPlan.FromJson(response.Plan);
            if (!string.IsNullOrEmpty(plan.Notes)) WriteAssistant(plan.Notes);

            if (plan.Ops.Count == 0)
            {
                SetBusy(false, "");
                WriteSystem("No geometry was produced.");
                return;
            }

            _lastPlanJson = response.Plan;
            _pendingPlanName = plan.Name;

            try
            {
                bool replace = _updatePrevious.Checked && _updatePrevious.Enabled &&
                               _lastHandles.Count > 0 && !_pickPoint.Checked;
                _pendingId = replace
                    ? PlanInbox.WriteRequest(response.Plan, false, _lastHandles,
                                             _hasLastInsertion, _lastInsertionX, _lastInsertionY)
                    : PlanInbox.WriteRequest(response.Plan, _pickPoint.Checked);
                _pendingSince = DateTime.UtcNow;
                _acad.SendCommand("_.AICADSYNC\n");

                SetBusy(true, "Drawing in AutoCAD...");
                WriteSystem(plan.Describe() + " - sent to AutoCAD.");
                if (_pickPoint.Checked)
                    WriteSystem("Switch to AutoCAD and pick the insertion point.");

                _resultTimer.Start();
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                WriteError("Could not send the plan to AutoCAD: " + ex.Message);
                _pendingId = null;
            }
        }

        /// <summary>Polls for the engine's result file.</summary>
        private void OnResultTick(object sender, EventArgs e)
        {
            if (_pendingId == null)
            {
                _resultTimer.Stop();
                return;
            }

            JsonValue result = null;
            try { result = PlanInbox.TryTakeResult(_pendingId); }
            catch (Exception) { }

            if (result == null)
            {
                if ((DateTime.UtcNow - _pendingSince).TotalSeconds > ResultTimeoutSeconds)
                {
                    _resultTimer.Stop();
                    _pendingId = null;
                    SetBusy(false, "");
                    WriteError("AutoCAD did not report back within " + ResultTimeoutSeconds + " seconds.");
                    WriteError("Check whether it is waiting for input at the command line.");
                }
                return;
            }

            _resultTimer.Stop();
            _pendingId = null;
            HandleResult(result);
        }

        private void HandleResult(JsonValue result)
        {
            bool success = result.GetBool("success", false);
            List<JsonValue> errors = result.GetArray("errors");
            List<JsonValue> warnings = result.GetArray("warnings");

            if (success)
            {
                SetBusy(false, "");
                int count = result.GetInt("entityCount", 0);
                WriteResult("Drew " + count.ToString(CultureInfo.InvariantCulture) +
                            " entities for \"" + (_pendingPlanName ?? "plan") + "\".");

                // Keep the handles so the next message can refine this drawing.
                _lastHandles.Clear();
                List<JsonValue> handles = result.GetArray("handles");
                for (int i = 0; i < handles.Count; i++)
                {
                    if (handles[i] != null && !string.IsNullOrEmpty(handles[i].Text))
                        _lastHandles.Add(handles[i].Text);
                }

                JsonValue where = result["insertion"];
                _hasLastInsertion = where != null && where.Kind == JsonKind.Array && where.Count >= 2;
                if (_hasLastInsertion)
                {
                    _lastInsertionX = where.At(0).Number;
                    _lastInsertionY = where.At(1).Number;
                }
                _updatePrevious.Enabled = _lastHandles.Count > 0;
                for (int i = 0; i < warnings.Count; i++)
                    WriteError("  warning: " + warnings[i].Text);

                RememberTurn(_lastUserMessage, _lastPlanJson);
                SaveCurrentChat();
                RefreshChatList();
                return;
            }

            // One repair round-trip, then report rather than draw something wrong.
            if (_repairAttempts == 0 && errors.Count > 0 && _lastPlanJson != null)
            {
                _repairAttempts++;
                WriteSystem("AutoCAD rejected the plan; asking for a correction...");

                List<string> lines = new List<string>();
                for (int i = 0; i < errors.Count; i++) lines.Add(errors[i].Text);

                string catalogue = ReadCapabilities();
                DrawingSnapshot snapshot = _acad.CaptureSnapshot();
                string systemPrompt = PromptBuilder.BuildSystemPrompt(_config, snapshot, catalogue);

                List<ChatTurn> history = new List<ChatTurn>(_history);
                history.Add(new ChatTurn("user", _lastUserMessage));
                history.Add(new ChatTurn("assistant", Shorten(_lastPlanJson.ToString())));

                string repair = "The plan you returned was rejected by the validator:\n" +
                                string.Join("\n", lines.ToArray()) +
                                "\nReturn a corrected plan for the same request.";
                RequestPlan(systemPrompt, history, repair);
                return;
            }

            SetBusy(false, "");
            WriteError("Nothing was drawn:");
            for (int i = 0; i < errors.Count; i++) WriteError("  " + errors[i].Text);
        }

        private static string ReadCapabilities()
        {
            try
            {
                string path = Path.Combine(AiCadConfig.Folder, "capabilities.txt");
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, System.Text.Encoding.UTF8);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void RememberTurn(string userMessage, JsonValue plan)
        {
            if (string.IsNullOrEmpty(userMessage) || plan == null) return;
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
            _drawButton.Enabled = !busy && _acad.IsConnected;
            _input.Enabled = !busy;
            _busyStatus.Text = status;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveCurrentChat();
            if (_connectionTimer != null) _connectionTimer.Stop();
            if (_resultTimer != null) _resultTimer.Stop();
            _acad.Release();
            base.OnFormClosed(e);
        }

        // ---------------------------------------------------------------- log

        private void WriteUser(string text) { _chat.AddMessage(ChatRole.User, text); }
        private void WriteAssistant(string text) { _chat.AddMessage(ChatRole.Assistant, text); }
        private void WriteError(string text) { _chat.AddMessage(ChatRole.Error, text); }
        private void WriteResult(string text) { _chat.AddMessage(ChatRole.Success, text); }

        /// <summary>Diagnostics belong in the sidebar, not the conversation.</summary>
        private void WriteSystem(string text) { _activity.AddMessage(ChatRole.System, text); }
    }
}
