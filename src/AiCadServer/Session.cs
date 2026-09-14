using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Ai;
using AiCad.Config;
using AiCad.Json;
using AiCad.Model;
using AiCadApp;

namespace AiCadServer
{
    /// <summary>
    /// The conversation and its AutoCAD connection. The server is the single
    /// source of truth; the browser only renders what it reports, so a refresh
    /// or a second tab never loses state.
    /// </summary>
    /// <summary>
    /// One line in the log panel. The level drives both the colour and the
    /// ALL / CMD / ISSUES filter, so it is recorded rather than inferred from
    /// the text every time the page repaints.
    /// </summary>
    public class ActivityEntry
    {
        public DateTime When;
        public string Level;
        public string Text;

        public ActivityEntry(string level, string text)
        {
            When = DateTime.Now;
            Level = level;
            Text = text;
        }
    }

    /// <summary>One named value the user pinned before drawing.</summary>
    public class ParamField
    {
        public string Key;
        public string Label;
        public string Value;
    }

    /// <summary>
    /// What a finished generation actually put in the drawing. Built from the
    /// plan and the engine's reply, so the result panel reports facts rather
    /// than restating the request.
    /// </summary>
    public class DrawResult
    {
        public string Summary;
        public List<string> Facts = new List<string>();
        public List<ParamField> Params = new List<ParamField>();
        public List<KeyValuePair<string, int>> Objects = new List<KeyValuePair<string, int>>();
        public string Drawing;
        public bool CanUndo;
    }

    public class Session
    {
        private readonly object _gate = new object();
        private readonly StaWorker _com = new StaWorker();
        private readonly AcadBridge _acad = new AcadBridge();

        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private readonly List<ChatTurn> _history = new List<ChatTurn>();
        private readonly List<ActivityEntry> _activity = new List<ActivityEntry>();

        // What the composer was set to for the request in flight, kept so the
        // result panel can show the parameters that produced it and Regenerate
        // can repeat the request exactly.
        private string _mode = "2d";
        private List<ParamField> _fields = new List<ParamField>();
        private DrawResult _result;
        private string _lastPrompt;
        private bool _lastPickPoint;
        private bool _lastUpdatePrevious = true;

        /// <summary>Set by Cancel; checked between stages of a request.</summary>
        private volatile bool _cancel;

        /// <summary>How far the request in flight has got, for the step trace.</summary>
        private volatile int _stepIdx;
        private string[] _stepNotes = new string[StepCount];

        private List<string> _lastHandles = new List<string>();
        private bool _hasLastInsertion;
        private double _lastInsertionX;
        private double _lastInsertionY;

        private string _chatId;
        private AiCadConfig _config;

        /// <summary>
        /// One request at a time. The browser polls state while this is set,
        /// rather than holding an HTTP request open for the whole round trip.
        /// </summary>
        private volatile bool _busy;

        public bool Busy { get { return _busy; } }

        private const int MaxHistoryTurns = 6;
        private const int MaxHistoryChars = 4000;
        private const int ResultTimeoutSeconds = 150;

        public Session()
        {
            _config = AiCadConfig.Load();
            _chatId = ChatStore.NewId();
            Note("AiCad server started. Provider: " + Describe());
            Note("Settings: " + _config.LoadedFrom);
            Note("Reference drawings folder: " + ReferenceLibrary.Folder);
            TryConnect();
        }

        private string Describe()
        {
            try { return ProviderFactory.Create(_config).DisplayName; }
            catch (Exception) { return _config.Provider; }
        }

        public void Note(string text)
        {
            // Most call sites predate levels and say what they are in the text.
            string level = "info";
            if (text != null)
            {
                if (text.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)) level = "warn";
                else if (text.StartsWith("error", StringComparison.OrdinalIgnoreCase)) level = "error";
            }
            Note(level, text);
        }

        public void Note(string level, string text)
        {
            lock (_gate)
            {
                _activity.Add(new ActivityEntry(level, text));
                if (_activity.Count > 400) _activity.RemoveAt(0);
            }
        }

        // ------------------------------------------------------- step trace

        public const int StepCount = 4;

        private static readonly string[] StepLabels = new string[]
        {
            "Reading the drawing",
            "Consulting reference drawings",
            "Asking the model",
            "Writing to AutoCAD"
        };

        /// <summary>Advances the progress trace and annotates the step reached.</summary>
        private void Step(int index, string note)
        {
            lock (_gate)
            {
                _stepIdx = index;
                if (index >= 0 && index < _stepNotes.Length) _stepNotes[index] = note;
            }
        }

        /// <summary>
        /// True once the user has asked to stop. Every stage checks it, because
        /// a request that has already reached AutoCAD cannot be pulled back and
        /// the next stage is the only place to stop cleanly.
        /// </summary>
        private bool Cancelled
        {
            get { return _cancel; }
        }

        /// <summary>Set while the user has deliberately let go of AutoCAD.</summary>
        private volatile bool _detached;

        /// <summary>
        /// Releases AutoCAD and stops probing for it, so the status pill reports
        /// what the user asked for rather than reconnecting behind their back.
        /// </summary>
        public void Disconnect()
        {
            _detached = true;
            try { _com.Invoke<bool>(delegate { _acad.Release(); return true; }); }
            catch (Exception) { }
            Note("warn", "Detached from AutoCAD.");
        }

        /// <summary>Empties the log panel.</summary>
        public void ClearActivity()
        {
            lock (_gate) { _activity.Clear(); }
            Note("Log cleared.");
        }

        public bool Cancel()
        {
            if (!_busy) return false;
            _cancel = true;
            Note("warn", "Cancelling at the next stage...");
            return true;
        }

        // ---------------------------------------------------------- AutoCAD

        public bool TryConnect()
        {
            _detached = false;
            return _com.Invoke<bool>(delegate
            {
                if (_acad.IsConnected && _acad.IsAlive()) return true;
                string error;
                return _acad.TryConnect(out error);
            });
        }

        public bool Launch(out string error)
        {
            string captured = null;
            bool ok = _com.Invoke<bool>(delegate
            {
                string e;
                bool started = _acad.Launch(out e);
                captured = e;
                return started;
            });
            error = captured;
            return ok;
        }

        public string Caption
        {
            get
            {
                try { return _com.Invoke<string>(delegate { return _acad.Caption; }); }
                catch (Exception) { return null; }
            }
        }

        private DateTime _lastConnectAttempt = DateTime.MinValue;

        /// <summary>
        /// True when AutoCAD is reachable, attaching to it if it has appeared
        /// since the last check. AutoCAD is often started after AiCad, so simply
        /// reporting the existing state would leave the banner red until the
        /// user pressed Reconnect by hand.
        /// </summary>
        public bool Connected
        {
            get
            {
                // Detaching on purpose must stick, or the probe below would undo
                // it on the next poll two seconds later.
                if (_detached) return false;

                try
                {
                    return _com.Invoke<bool>(delegate
                    {
                        if (_acad.IsConnected && _acad.IsAlive()) return true;

                        // Probing costs a handful of failed COM lookups, so do it
                        // at most every couple of seconds rather than every poll.
                        if ((DateTime.UtcNow - _lastConnectAttempt).TotalSeconds < 2) return false;
                        _lastConnectAttempt = DateTime.UtcNow;

                        string error;
                        return _acad.TryConnect(out error);
                    });
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Connected is not enough: AutoCAD can be open with no drawing.</summary>
        public bool HasDrawing
        {
            get
            {
                try { return _com.Invoke<bool>(delegate { return _acad.HasDrawing; }); }
                catch (Exception) { return false; }
            }
        }

        private DateTime _lastNudge = DateTime.MinValue;

        /// <summary>
        /// Wakes the engine after AutoCAD restarts.
        ///
        /// The plug-in is demand-loaded, so a fresh AutoCAD has not loaded it and
        /// has not republished the capability list the model is given. AICADSYNC
        /// is the quietest command that triggers the load - with nothing queued
        /// it just prints one line.
        /// </summary>
        public void NudgeEngine()
        {
            if ((DateTime.UtcNow - _lastNudge).TotalSeconds < 20) return;
            _lastNudge = DateTime.UtcNow;
            try
            {
                SendCommand("_.AICADSYNC\n");
                Note("Waking the drawing engine in AutoCAD...");
            }
            catch (Exception)
            {
                // AutoCAD may be busy; the next poll tries again.
            }
        }

        public void SendCommand(string command)
        {
            _com.Invoke<bool>(delegate { _acad.SendCommand(command); return true; });
        }

        /// <summary>NETLOADs the engine that ships beside this server.</summary>
        public void LoadEngine()
        {
            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string dll = Path.Combine(dir ?? ".", "AiCad.dll");
            if (!File.Exists(dll)) throw new FileNotFoundException("AiCad.dll not found beside the server.");

            // LISP string literals need their backslashes doubled.
            SendCommand("(command \"_.NETLOAD\" \"" + dll.Replace("\\", "\\\\") + "\")\n");
            Note("Asked AutoCAD to load " + dll);
        }

        // ------------------------------------------------------------- chats

        public void NewChat()
        {
            lock (_gate)
            {
                SaveLocked();
                _chatId = ChatStore.NewId();
                _messages.Clear();
                _history.Clear();
                ForgetPreviousDrawingLocked();
                _lastPrompt = null;
                _drawingName = null;   // a fresh sheet, made when it first draws
                _lastRenderBase64 = null;
            }
        }

        public bool OpenChat(string id)
        {
            List<ChatMessage> messages;
            List<ChatTurn> history;
            if (!ChatStore.Load(id, out messages, out history)) return false;

            lock (_gate)
            {
                SaveLocked();
                _chatId = id;
                _messages.Clear();
                _messages.AddRange(messages);
                _history.Clear();
                _history.AddRange(history);
                // Handles belong to the drawing made in the other conversation.
                ForgetPreviousDrawingLocked();
                _lastPrompt = null;
                _drawingName = null;
                _lastRenderBase64 = null;
            }
            return true;
        }

        /// <summary>Deletes a saved chat by id; the open one is reset as well.</summary>
        public bool DeleteChat(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_gate)
            {
                if (id == _chatId)
                {
                    ChatStore.Delete(_chatId);
                    _chatId = ChatStore.NewId();
                    _messages.Clear();
                    _history.Clear();
                    ForgetPreviousDrawingLocked();
                    return true;
                }
            }
            // ChatStore.Delete swallows a missing file, so removal is reported as
            // done either way - the row disappears from the list regardless.
            ChatStore.Delete(id);
            return true;
        }

        public void DeleteChat()
        {
            lock (_gate)
            {
                ChatStore.Delete(_chatId);
                _chatId = ChatStore.NewId();
                _messages.Clear();
                _history.Clear();
                ForgetPreviousDrawingLocked();
            }
        }

        private void ForgetPreviousDrawingLocked()
        {
            _lastHandles = new List<string>();
            _hasLastInsertion = false;
            // The result panel describes geometry we can no longer point at.
            _result = null;
        }

        private void SaveLocked()
        {
            if (_messages.Count > 0) ChatStore.Save(_chatId, _messages, _history, _mode);
        }

        public void Save()
        {
            lock (_gate) { SaveLocked(); }
        }

        // -------------------------------------------------------- the request

        /// <summary>
        /// The whole cycle: ask the model, queue the plan, tell AutoCAD to draw
        /// it, wait for the engine's answer. Runs on a worker thread.
        /// </summary>
        public void BeginSend(string message, bool pickPoint, bool updatePrevious)
        {
            BeginSend(message, pickPoint, updatePrevious, null, null);
        }

        /// <summary>
        /// mode is "2d" or "3d" and fields are the composer's pinned parameters;
        /// both are folded into the prompt but kept out of the transcript, so the
        /// conversation reads as what the user actually typed.
        /// </summary>
        public void BeginSend(string message, bool pickPoint, bool updatePrevious,
                              string mode, List<ParamField> fields)
        {
            if (_busy) return;
            _busy = true;
            _cancel = false;

            lock (_gate)
            {
                if (!string.IsNullOrEmpty(mode)) _mode = mode == "3d" ? "3d" : "2d";
                if (fields != null) _fields = fields;
                _lastPrompt = message;
                _lastPickPoint = pickPoint;
                _lastUpdatePrevious = updatePrevious;
                _result = null;
                _stepIdx = 0;
                _stepNotes = new string[StepCount];
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { Send(message, pickPoint, updatePrevious); }
                catch (Exception ex) { Add(ChatRole.Error, "Unexpected failure: " + ex.Message); }
                finally
                {
                    _busy = false;
                    _cancel = false;
                    AiProgress.Report = null;
                    DrawProgress.Clear();
                }
            });
        }

        /// <summary>Repeats the last request with whatever the parameters now say.</summary>
        public bool Regenerate()
        {
            string prompt;
            bool pick, update;
            lock (_gate)
            {
                prompt = _lastPrompt;
                pick = _lastPickPoint;
                // A regeneration is meant to replace what it produced last time,
                // not to stack a second copy on top of the first.
                update = _lastHandles.Count > 0 ? true : _lastUpdatePrevious;
            }
            if (string.IsNullOrEmpty(prompt)) return false;

            Note("Regenerating the last request.");
            BeginSend(prompt, pick, update, null, null);
            return true;
        }

        /// <summary>
        /// Removes what the last generation drew, by handle. Erasing exactly
        /// those entities leaves anything the user drew themselves alone, which
        /// AutoCAD's own UNDO would not.
        /// </summary>
        public bool Undo(out string error)
        {
            error = null;
            List<string> handles;
            lock (_gate) { handles = new List<string>(_lastHandles); }

            if (handles.Count == 0)
            {
                error = "There is nothing from the last generation left to remove.";
                return false;
            }
            if (!Connected)
            {
                error = "Not connected to AutoCAD.";
                return false;
            }

            try
            {
                // A plan with no ops and a list of handles is an erase-only job.
                JsonValue empty = JsonValue.NewObject();
                empty["name"] = JsonValue.New("undo");
                empty["ops"] = JsonValue.NewArray();

                string id = PlanInbox.WriteRequest(empty, false, handles, false, 0, 0);
                SendCommand("_.AICADSYNC\n");
                Note("cmd", "ERASE " + handles.Count.ToString(CultureInfo.InvariantCulture) +
                            " entities from the last generation");

                JsonValue result = WaitForResult(id);
                if (result == null)
                {
                    error = "AutoCAD did not report back. Check whether it is waiting for input.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (_gate)
            {
                ForgetPreviousDrawingLocked();
                _result = null;
                _lastRenderBase64 = null;
            }
            Add(ChatRole.System, "Removed the last generation from the drawing.");
            return true;
        }

        /// <summary>
        /// Converts any reference drawings that are new or have changed since
        /// they were last seen. Conversion happens inside AutoCAD, so this waits
        /// briefly for it - but never long enough to stall a request badly.
        /// </summary>
        /// <summary>
        /// One import at a time. AICADIMPORT is a modal command: queueing a
        /// second while the first is still reading a 70 MB drawing stacks them
        /// up behind each other and freezes AutoCAD for even longer.
        /// </summary>
        private DateTime _importStartedUtc = DateTime.MinValue;

        /// <summary>
        /// A crashed or cancelled import must not wedge the claim forever, so it
        /// expires - but not so soon that a genuinely slow folder is interrupted
        /// by a second run.
        /// </summary>
        private static readonly TimeSpan ImportLease = TimeSpan.FromMinutes(30);

        private bool ImportRunning
        {
            get
            {
                if (ImportProgress.Read().Running) return true;
                return _importStartedUtc != DateTime.MinValue
                       && DateTime.UtcNow - _importStartedUtc < ImportLease;
            }
        }

        public int SyncReferences()
        {
            return SyncReferences(false);
        }

        /// <summary>
        /// Queues any new reference drawings for conversion.
        ///
        /// This used to block for up to twenty seconds so the request in flight
        /// could already use them. With a folder of large drawings that never
        /// finish inside that window, every single request paid the full twenty
        /// seconds and got nothing for it - then re-queued the same files,
        /// piling modal imports up inside AutoCAD. So drawing never waits now:
        /// the import runs in the background and the references are ready for
        /// the next request. Only an explicit Reindex waits.
        /// </summary>
        public int SyncReferences(bool wait)
        {
            List<ReferenceFile> pending;
            try
            {
                ReferenceLibrary.PruneOrphans();
                pending = ReferenceLibrary.Pending();
            }
            catch (Exception)
            {
                return 0;
            }

            if (pending.Count == 0) return 0;
            if (!Connected)
            {
                Note(pending.Count.ToString(CultureInfo.InvariantCulture) +
                     " reference drawing(s) waiting - they are read once AutoCAD is open.");
                return 0;
            }

            if (ImportRunning)
            {
                ImportProgress.Status running = ImportProgress.Read();
                if (running.Running && running.Total > 0)
                    Note("Still reading reference drawings (" +
                         (running.Done + 1).ToString(CultureInfo.InvariantCulture) + " of " +
                         running.Total.ToString(CultureInfo.InvariantCulture) + ": " +
                         running.Current + ").");
                return 0;
            }

            try
            {
                ImportJob.Write(pending);
                _importStartedUtc = DateTime.UtcNow;
                SendCommand("_.AICADIMPORT\n");
                Note("Reading " + pending.Count.ToString(CultureInfo.InvariantCulture) +
                     " reference drawing(s) in the background. A large file takes a few " +
                     "minutes; drawing is not held up by it.");
            }
            catch (Exception ex)
            {
                _importStartedUtc = DateTime.MinValue;
                Note("Could not read the reference drawings: " + ex.Message);
                return 0;
            }

            if (!wait) return 0;

            // Only the explicit Reindex waits, and only briefly, so the button
            // can report something real instead of guessing.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(500);
                try { if (ReferenceLibrary.Pending().Count == 0) break; }
                catch (Exception) { break; }
            }

            int done = pending.Count;
            try { done = pending.Count - ReferenceLibrary.Pending().Count; }
            catch (Exception) { }
            if (done > 0) Note("Learned from " + done.ToString(CultureInfo.InvariantCulture) + " drawing(s).");
            return done;
        }

        /// <summary>Saves the plan just drawn, so a good result becomes a lesson.</summary>
        public bool SaveLastAsExample(string title)
        {
            lock (_gate)
            {
                if (_lastPlanForExample == null) return false;
                ExampleStore.Save(title, _lastPromptForExample ?? title, _lastPlanForExample);
                return true;
            }
        }

        private JsonValue _lastPlanForExample;
        private string _lastPromptForExample;

        public void Send(string message, bool pickPoint, bool updatePrevious)
        {
            lock (_gate)
            {
                _config = AiCadConfig.Load();
                _messages.Add(new ChatMessage(ChatRole.User, message));
            }

            Note(Describe() + ", max tokens " +
                 _config.MaxTokens.ToString(CultureInfo.InvariantCulture));

            if (!Connected)
            {
                Add(ChatRole.Error, "Not connected to AutoCAD.");
                return;
            }

            Step(0, "reading the open drawing");
            string catalogue = ReadCapabilities();
            if (catalogue == null)
            {
                Add(ChatRole.Error, "The AutoCAD-side engine has not loaded yet. " +
                                    "Click \"Draw sample\" once to wake it.");
                return;
            }

            DrawingSnapshot snapshot;
            try { snapshot = _com.Invoke<DrawingSnapshot>(delegate { return _acad.CaptureSnapshot(); }); }
            catch (Exception) { snapshot = new DrawingSnapshot(); }

            if (StopHere()) return;
            Step(1, "indexing new files");

            // Anything newly dropped into the reference folder is queued for
            // conversion, but the request never waits for it: a folder of large
            // drawings would otherwise delay every single request by the full
            // import timeout. New files are ready for the next request.
            SyncReferences(false);

            // The user's own drawings, chosen by relevance to this request.
            List<DrawingExample> examples = ExampleStore.Select(message, 2);
            if (examples.Count > 0)
                Note("Using " + examples.Count.ToString(CultureInfo.InvariantCulture) +
                     " of your examples for style.");
            Step(1, examples.Count.ToString(CultureInfo.InvariantCulture) + " matched");

            // The mode and the pinned parameters steer the model without
            // appearing in the transcript, which shows what the user typed.
            string request = BuildRequestText(message);

            string systemPrompt = PromptBuilder.BuildSystemPrompt(
                _config, snapshot, catalogue, ExampleStore.Format(examples, 12000), request);
            List<ChatTurn> history;
            string render;
            lock (_gate)
            {
                history = new List<ChatTurn>(_history);
                render = _lastRenderBase64;
            }

            // The picture goes in as the last thing before the new request, so
            // the model reads it as "this is what you produced" rather than as
            // part of the instruction.
            if (!string.IsNullOrEmpty(render))
            {
                history.Add(new ChatTurn("user",
                    "This is a shaded render of the drawing you just produced, seen from the " +
                    "south-east. Look at it before answering. Judge what is actually there - " +
                    "parts floating unattached, parts sunk inside each other, blocky stacks " +
                    "where one smooth casting belongs, wrong proportions - and correct those " +
                    "faults in what you return next.", render));
                Note("Showing the model a render of the last drawing.");
            }

            if (StopHere()) return;
            Step(2, _config.ActiveModel);

            // The chain can spend minutes moving between models. Without this the
            // step showed whichever model was asked first and never changed, so a
            // request that had long since fallen back looked hung.
            AiResponse response;
            AiProgress.Report = delegate(string what)
            {
                Step(2, what);
                // AutoCAD has nothing to do during the call, so it shows this
                // instead of looking as though nothing was asked of it.
                DrawProgress.Set("asking " + what);
            };
            try
            {
                IAiProvider provider = ProviderFactory.Create(_config);
                response = provider.RequestPlan(systemPrompt, history, request);
            }
            finally
            {
                AiProgress.Report = null;
                DrawProgress.Set("plan received, drawing...");
            }
            if (!response.Success)
            {
                // The render is deliberately kept: the request never reached a
                // model, so the picture has not been spent and the next attempt
                // should still get to see it.
                Add(ChatRole.Error, response.Error);
                return;
            }

            // Seen now, so it will not be shown again.
            if (!string.IsNullOrEmpty(render))
                lock (_gate) { if (_lastRenderBase64 == render) _lastRenderBase64 = null; }
            if (!string.IsNullOrEmpty(response.UsedModel))
            {
                Note("Answered by " + response.UsedModel);
                Step(2, "answered by " + response.UsedModel);
            }

            DrawPlan plan = DrawPlan.FromJson(response.Plan);
            if (!string.IsNullOrEmpty(plan.Notes)) Add(ChatRole.Assistant, plan.Notes);

            if (plan.Ops.Count == 0)
            {
                Add(ChatRole.System, "No geometry was produced.");
                return;
            }

            if (StopHere()) return;
            Draw(message, response, plan, pickPoint, updatePrevious, systemPrompt, history, 0, 0);
        }

        /// <summary>How many self-correction passes one request may spend.</summary>
        private int AutoFixLimit
        {
            get { return _config != null ? _config.AutoFixAttempts : 1; }
        }

        /// <summary>Actionable faults from the last draw, for the regression check.</summary>
        private int _lastProblemCount;

        /// <summary>How bad the last draw was, for the regression check.</summary>
        private int _lastProblemWeight;

        /// <summary>
        /// How much is wrong, not merely how many complaints there are.
        ///
        /// Counting complaints alone rated "27 of 94 wire ends stop in empty
        /// space" the same as "36 of 124" - one problem either way - so a
        /// correction that made the drawing measurably worse still passed the
        /// regression check. Warnings that begin with a count are weighted by it;
        /// the rest count as one apiece.
        /// </summary>
        private static int Weigh(List<string> faults)
        {
            int weight = 0;
            for (int i = 0; i < faults.Count; i++)
            {
                string text = faults[i] ?? "";

                int digits = 0;
                while (digits < text.Length && char.IsDigit(text[digits])) digits++;

                int leading;
                if (digits > 0 &&
                    int.TryParse(text.Substring(0, digits), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out leading) && leading > 0)
                    weight += leading;
                else
                    weight += 1;
            }
            return weight;
        }

        /// <summary>
        /// Status lines that are not faults. Correcting a drawing because it
        /// replaced the previous one would loop forever.
        /// </summary>
        private static List<string> Actionable(List<string> reported)
        {
            List<string> faults = new List<string>();
            for (int i = 0; i < reported.Count; i++)
            {
                string text = reported[i] ?? "";
                if (text.StartsWith("Replaced the previous drawing",
                                    StringComparison.OrdinalIgnoreCase)) continue;
                faults.Add(text);
            }
            return faults;
        }

        /// <summary>
        /// Sends the measured faults straight back and redraws.
        ///
        /// The review can already say exactly what is wrong and where, and the
        /// engine can already replace a drawing by handle. Printing that list and
        /// waiting for the user to press Regenerate wasted both. This closes the
        /// loop: draw, measure, correct, redraw.
        ///
        /// The correction is never allowed to make things worse - if the redraw
        /// measures worse than what it replaced, the original plan goes back in.
        /// Returns true when it has taken over the drawing and reporting.
        /// </summary>
        private bool AutoCorrect(string message, AiResponse previous, DrawPlan previousPlan,
                                 string systemPrompt, List<ChatTurn> history,
                                 List<string> faults, int fixAttempt, int before, int beforeWeight)
        {
            Note("Found " + before.ToString(CultureInfo.InvariantCulture) +
                 " problem(s); correcting automatically.");
            Step(2, "correcting the drawing");

            StringBuilder ask = new StringBuilder();
            ask.Append("Your plan was drawn, and the finished drawing was then measured. ");
            ask.Append("These faults were found in the RESULT - they are facts about the ");
            ask.Append("geometry, not opinions:\n");
            for (int i = 0; i < faults.Count; i++) ask.Append("- " + faults[i] + "\n");
            ask.Append("\nReturn a corrected plan for the same request. Keep everything that ");
            ask.Append("was right and change only what these faults require. Return the ");
            ask.Append("COMPLETE plan, not a fragment and not a description of the changes.");

            List<ChatTurn> repairHistory = new List<ChatTurn>(history);
            repairHistory.Add(new ChatTurn("user", message));
            repairHistory.Add(new ChatTurn("assistant", Shorten(previous.Plan.ToString())));

            AiResponse corrected;
            AiProgress.Report = delegate(string what)
            {
                Step(2, "correcting - " + what);
                DrawProgress.Set("correcting the drawing - " + what);
            };
            try
            {
                IAiProvider provider = ProviderFactory.Create(_config);
                corrected = provider.RequestPlan(systemPrompt, repairHistory, ask.ToString());
            }
            catch (Exception ex)
            {
                Note("warn", "Could not ask for a correction: " + ex.Message);
                return false;
            }
            finally
            {
                AiProgress.Report = null;
            }

            if (!corrected.Success)
            {
                Note("warn", "Could not get a correction: " + corrected.Error);
                return false;
            }

            DrawPlan fixedPlan = DrawPlan.FromJson(corrected.Plan);
            if (fixedPlan.Ops.Count == 0)
            {
                Note("warn", "The correction had no geometry in it; keeping the drawing as it is.");
                return false;
            }

            // Replaces the drawing just made: updatePrevious, no point prompt.
            Draw(message, corrected, fixedPlan, false, true, systemPrompt, history,
                 0, fixAttempt + 1);

            int after = _lastProblemCount;
            int afterWeight = _lastProblemWeight;

            // Weight first: the same complaint about twice as much broken
            // geometry is not an improvement.
            if (afterWeight > beforeWeight)
            {
                Note("warn", "The correction came out worse (" +
                     afterWeight.ToString(CultureInfo.InvariantCulture) + " against " +
                     beforeWeight.ToString(CultureInfo.InvariantCulture) +
                     " by severity); putting the earlier drawing back.");

                // fixAttempt at the limit, so restoring cannot start another round.
                Draw(message, previous, previousPlan, false, true, systemPrompt, history,
                     0, AutoFixLimit);
                Add(ChatRole.System, "Tried to correct the drawing automatically, but the " +
                                     "result was worse, so the first version was kept.");
                return true;
            }

            if (after == 0)
                Add(ChatRole.Success, "Corrected " + before.ToString(CultureInfo.InvariantCulture) +
                                      " problem(s) automatically; the drawing now measures clean.");
            else if (afterWeight < beforeWeight)
                Add(ChatRole.System, "Corrected automatically: improved from " +
                                     beforeWeight.ToString(CultureInfo.InvariantCulture) + " to " +
                                     afterWeight.ToString(CultureInfo.InvariantCulture) +
                                     " by severity, but " + after.ToString(CultureInfo.InvariantCulture) +
                                     " problem(s) remain.");
            else
                Add(ChatRole.System, "Tried to correct the drawing automatically, but it did not " +
                                     "improve. " + after.ToString(CultureInfo.InvariantCulture) +
                                     " problem(s) remain - listed above.");
            return true;
        }

        /// <summary>
        /// Reports a cancellation once, so each stage can bail with one check
        /// instead of repeating the message.
        /// </summary>
        private bool StopHere()
        {
            if (!Cancelled) return false;
            Add(ChatRole.System, "Cancelled. Nothing was drawn.");
            return true;
        }

        /// <summary>
        /// The prompt as the model sees it: what the user typed, plus the mode
        /// and any parameters they pinned in the composer. Values given here are
        /// stated as fixed so the model uses them instead of inventing its own.
        /// </summary>
        private string BuildRequestText(string message)
        {
            string mode;
            List<ParamField> fields;
            lock (_gate)
            {
                mode = _mode;
                fields = new List<ParamField>(_fields);
            }

            StringBuilder text = new StringBuilder(message);

            text.Append(mode == "3d"
                ? "\n\nProduce 3D solid geometry."
                : "\n\nProduce a 2D drawing.");

            List<string> given = new List<string>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] == null) continue;
                string value = (fields[i].Value ?? "").Trim();
                if (value.Length == 0) continue;
                string label = (fields[i].Label ?? fields[i].Key ?? "").Trim();
                if (label.Length == 0) continue;
                given.Add(label.ToLowerInvariant() + ": " + value);
            }

            if (given.Count > 0)
            {
                text.Append("\n\nUse these exactly, rather than choosing your own:\n");
                for (int i = 0; i < given.Count; i++) text.Append("- " + given[i] + "\n");
            }

            return text.ToString();
        }

        private void Draw(string message, AiResponse response, DrawPlan plan, bool pickPoint,
                          bool updatePrevious, string systemPrompt, List<ChatTurn> history,
                          int repairAttempt, int fixAttempt)
        {
            bool replace;
            List<string> handles;
            bool hasInsertion;
            double insX, insY;
            lock (_gate)
            {
                handles = new List<string>(_lastHandles);
                hasInsertion = _hasLastInsertion;
                insX = _lastInsertionX;
                insY = _lastInsertionY;
                replace = updatePrevious && !pickPoint && handles.Count > 0;
            }

            string id;
            try
            {
                UseThisChatsDrawing();
                id = replace
                    ? PlanInbox.WriteRequest(response.Plan, false, handles, hasInsertion, insX, insY)
                    : PlanInbox.WriteRequest(response.Plan, pickPoint);
                SendCommand("_.AICADSYNC\n");
            }
            catch (Exception ex)
            {
                // The plan file is already queued at this point - only the nudge
                // failed - so this is recoverable, and saying so beats letting the
                // user think a good drawing was thrown away.
                Add(ChatRole.Error, "Could not tell AutoCAD to draw it: " + ex.Message +
                                    "\nThe drawing is queued and nothing is lost. AutoCAD is " +
                                    "most likely part-way through a command: click in its " +
                                    "window, press Esc, and send anything here to flush it - " +
                                    "or type AICADSYNC at the AutoCAD command line.");
                return;
            }

            Note("cmd", plan.Describe() + (replace ? " - replacing previous drawing" : " - sent to AutoCAD"));
            Step(3, replace ? "replacing previous" : "drawing");
            if (pickPoint) Add(ChatRole.System, "Switch to AutoCAD and pick the insertion point.");

            JsonValue result = WaitForResult(id);
            if (result == null)
            {
                Add(ChatRole.Error, "AutoCAD did not report back within " + ResultTimeoutSeconds +
                                    " seconds. Check whether it is waiting for input.");
                return;
            }

            if (result.GetBool("success", false))
            {
                int count = result.GetInt("entityCount", 0);
                Add(ChatRole.Success, "Drew " + count.ToString(CultureInfo.InvariantCulture) +
                                      " entities for \"" + plan.Name + "\".");

                // Warnings say what came out wrong. Left in the activity sidebar
                // they were seen by nobody who could act on them: not the user,
                // who is not reading a log, and not the model, whose history did
                // not carry them. Both need them - the model cannot see the
                // drawing it just made, so this is the only correction it gets.
                List<JsonValue> warnings = result.GetArray("warnings");
                List<string> reported = new List<string>();
                for (int i = 0; i < warnings.Count; i++)
                {
                    string text = warnings[i] != null ? warnings[i].Text : null;
                    if (string.IsNullOrEmpty(text)) continue;
                    Note("warning: " + text);
                    reported.Add(text);
                }
                // Only real faults are worth another round trip; "replaced the
                // previous drawing" is a status line, not something to fix.
                List<string> faults = Actionable(reported);
                _lastProblemCount = faults.Count;
                _lastProblemWeight = Weigh(faults);

                bool willCorrect = faults.Count > 0
                                && fixAttempt < AutoFixLimit
                                && !Cancelled
                                && Connected;

                // Reporting is deferred while a correction is coming: telling the
                // user about faults that are about to be fixed is just noise.
                if (faults.Count > 0 && !willCorrect)
                {
                    StringBuilder notice = new StringBuilder();
                    notice.Append(faults.Count == 1
                        ? "One problem with the drawing:"
                        : faults.Count.ToString(CultureInfo.InvariantCulture) +
                          " problems with the drawing:");
                    for (int i = 0; i < faults.Count; i++)
                        notice.Append("\n\u2022 " + faults[i]);

                    Add(ChatRole.Error, notice.ToString());
                    _pendingFeedback = notice.ToString();
                }

                lock (_gate)
                {
                    _lastHandles = new List<string>();
                    List<JsonValue> h = result.GetArray("handles");
                    for (int i = 0; i < h.Count; i++)
                        if (h[i] != null && !string.IsNullOrEmpty(h[i].Text)) _lastHandles.Add(h[i].Text);

                    JsonValue where = result["insertion"];
                    _hasLastInsertion = where != null && where.Kind == JsonKind.Array && where.Count >= 2;
                    if (_hasLastInsertion)
                    {
                        _lastInsertionX = where.At(0).Number;
                        _lastInsertionY = where.At(1).Number;
                    }

                    Remember(message, response.Plan);
                    _lastPlanForExample = response.Plan;
                    _lastPromptForExample = message;
                    _result = BuildResultLocked(plan, count, reported);
                    SaveLocked();
                }

                // Handles are recorded by now, so the corrected plan can replace
                // this drawing rather than landing beside it.
                if (willCorrect &&
                    AutoCorrect(message, response, plan, systemPrompt, history, faults,
                                fixAttempt, faults.Count, _lastProblemWeight))
                    return;

                CaptureRender(Renderer.IsThreeDimensional(response.Plan));
                return;
            }

            List<JsonValue> errors = result.GetArray("errors");

            // One repair round-trip, then report rather than draw something wrong.
            if (repairAttempt == 0 && errors.Count > 0)
            {
                Note("Plan rejected; asking for a correction.");
                List<string> lines = new List<string>();
                for (int i = 0; i < errors.Count; i++) lines.Add(errors[i].Text);

                List<ChatTurn> repairHistory = new List<ChatTurn>(history);
                repairHistory.Add(new ChatTurn("user", message));
                repairHistory.Add(new ChatTurn("assistant", Shorten(response.Plan.ToString())));

                string repair = "The plan you returned was rejected by the validator:\n" +
                                string.Join("\n", lines.ToArray()) +
                                "\nReturn a corrected plan for the same request.";

                IAiProvider provider = ProviderFactory.Create(_config);
                AiResponse second = provider.RequestPlan(systemPrompt, repairHistory, repair);
                if (second.Success)
                {
                    DrawPlan retry = DrawPlan.FromJson(second.Plan);
                    if (retry.Ops.Count > 0)
                    {
                        Draw(message, second, retry, pickPoint, updatePrevious,
                             systemPrompt, history, 1, fixAttempt);
                        return;
                    }
                }
            }

            Add(ChatRole.Error, "Nothing was drawn:");
            for (int i = 0; i < errors.Count; i++) Add(ChatRole.Error, "  " + errors[i].Text);
        }

        /// <summary>
        /// Describes what landed in the drawing: the model's own note if it left
        /// one, then counted facts taken from the plan and the engine's reply.
        /// Called with _gate held.
        /// </summary>
        private DrawResult BuildResultLocked(DrawPlan plan, int entityCount, List<string> warnings)
        {
            DrawResult result = new DrawResult();

            result.Summary = !string.IsNullOrEmpty(plan.Notes)
                ? plan.Notes
                : "Drew " + entityCount.ToString(CultureInfo.InvariantCulture) +
                  " entities for \"" + plan.Name + "\".";

            result.Drawing = _drawingName;
            result.CanUndo = _lastHandles.Count > 0;

            result.Facts.Add("objects: " + entityCount.ToString(CultureInfo.InvariantCulture));
            if (plan.Layers.Count == 1)
                result.Facts.Add("layer: " + plan.Layers[0].Name);
            else if (plan.Layers.Count > 1)
                result.Facts.Add("layers: " + plan.Layers.Count.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(plan.Units)) result.Facts.Add("units: " + plan.Units);
            result.Facts.Add(_mode == "3d" ? "mode: 3D" : "mode: 2D");
            if (warnings != null && warnings.Count > 0)
                result.Facts.Add("warnings: " + warnings.Count.ToString(CultureInfo.InvariantCulture));

            // The parameters that produced this, so the panel can show what was
            // fixed rather than guessed.
            for (int i = 0; i < _fields.Count; i++)
            {
                ParamField f = _fields[i];
                if (f == null || string.IsNullOrEmpty((f.Value ?? "").Trim())) continue;
                ParamField copy = new ParamField();
                copy.Key = f.Key;
                copy.Label = f.Label;
                copy.Value = f.Value;
                result.Params.Add(copy);
            }

            result.Objects = TallyOps(plan.Ops);
            return result;
        }

        /// <summary>
        /// Counts operations by name, nested ones included, most common first.
        /// This is what the plan asked for, which is the only per-kind breakdown
        /// available - the engine reports one total, not a total per op.
        /// </summary>
        private static List<KeyValuePair<string, int>> TallyOps(List<JsonValue> ops)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            Accumulate(ops, counts, 0);

            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> pair in counts) list.Add(pair);

            list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });

            // A long tail of one-offs tells the user nothing; the top of the list does.
            if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
            return list;
        }

        private static void Accumulate(List<JsonValue> ops, Dictionary<string, int> counts, int depth)
        {
            if (ops == null || depth > 8) return;
            for (int i = 0; i < ops.Count; i++)
            {
                JsonValue op = ops[i];
                if (op == null || op.Kind != JsonKind.Object) continue;

                string name = op.GetString("op", null);
                if (!string.IsNullOrEmpty(name))
                {
                    int current;
                    counts[name] = counts.TryGetValue(name, out current) ? current + 1 : 1;
                }
                Accumulate(op.GetArray("ops"), counts, depth + 1);
            }
        }

        private JsonValue WaitForResult(string id)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(ResultTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                JsonValue result = null;
                try { result = PlanInbox.TryTakeResult(id); }
                catch (Exception) { }
                if (result != null) return result;
                System.Threading.Thread.Sleep(250);
            }
            return null;
        }

        private static string ReadCapabilities()
        {
            try
            {
                string path = Path.Combine(AiCadConfig.Folder, "capabilities.txt");
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Makes this conversation's drawing the active one, creating it the
        /// first time. If it has since been closed, a new one is started rather
        /// than quietly drawing into whatever else is open - which is how a
        /// ladder diagram ended up beside a 3D machine.
        /// </summary>
        private void UseThisChatsDrawing()
        {
            string wanted;
            lock (_gate) { wanted = _drawingName; }

            if (!string.IsNullOrEmpty(wanted))
            {
                bool switched = _com.Invoke<bool>(delegate { return _acad.ActivateDocument(wanted); });
                if (switched) return;
                Note("The drawing for this chat is no longer open; starting a new one.");
            }

            string created = _com.Invoke<string>(delegate { return _acad.NewDocument(); });
            if (string.IsNullOrEmpty(created)) return;   // fall back to whatever is open

            lock (_gate)
            {
                _drawingName = created;
                // A new sheet means the old entity handles refer to nothing here.
                _lastHandles = new List<string>();
                _hasLastInsertion = false;
            }
            Note("Drawing into " + created + " for this chat.");
        }

        /// <summary>
        /// Asks AutoCAD for a picture of what it just drew and keeps it for the
        /// next request. Best effort throughout: a missing render costs the model
        /// its eyes for one turn, which is no worse than before, so nothing here
        /// is allowed to fail a drawing that already succeeded.
        /// </summary>
        private void CaptureRender(bool threeDimensional)
        {
            string path = null;
            try
            {
                Renderer.Purge();
                path = Renderer.NextPath();
                string command = Renderer.BuildCommand(path, threeDimensional);
                _com.Invoke<bool>(delegate { _acad.SendCommand(command); return true; });
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                string encoded = Renderer.WaitAndEncode(path);
                if (string.IsNullOrEmpty(encoded))
                {
                    Note("Could not capture a render of the drawing.");
                    return;
                }
                lock (_gate) { _lastRenderBase64 = encoded; }
                Note("Captured a render of the drawing (" +
                     (encoded.Length / 1024).ToString(CultureInfo.InvariantCulture) + " KB).");
            }
            catch (Exception)
            {
            }
        }

        private void Remember(string message, JsonValue plan)
        {
            _history.Add(new ChatTurn("user", message));
            _history.Add(new ChatTurn("assistant", Shorten(plan.ToString())));

            // Anything the engine complained about is fed back as if the user had
            // pointed it out, which is the only way the next plan can improve on
            // this one. Cleared once spent so it is not repeated forever.
            if (!string.IsNullOrEmpty(_pendingFeedback))
            {
                _history.Add(new ChatTurn("user",
                    "The drawing you just produced had these problems. Fix them in any " +
                    "further work on it, and do not repeat them:\n" + _pendingFeedback));
                _pendingFeedback = null;
            }

            while (_history.Count > MaxHistoryTurns * 2) _history.RemoveAt(0);
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= MaxHistoryChars) return s;
            return s.Substring(0, MaxHistoryChars) + " ...(truncated)";
        }

        /// <summary>
        /// The AutoCAD drawing this conversation draws into. A schematic and a
        /// 3D model have no business sharing a sheet, so each chat keeps its
        /// own, created the first time it draws.
        /// </summary>
        private string _drawingName;

        /// <summary>A PNG of the last drawing, base64, waiting to be shown to the model.</summary>
        private string _lastRenderBase64;

        /// <summary>Engine complaints about the last drawing, not yet fed back.</summary>
        private string _pendingFeedback;

        private void Add(ChatRole role, string text)
        {
            lock (_gate) { _messages.Add(new ChatMessage(role, text ?? "")); }
        }

        // ------------------------------------------------------------- state

        /// <summary>Everything the browser needs to render, in one payload.</summary>
        private DateTime _refCountedAt = DateTime.MinValue;
        private int _refCount;
        private int _refPending;

        /// <summary>
        /// Refreshes the reference tally at most every few seconds. Called with
        /// _gate held.
        /// </summary>
        private void CountReferencesLocked()
        {
            if ((DateTime.UtcNow - _refCountedAt).TotalSeconds < 5) return;
            _refCountedAt = DateTime.UtcNow;
            try
            {
                _refCount = ReferenceLibrary.DrawingFiles().Count;
                _refPending = ReferenceLibrary.Pending().Count;
            }
            catch (Exception) { }
        }

        /// <summary>Compact relative time for the history list: 4m, 3h, Yest, Mon.</summary>
        private static string Ago(DateTime utc)
        {
            TimeSpan age = DateTime.UtcNow - utc;
            if (age.TotalMinutes < 1) return "now";
            if (age.TotalMinutes < 60)
                return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
            if (age.TotalHours < 24)
                return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
            if (age.TotalDays < 2) return "Yest";
            if (age.TotalDays < 7) return utc.ToLocalTime().ToString("ddd", CultureInfo.InvariantCulture);
            return utc.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
        }

        public JsonValue StateJson(bool connected, string caption)
        {
            lock (_gate)
            {
                JsonValue root = JsonValue.NewObject();
                root["connected"] = JsonValue.New(connected);
                root["caption"] = JsonValue.New(caption ?? "");
                root["hasDrawing"] = JsonValue.New(connected && HasDrawing);
                root["provider"] = JsonValue.New(Describe());
                root["configPath"] = JsonValue.New(_config.LoadedFrom ?? "");
                root["hasKey"] = JsonValue.New(!string.IsNullOrEmpty(_config.ActiveKey));
                root["chatId"] = JsonValue.New(_chatId);
                root["canReplace"] = JsonValue.New(_lastHandles.Count > 0);
                root["busy"] = JsonValue.New(_busy);
                // Surfaced in the UI: an out-of-date engine silently ignores
                // newer instructions, which is impossible to diagnose from the
                // drawing alone.
                root["engineStale"] = JsonValue.New(!EngineInstaller.IsUpToDate());
                // A newer DLL on disk is useless until AutoCAD reloads it.
                root["engineNeedsRestart"] = JsonValue.New(EngineInstaller.NeedsAutoCadRestart());
                root["engineVersion"] = JsonValue.New(EngineInstaller.InstalledVersion());
                root["canSaveExample"] = JsonValue.New(_lastPlanForExample != null);
                root["exampleCount"] = JsonValue.New((double)ExampleStore.All().Count);
                root["referenceFolder"] = JsonValue.New(ReferenceLibrary.Folder);

                root["mode"] = JsonValue.New(_mode);
                root["drawingName"] = JsonValue.New(_drawingName ?? "");
                root["canUndo"] = JsonValue.New(_lastHandles.Count > 0);
                root["canRegenerate"] = JsonValue.New(!string.IsNullOrEmpty(_lastPrompt));
                root["lastPrompt"] = JsonValue.New(_lastPrompt ?? "");
                root["cancelling"] = JsonValue.New(_cancel);
                root["detached"] = JsonValue.New(_detached);
                root["stepIndex"] = JsonValue.New((double)_stepIdx);

                JsonValue steps = JsonValue.NewArray();
                for (int i = 0; i < StepLabels.Length; i++)
                {
                    JsonValue step = JsonValue.NewObject();
                    step["label"] = JsonValue.New(StepLabels[i]);
                    step["note"] = JsonValue.New(_stepNotes[i] ?? "");
                    steps.Add(step);
                }
                root["steps"] = steps;

                JsonValue fields = JsonValue.NewArray();
                for (int i = 0; i < _fields.Count; i++)
                {
                    if (_fields[i] == null) continue;
                    JsonValue f = JsonValue.NewObject();
                    f["key"] = JsonValue.New(_fields[i].Key ?? "");
                    f["label"] = JsonValue.New(_fields[i].Label ?? "");
                    f["value"] = JsonValue.New(_fields[i].Value ?? "");
                    fields.Add(f);
                }
                root["fields"] = fields;

                if (_result != null)
                {
                    JsonValue r = JsonValue.NewObject();
                    r["summary"] = JsonValue.New(_result.Summary ?? "");
                    r["drawing"] = JsonValue.New(_result.Drawing ?? "");

                    JsonValue facts = JsonValue.NewArray();
                    for (int i = 0; i < _result.Facts.Count; i++)
                        facts.Add(JsonValue.New(_result.Facts[i]));
                    r["facts"] = facts;

                    JsonValue objects = JsonValue.NewArray();
                    for (int i = 0; i < _result.Objects.Count; i++)
                    {
                        JsonValue o = JsonValue.NewObject();
                        o["name"] = JsonValue.New(_result.Objects[i].Key);
                        o["count"] = JsonValue.New((double)_result.Objects[i].Value);
                        objects.Add(o);
                    }
                    r["objects"] = objects;

                    JsonValue used = JsonValue.NewArray();
                    for (int i = 0; i < _result.Params.Count; i++)
                    {
                        JsonValue pv = JsonValue.NewObject();
                        pv["key"] = JsonValue.New(_result.Params[i].Key ?? "");
                        pv["label"] = JsonValue.New(_result.Params[i].Label ?? "");
                        pv["value"] = JsonValue.New(_result.Params[i].Value ?? "");
                        used.Add(pv);
                    }
                    r["params"] = used;

                    root["result"] = r;
                }

                // Counted for the sidebar footer: how much the assistant has to
                // learn your conventions from. Cached, because this walks the
                // reference folder and the browser polls this several times a
                // minute - on a folder of a hundred drawings that adds up.
                CountReferencesLocked();
                root["referenceCount"] = JsonValue.New((double)_refCount);
                root["referencePending"] = JsonValue.New((double)_refPending);

                // Reading a large drawing takes minutes and freezes AutoCAD
                // while it runs, so the UI says so rather than looking hung.
                ImportProgress.Status importing = ImportProgress.Read();
                root["indexing"] = JsonValue.New(importing.Running);
                root["indexingFile"] = JsonValue.New(importing.Current ?? "");
                root["indexingDone"] = JsonValue.New((double)importing.Done);
                root["indexingTotal"] = JsonValue.New((double)importing.Total);

                JsonValue messages = JsonValue.NewArray();
                for (int i = 0; i < _messages.Count; i++)
                {
                    JsonValue m = JsonValue.NewObject();
                    m["role"] = JsonValue.New(_messages[i].Role.ToString().ToLowerInvariant());
                    m["text"] = JsonValue.New(_messages[i].Text);
                    messages.Add(m);
                }
                root["messages"] = messages;

                JsonValue activity = JsonValue.NewArray();
                for (int i = 0; i < _activity.Count; i++)
                {
                    JsonValue a = JsonValue.NewObject();
                    a["time"] = JsonValue.New(_activity[i].When.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    a["level"] = JsonValue.New(_activity[i].Level ?? "info");
                    a["text"] = JsonValue.New(_activity[i].Text ?? "");
                    activity.Add(a);
                }
                root["activity"] = activity;

                JsonValue chats = JsonValue.NewArray();
                List<ChatSummary> saved = ChatStore.List(40);
                for (int i = 0; i < saved.Count; i++)
                {
                    JsonValue c = JsonValue.NewObject();
                    c["id"] = JsonValue.New(saved[i].Id);
                    c["title"] = JsonValue.New(saved[i].Title);
                    c["kind"] = JsonValue.New(saved[i].Mode == "3d" ? "3D" : "2D");
                    c["when"] = JsonValue.New(Ago(saved[i].Updated));
                    chats.Add(c);
                }
                root["chats"] = chats;

                return root;
            }
        }
    }
}
