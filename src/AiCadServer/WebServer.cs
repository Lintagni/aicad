using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using AiCad.Config;
using AiCad.Json;
using AiCad.UI;

namespace AiCadServer
{
    /// <summary>
    /// A local-only web server for the browser UI.
    ///
    /// Bound to 127.0.0.1 so nothing off this machine can reach it, and every
    /// /api call must carry a per-run token. The token matters: without it any
    /// web page you happened to be visiting could POST to localhost and drive
    /// your AutoCAD. A custom header also forces a CORS preflight, which is
    /// refused, so browsers block cross-site calls outright.
    /// </summary>
    public class WebServer
    {
        private HttpListener _listener;
        private readonly Session _session;
        private readonly string _token;
        private readonly string _webRoot;

        public string Url { get; private set; }
        public string Token { get { return _token; } }

        private void _sessionNote(string text)
        {
            _session.Note(text);
        }

        public WebServer(Session session, int preferredPort)
        {
            _session = session;
            _token = Guid.NewGuid().ToString("N");

            string dir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            _webRoot = Path.Combine(dir, "web");

            // 127.0.0.1 (rather than + or *) needs no administrator rights.
            //
            // A fresh listener per attempt: a failed Start() leaves the instance
            // disposed, so reusing it made the retry throw ObjectDisposedException
            // instead of simply moving to the next port.
            for (int port = preferredPort; port < preferredPort + 20; port++)
            {
                string prefix = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                HttpListener candidate = new HttpListener();
                try
                {
                    candidate.Prefixes.Add(prefix);
                    candidate.Start();
                    _listener = candidate;
                    Url = prefix;
                    return;
                }
                catch (Exception)
                {
                    // Port busy, or the listener is unusable; discard and move on.
                    try { candidate.Close(); }
                    catch (Exception) { }
                }
            }
            throw new InvalidOperationException(
                "No free port between " + preferredPort.ToString(CultureInfo.InvariantCulture) +
                " and " + (preferredPort + 19).ToString(CultureInfo.InvariantCulture) +
                ". Another copy of AiCad may already be running - check the system tray.");
        }

        public void Run()
        {
            if (_listener == null) return;
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (Exception) { return; }

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(context); }
                    catch (Exception ex) { TrySendError(context, ex); }
                });
            }
        }

        public void Stop()
        {
            if (_listener == null) return;
            try { _listener.Stop(); }
            catch (Exception) { }
        }

        // --------------------------------------------------------- dispatch

        private void Handle(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath;

            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                ServeStatic(context, path);
                return;
            }

            // Every API call must prove it came from our own page.
            string supplied = context.Request.Headers["X-AiCad-Token"];
            if (supplied != _token)
            {
                context.Response.StatusCode = 403;
                WriteText(context, "text/plain", "Forbidden");
                return;
            }

            switch (path.ToLowerInvariant())
            {
                case "/api/state": ApiState(context); break;
                case "/api/send": ApiSend(context); break;
                case "/api/action": ApiAction(context); break;
                case "/api/openchat": ApiOpenChat(context); break;
                case "/api/config": ApiConfig(context); break;
                case "/api/models": ApiModels(context); break;
                case "/api/setup": ApiSetup(context); break;
                case "/api/examples": ApiExamples(context); break;
                case "/api/references": ApiReferences(context); break;
                default:
                    context.Response.StatusCode = 404;
                    WriteText(context, "text/plain", "Unknown endpoint");
                    break;
            }
        }

        private void ApiState(HttpListenerContext context)
        {
            bool connected = _session.Connected;

            // If AutoCAD is up but the engine has not loaded, wake it rather than
            // leaving the user staring at a warning they cannot act on.
            if (connected && EngineInstaller.NeedsAutoCadRestart()) _session.NudgeEngine();

            JsonValue state = _session.StateJson(connected, connected ? _session.Caption : null);
            WriteJson(context, state);
        }

        private void ApiSend(HttpListenerContext context)
        {
            JsonValue body = ReadJson(context);
            string message = body != null ? body.GetString("message", "") : "";
            if (string.IsNullOrEmpty(message.Trim()))
            {
                WriteJson(context, Ok(false, "Nothing to send."));
                return;
            }

            bool pickPoint = body.GetBool("pickPoint", false);
            bool updatePrevious = body.GetBool("updatePrevious", true);
            string mode = body.GetString("mode", null);

            // The composer's pinned parameters travel with the request; absent
            // means "leave whatever was set", empty means "clear them".
            List<ParamField> fields = null;
            JsonValue given = body["fields"];
            if (given != null && given.Kind == JsonKind.Array)
            {
                fields = new List<ParamField>();
                for (int i = 0; i < given.Count; i++)
                {
                    JsonValue f = given.At(i);
                    if (f == null || f.Kind != JsonKind.Object) continue;
                    ParamField field = new ParamField();
                    field.Key = f.GetString("key", "");
                    field.Label = f.GetString("label", "");
                    field.Value = f.GetString("value", "");
                    fields.Add(field);
                }
            }

            _session.BeginSend(message.Trim(), pickPoint, updatePrevious, mode, fields);
            WriteJson(context, Ok(true, null));
        }

        private void ApiAction(HttpListenerContext context)
        {
            JsonValue body = ReadJson(context);
            string action = body != null ? body.GetString("action", "") : "";

            try
            {
                switch (action.ToLowerInvariant())
                {
                    case "connect":
                        WriteJson(context, Ok(_session.TryConnect(), null));
                        return;
                    case "launch":
                        string error;
                        bool started = _session.Launch(out error);
                        WriteJson(context, Ok(started, started ? null : error));
                        return;
                    case "sample":
                        _session.SendCommand("_.AICADTEST\n");
                        break;
                    case "capture":
                        // Runs in AutoCAD, where the user picks the drawing to learn from.
                        _session.SendCommand("_.AICADCAPTURE\n");
                        break;
                    case "setup":
                        // Also reachable as /api/setup; the buttons use this route.
                        ApiSetup(context);
                        return;
                    case "openreferences":
                        // Opens the folder in Explorer; the user drops files in.
                        System.Diagnostics.Process.Start("explorer.exe",
                            "\"" + AiCad.Model.ReferenceLibrary.Folder + "\"");
                        break;
                    case "syncreferences":
                        WriteJson(context, Ok(true, null));
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate
                        {
                            // Asked for by hand, so this one waits: the button
                            // can then say what actually happened.
                            try { _session.SyncReferences(true); }
                            catch (Exception) { }
                        });
                        return;
                    case "retryreferences":
                        // Forgets the failures, so drawings that could not be
                        // read before are tried once more.
                        AiCad.Model.ReferenceLibrary.ClearFailures();
                        WriteJson(context, Ok(true, null));
                        System.Threading.ThreadPool.QueueUserWorkItem(delegate
                        {
                            try { _session.SyncReferences(true); }
                            catch (Exception) { }
                        });
                        return;
                    case "loadengine":
                        _session.LoadEngine();
                        break;
                    case "newchat":
                        _session.NewChat();
                        break;
                    case "deletechat":
                        _session.DeleteChat();
                        break;
                    case "deletechatid":
                        bool removed = _session.DeleteChat(body.GetString("id", ""));
                        WriteJson(context, Ok(removed, removed ? null : "That conversation was not found."));
                        return;
                    case "disconnect":
                        _session.Disconnect();
                        break;
                    case "clearlog":
                        _session.ClearActivity();
                        break;
                    case "cancel":
                        bool stopping = _session.Cancel();
                        WriteJson(context, Ok(stopping, stopping ? null : "Nothing is running."));
                        return;
                    case "undo":
                        string undoError;
                        bool undone = _session.Undo(out undoError);
                        WriteJson(context, Ok(undone, undoError));
                        return;
                    case "regenerate":
                        bool again = _session.Regenerate();
                        WriteJson(context, Ok(again, again ? null : "There is no previous request to repeat."));
                        return;
                    case "settings":
                        // The settings dialog is WinForms; showing it here keeps
                        // one editor for config rather than duplicating it in HTML.
                        ShowSettings();
                        break;
                    default:
                        WriteJson(context, Ok(false, "Unknown action."));
                        return;
                }
                WriteJson(context, Ok(true, null));
            }
            catch (Exception ex)
            {
                WriteJson(context, Ok(false, ex.Message));
            }
        }

        private void ShowSettings()
        {
            System.Threading.Thread thread = new System.Threading.Thread(delegate()
            {
                try
                {
                    using (SettingsForm form = new SettingsForm(AiCadConfig.Load()))
                    {
                        form.TopMost = true;
                        form.ShowDialog();
                    }
                }
                catch (Exception) { }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
        }

        private void ApiOpenChat(HttpListenerContext context)
        {
            JsonValue body = ReadJson(context);
            string id = body != null ? body.GetString("id", "") : "";
            WriteJson(context, Ok(_session.OpenChat(id), null));
        }

        private void ApiConfig(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "POST")
            {
                SaveConfig(context);
                return;
            }

            AiCadConfig config = AiCadConfig.Load();
            JsonValue root = JsonValue.NewObject();
            root["provider"] = JsonValue.New(config.Provider ?? "");
            root["geminiModel"] = JsonValue.New(config.GeminiModel ?? "");
            root["claudeModel"] = JsonValue.New(config.ClaudeModel ?? "");
            root["openAiModel"] = JsonValue.New(config.OpenAiModel ?? "");
            root["openAiBaseUrl"] = JsonValue.New(config.OpenAiBaseUrl ?? "");
            root["fallbackModels"] = JsonValue.New(config.FallbackModels ?? "");
            // All three, so switching provider in the dialog can swap the box
            // without a round trip.
            root["geminiFallbackModels"] = JsonValue.New(config.GeminiFallbackModels ?? "");
            root["claudeFallbackModels"] = JsonValue.New(config.ClaudeFallbackModels ?? "");
            root["openAiFallbackModels"] = JsonValue.New(config.OpenAiFallbackModels ?? "");
            root["maxTokens"] = JsonValue.New((double)config.MaxTokens);
            root["thinkingBudget"] = JsonValue.New((double)config.ThinkingBudget);
            root["units"] = JsonValue.New(config.Units ?? "mm");
            root["extraInstructions"] = JsonValue.New(config.ExtraInstructions ?? "");
            root["path"] = JsonValue.New(config.LoadedFrom ?? "");
            // Presence only - a key is never sent back to the page.
            root["hasClaudeKey"] = JsonValue.New(!string.IsNullOrEmpty(config.ClaudeApiKey));
            root["hasGeminiKey"] = JsonValue.New(!string.IsNullOrEmpty(config.GeminiApiKey));
            root["hasOpenAiKey"] = JsonValue.New(!string.IsNullOrEmpty(config.OpenAiApiKey));
            WriteJson(context, root);
        }

        private void SaveConfig(HttpListenerContext context)
        {
            JsonValue body = ReadJson(context);
            if (body == null) { WriteJson(context, Ok(false, "No settings received.")); return; }

            AiCadConfig config = AiCadConfig.Load();
            config.Provider = body.GetString("provider", config.Provider);
            config.GeminiModel = body.GetString("geminiModel", config.GeminiModel);
            config.ClaudeModel = body.GetString("claudeModel", config.ClaudeModel);
            config.OpenAiModel = body.GetString("openAiModel", config.OpenAiModel);
            config.OpenAiBaseUrl = body.GetString("openAiBaseUrl", config.OpenAiBaseUrl);
            config.FallbackModels = body.GetString("fallbackModels", config.FallbackModels);
            config.MaxTokens = body.GetInt("maxTokens", config.MaxTokens);
            config.ThinkingBudget = body.GetInt("thinkingBudget", config.ThinkingBudget);
            config.Units = body.GetString("units", config.Units);
            config.ExtraInstructions = body.GetString("extraInstructions", config.ExtraInstructions);

            // An absent or blank key field leaves the stored key alone; Save()
            // itself refuses to overwrite a key with an empty one.
            string claudeKey = body.GetString("claudeApiKey", null);
            if (!string.IsNullOrEmpty(claudeKey)) config.ClaudeApiKey = claudeKey.Trim();
            string geminiKey = body.GetString("geminiApiKey", null);
            if (!string.IsNullOrEmpty(geminiKey)) config.GeminiApiKey = geminiKey.Trim();
            string openAiKey = body.GetString("openAiApiKey", null);
            if (!string.IsNullOrEmpty(openAiKey)) config.OpenAiApiKey = openAiKey.Trim();

            try
            {
                config.Save();
                WriteJson(context, Ok(true, null));
            }
            catch (Exception ex)
            {
                WriteJson(context, Ok(false, ex.Message));
            }
        }

        /// <summary>
        /// The reference folder as the overlay shows it: every drawing, its size
        /// and date, and whether the assistant has already learned from it.
        /// </summary>
        private void ApiReferences(HttpListenerContext context)
        {
            JsonValue root = JsonValue.NewObject();
            root["folder"] = JsonValue.New(AiCad.Model.ReferenceLibrary.Folder);

            JsonValue list = JsonValue.NewArray();
            int indexed = 0;
            int failedCount = 0;
            AiCad.Model.ImportProgress.Status progress = AiCad.Model.ImportProgress.Read();

            try
            {
                Dictionary<string, string> failures = AiCad.Model.ReferenceLibrary.Failures();
                List<string> files = AiCad.Model.ReferenceLibrary.DrawingFiles();

                for (int i = 0; i < files.Count; i++)
                {
                    FileInfo info = new FileInfo(files[i]);
                    string id = AiCad.Model.ReferenceLibrary.IdFor(files[i]);
                    bool done = AiCad.Model.ExampleStore.Exists(id);

                    string state;
                    string reason = null;
                    if (done) { state = "indexed"; indexed++; }
                    else if (failures.TryGetValue(id, out reason)) { state = "failed"; failedCount++; }
                    else if (progress.Running &&
                             string.Equals(progress.Current,
                                           AiCad.Model.ReferenceLibrary.TitleFor(files[i]),
                                           StringComparison.OrdinalIgnoreCase))
                        state = "reading";
                    else state = "pending";

                    JsonValue f = JsonValue.NewObject();
                    f["name"] = JsonValue.New(info.Name);
                    f["meta"] = JsonValue.New(
                        DescribeSize(info.Length) + " · " +
                        info.LastWriteTime.ToString("d MMM", CultureInfo.InvariantCulture));
                    f["state"] = JsonValue.New(state);
                    if (state == "failed") f["reason"] = JsonValue.New(reason ?? "could not be read");
                    list.Add(f);
                }
            }
            catch (Exception ex)
            {
                root["error"] = JsonValue.New(ex.Message);
            }

            root["files"] = list;
            root["indexed"] = JsonValue.New((double)indexed);
            root["failed"] = JsonValue.New((double)failedCount);
            root["indexing"] = JsonValue.New(progress.Running);
            root["current"] = JsonValue.New(progress.Current ?? "");
            root["progressDone"] = JsonValue.New((double)progress.Done);
            root["progressTotal"] = JsonValue.New((double)progress.Total);
            root["ok"] = JsonValue.New(true);
            WriteJson(context, root);
        }

        private static string DescribeSize(long bytes)
        {
            if (bytes >= 1048576)
                return (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024)
                return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        /// <summary>Lists, saves and deletes the user's worked examples.</summary>
        private void ApiExamples(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "POST")
            {
                JsonValue body = ReadJson(context);
                string action = body != null ? body.GetString("action", "") : "";

                if (action == "delete")
                {
                    bool gone = AiCad.Model.ExampleStore.Delete(body.GetString("id", ""));
                    WriteJson(context, Ok(gone, gone ? null : "That example could not be removed."));
                    return;
                }
                if (action == "saveLast")
                {
                    string title = body.GetString("title", "");
                    if (string.IsNullOrEmpty(title.Trim()))
                    {
                        WriteJson(context, Ok(false, "Give the example a short description."));
                        return;
                    }
                    bool saved = _session.SaveLastAsExample(title.Trim());
                    WriteJson(context, Ok(saved, saved ? null : "There is no drawing to save yet."));
                    return;
                }
                WriteJson(context, Ok(false, "Unknown example action."));
                return;
            }

            JsonValue list = JsonValue.NewArray();
            System.Collections.Generic.List<AiCad.Model.DrawingExample> all =
                AiCad.Model.ExampleStore.All();
            for (int i = 0; i < all.Count; i++)
            {
                JsonValue e = JsonValue.NewObject();
                e["id"] = JsonValue.New(all[i].Id);
                e["title"] = JsonValue.New(all[i].Title);
                e["ops"] = JsonValue.New((double)all[i].OpCount);
                e["shipped"] = JsonValue.New(all[i].Shipped);
                list.Add(e);
            }

            JsonValue root = JsonValue.NewObject();
            root["ok"] = JsonValue.New(true);
            root["examples"] = list;
            root["folder"] = JsonValue.New(AiCad.Model.ExampleStore.UserFolder);
            WriteJson(context, root);
        }

        /// <summary>Model names the configured key can actually reach.</summary>
        private void ApiModels(HttpListenerContext context)
        {
            AiCadConfig config = AiCadConfig.Load();
            string error;
            JsonValue models = ModelCatalog.List(config, out error);

            JsonValue root = JsonValue.NewObject();
            root["ok"] = JsonValue.New(string.IsNullOrEmpty(error));
            if (!string.IsNullOrEmpty(error)) root["error"] = JsonValue.New(error);
            root["models"] = models;
            WriteJson(context, root);
        }

        /// <summary>
        /// Copies the engine into AutoCAD's plug-in folder, so first-time setup
        /// needs no separate install step.
        /// </summary>
        private void ApiSetup(HttpListenerContext context)
        {
            try
            {
                string installed = EngineInstaller.Install();
                WriteJson(context, Ok(true, null));
                _sessionNote("Engine installed to " + installed + ". Restart AutoCAD to load it.");
            }
            catch (Exception ex)
            {
                WriteJson(context, Ok(false, ex.Message));
            }
        }

        private static JsonValue Ok(bool ok, string error)
        {
            JsonValue root = JsonValue.NewObject();
            root["ok"] = JsonValue.New(ok);
            if (!string.IsNullOrEmpty(error)) root["error"] = JsonValue.New(error);
            return root;
        }

        // ----------------------------------------------------------- static

        private void ServeStatic(HttpListenerContext context, string path)
        {
            if (path == "/" || string.IsNullOrEmpty(path)) path = "/index.html";

            // Only ever serve from the web folder, whatever the request says.
            // One nested level is allowed so fonts can live in webonts, but the
            // segments are rebuilt from their file names, which leaves no way to
            // express "..".
            string name = Path.GetFileName(path);
            string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            string full = string.IsNullOrEmpty(folder)
                ? Path.Combine(_webRoot, name)
                : Path.Combine(_webRoot, folder, name);

            if (!File.Exists(full))
            {
                context.Response.StatusCode = 404;
                WriteText(context, "text/plain", "Not found");
                return;
            }

            string type = "text/plain; charset=utf-8";
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".html") type = "text/html; charset=utf-8";
            else if (ext == ".js") type = "application/javascript; charset=utf-8";
            else if (ext == ".css") type = "text/css; charset=utf-8";
            else if (ext == ".svg") type = "image/svg+xml";
            else if (ext == ".woff2") type = "font/woff2";
            else if (ext == ".png") type = "image/png";

            // Fonts and images are binary; reading them as UTF-8 text corrupts them.
            if (ext == ".woff2" || ext == ".png")
            {
                WriteBytes(context, type, File.ReadAllBytes(full));
                return;
            }

            string content = File.ReadAllText(full, Encoding.UTF8);
            // The page needs the token; injecting it avoids ever putting it in a URL.
            if (ext == ".html") content = content.Replace("__AICAD_TOKEN__", _token);

            WriteText(context, type, content);
        }

        // ---------------------------------------------------------- plumbing

        private static JsonValue ReadJson(HttpListenerContext context)
        {
            try
            {
                using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    return JsonValue.ParseLenient(reader.ReadToEnd());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteJson(HttpListenerContext context, JsonValue value)
        {
            WriteText(context, "application/json; charset=utf-8", value.ToString());
        }

        /// <summary>Static binary assets - fonts and images - byte for byte.</summary>
        private static void WriteBytes(HttpListenerContext context, string contentType, byte[] bytes)
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            // Fonts never change between builds, so let the browser keep them.
            context.Response.Headers["Cache-Control"] = "max-age=604800";
            try
            {
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
            }
        }

        private static void WriteText(HttpListenerContext context, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            // No caching: the UI is served from disk and edited in place.
            context.Response.Headers["Cache-Control"] = "no-store";
            try
            {
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
            }
        }

        private static void TrySendError(HttpListenerContext context, Exception ex)
        {
            try
            {
                context.Response.StatusCode = 500;
                WriteText(context, "text/plain", ex.Message);
            }
            catch (Exception)
            {
            }
        }
    }
}
