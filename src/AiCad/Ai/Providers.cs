using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Ai
{
    /// <summary>
    /// Live progress from inside a provider call.
    ///
    /// A request that walks a fallback chain can spend minutes before anything
    /// is drawn, and the UI used to show only the model that was asked FIRST -
    /// so a run that had quietly moved on to a third model looked frozen on the
    /// first. The provider says what it is actually doing; the session decides
    /// how to show it.
    /// </summary>
    public static class AiProgress
    {
        public static Action<string> Report;

        internal static void Say(string what)
        {
            Action<string> sink = Report;
            if (sink == null) return;
            try { sink(what); }
            catch (Exception) { }
        }
    }

    /// <summary>Shared retry pacing for a service that reports itself busy.</summary>
    internal static class Backoff
    {
        internal const int Attempts = 3;
        internal const int PauseMs = 2500;
    }

    public class ChatTurn
    {
        public string Role;      // "user" or "assistant"
        public string Content;

        /// <summary>
        /// A PNG of the drawing this turn is talking about, base64 encoded, or
        /// null. Without it the model is writing coordinates for something it
        /// has never seen; with it, the next attempt can correct what actually
        /// came out rather than what was intended.
        /// </summary>
        public string ImagePngBase64;

        public ChatTurn(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public ChatTurn(string role, string content, string imagePngBase64)
        {
            Role = role;
            Content = content;
            ImagePngBase64 = imagePngBase64;
        }

        public bool HasImage
        {
            get { return !string.IsNullOrEmpty(ImagePngBase64); }
        }
    }

    public class AiResponse
    {
        public bool Success;
        public string Error;
        public string RawText;
        public JsonValue Plan;

        /// <summary>True for quota / overload failures, which another model may survive.</summary>
        public bool Retryable;

        /// <summary>
        /// HTTP status behind a failure, or 0. A 404 means the model name is
        /// gone and the next one should be tried at once; 429 and 5xx mean the
        /// service is busy, and the same model may well answer in a moment.
        /// </summary>
        public int Status;

        /// <summary>A busy service, as opposed to a name that no longer exists.</summary>
        public bool IsOverloaded
        {
            get { return Status == 429 || Status == 500 || Status == 502 || Status == 503; }
        }

        /// <summary>The model that actually answered, which may be a fallback.</summary>
        public string UsedModel;

        public static AiResponse Fail(string message)
        {
            AiResponse r = new AiResponse();
            r.Success = false;
            r.Error = message;
            return r;
        }
    }

    public interface IAiProvider
    {
        string DisplayName { get; }
        AiResponse RequestPlan(string systemPrompt, List<ChatTurn> history, string userMessage);
    }

    /// <summary>Blocking HTTP POST. Callers run it on a worker thread.</summary>
    internal static class Http
    {
        public static string PostJson(string url, string body, IDictionary<string, string> headers,
                                      int timeoutSeconds, out string error)
        {
            int ignored;
            return PostJson(url, body, headers, timeoutSeconds, out error, out ignored);
        }

        /// <summary>
        /// POSTs and hands back each line of the reply as it arrives.
        ///
        /// The blocking form above waits for the whole answer, which for a large
        /// plan means minutes with nothing to show. Server-sent events let the
        /// caller watch the plan being written and report progress while it
        /// happens. Returns the complete body, exactly as PostJson would.
        /// </summary>
        public static string PostJsonStreamed(string url, string body,
                                              IDictionary<string, string> headers,
                                              int timeoutSeconds, Action<string> onLine,
                                              out string error, out int status)
        {
            error = null;
            status = 0;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = timeoutSeconds * 1000;
                // The gap between chunks, not the whole download: a stream that
                // keeps producing must not be cut off for taking a long time.
                request.ReadWriteTimeout = timeoutSeconds * 1000;
                request.UserAgent = "AiCad-AutoCAD-Plugin/1.0";
                request.AllowReadStreamBuffering = false;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> h in headers)
                        request.Headers[h.Key] = h.Value;
                }

                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (Stream s = request.GetRequestStream()) s.Write(payload, 0, payload.Length);

                StringBuilder whole = new StringBuilder();
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        whole.Append(line).Append('\n');
                        if (onLine != null)
                        {
                            try { onLine(line); }
                            catch (Exception) { }   // a reporting fault must not kill the stream
                        }
                    }
                }
                return whole.ToString();
            }
            catch (WebException wex)
            {
                string detail = "";
                if (wex.Response != null)
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch (Exception) { }
                }
                HttpWebResponse http = wex.Response as HttpWebResponse;
                if (http != null) status = (int)http.StatusCode;
                string code = http != null ? ((int)http.StatusCode).ToString(CultureInfo.InvariantCulture) : "network";
                error = "HTTP " + code + ": " + Summarize(detail, wex.Message);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static string PostJson(string url, string body, IDictionary<string, string> headers,
                                      int timeoutSeconds, out string error, out int status)
        {
            error = null;
            status = 0;
            try
            {
                // .NET Framework 4.7 still defaults to SSL3/TLS1.0 in some hosts.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = timeoutSeconds * 1000;
                request.ReadWriteTimeout = timeoutSeconds * 1000;
                request.UserAgent = "AiCad-AutoCAD-Plugin/1.0";
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> h in headers)
                        request.Headers[h.Key] = h.Value;
                }

                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (Stream s = request.GetRequestStream()) s.Write(payload, 0, payload.Length);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                string detail = "";
                if (wex.Response != null)
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch (Exception) { }
                }
                HttpWebResponse http = wex.Response as HttpWebResponse;
                if (http != null) status = (int)http.StatusCode;
                string code = http != null ? ((int)http.StatusCode).ToString(CultureInfo.InvariantCulture) : "network";
                error = "HTTP " + code + ": " + Summarize(detail, wex.Message);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Blocking HTTP GET, used to list the models a key can reach.</summary>
        public static string GetJson(string url, IDictionary<string, string> headers,
                                     int timeoutSeconds, out string error)
        {
            error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = timeoutSeconds * 1000;
                request.UserAgent = "AiCad-AutoCAD-Plugin/1.0";
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> h in headers)
                        request.Headers[h.Key] = h.Value;
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException wex)
            {
                string detail = "";
                if (wex.Response != null)
                {
                    try
                    {
                        using (StreamReader reader = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch (Exception) { }
                }
                error = Summarize(detail, wex.Message);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Pulls the human-readable message out of a provider error body.</summary>
        private static string Summarize(string body, string fallback)
        {
            if (string.IsNullOrEmpty(body)) return fallback;
            JsonValue root = JsonValue.ParseLenient(body);
            if (root != null)
            {
                JsonValue err = root["error"];
                if (err != null)
                {
                    string message = err.Kind == JsonKind.Object ? err.GetString("message", null) : err.Text;
                    if (!string.IsNullOrEmpty(message)) return message;
                }
            }
            return body.Length > 400 ? body.Substring(0, 400) + "..." : body;
        }
    }

    // ---------------------------------------------------------------- Claude

    public class ClaudeProvider : IAiProvider
    {
        private readonly AiCadConfig _config;

        public ClaudeProvider(AiCadConfig config)
        {
            _config = config;
        }

        public string DisplayName
        {
            get { return "Claude (" + _config.ClaudeModel + ")"; }
        }

        /// <summary>
        /// The plan is requested as a forced tool call, which is the most
        /// reliable way to get schema-shaped JSON back rather than prose.
        /// </summary>
        private const string PlanSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"name\":{\"type\":\"string\",\"description\":\"short name for the plan\"}," +
            "\"notes\":{\"type\":\"string\",\"description\":\"assumptions and key sizes, for the user\"}," +
            "\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
            "\"name\":{\"type\":\"string\"},\"color\":{\"type\":\"integer\"}," +
            "\"linetype\":{\"type\":\"string\"},\"lineWeight\":{\"type\":\"number\"}}," +
            "\"required\":[\"name\"]}}," +
            "\"ops\":{\"type\":\"array\",\"description\":\"drawing operations in execution order\"," +
            "\"items\":{\"type\":\"object\",\"properties\":{\"op\":{\"type\":\"string\"}}," +
            "\"required\":[\"op\"]}}}," +
            "\"required\":[\"name\",\"ops\"]}";

        public AiResponse RequestPlan(string systemPrompt, List<ChatTurn> history, string userMessage)
        {
            if (string.IsNullOrEmpty(_config.ClaudeApiKey))
                return AiResponse.Fail("No Anthropic API key. Run AICADCONFIG, or set ANTHROPIC_API_KEY.");

            StringBuilder body = new StringBuilder();
            body.Append("{\"model\":").Append(JsonValue.Quote(_config.ClaudeModel));
            body.Append(",\"max_tokens\":").Append(_config.MaxTokens.ToString(CultureInfo.InvariantCulture));
            body.Append(",\"system\":").Append(JsonValue.Quote(systemPrompt));
            body.Append(",\"tools\":[{\"name\":\"draw\",\"description\":");
            body.Append(JsonValue.Quote("Return the drawing plan to execute in AutoCAD."));
            body.Append(",\"input_schema\":").Append(PlanSchema).Append("}]");
            body.Append(",\"tool_choice\":{\"type\":\"tool\",\"name\":\"draw\"}");
            body.Append(",\"messages\":[");

            bool first = true;
            if (history != null)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    if (!first) body.Append(',');
                    first = false;
                    body.Append("{\"role\":").Append(JsonValue.Quote(history[i].Role));
                    if (history[i].HasImage)
                    {
                        body.Append(",\"content\":[{\"type\":\"text\",\"text\":")
                            .Append(JsonValue.Quote(history[i].Content)).Append('}');
                        body.Append(",{\"type\":\"image\",\"source\":{\"type\":\"base64\"")
                            .Append(",\"media_type\":\"image/png\",\"data\":")
                            .Append(JsonValue.Quote(history[i].ImagePngBase64)).Append("}}]}");
                    }
                    else
                    {
                        body.Append(",\"content\":")
                            .Append(JsonValue.Quote(history[i].Content)).Append('}');
                    }
                }
            }
            if (!first) body.Append(',');
            body.Append("{\"role\":\"user\",\"content\":").Append(JsonValue.Quote(userMessage)).Append('}');
            body.Append("]}");

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["x-api-key"] = _config.ClaudeApiKey;
            headers["anthropic-version"] = "2023-06-01";

            string error;
            string raw = Http.PostJson("https://api.anthropic.com/v1/messages",
                                       body.ToString(), headers, _config.TimeoutSeconds, out error);
            if (raw == null) return AiResponse.Fail(error);

            AiResponse result = new AiResponse();
            result.RawText = raw;

            JsonValue root = JsonValue.ParseLenient(raw);
            if (root == null) return AiResponse.Fail("Could not parse the Claude response.");

            List<JsonValue> content = root.GetArray("content");
            string textFallback = null;
            for (int i = 0; i < content.Count; i++)
            {
                JsonValue block = content[i];
                if (block == null || block.Kind != JsonKind.Object) continue;
                string type = block.GetString("type", "");
                if (type == "tool_use")
                {
                    JsonValue input = block["input"];
                    if (input != null && input.Kind == JsonKind.Object)
                    {
                        result.Success = true;
                        result.Plan = input;
                        return result;
                    }
                }
                else if (type == "text" && textFallback == null)
                {
                    textFallback = block.GetString("text", null);
                }
            }

            // The model answered in prose instead of calling the tool.
            if (root.GetString("stop_reason", "") == "max_tokens")
                return AiResponse.Fail("The reply was cut off before the plan was complete. " +
                                       "Raise \"Max tokens\" in Settings and try again.");

            if (!string.IsNullOrEmpty(textFallback))
            {
                JsonValue plan = JsonValue.ParseLenient(textFallback, "ops");
                if (plan != null)
                {
                    result.Success = true;
                    result.Plan = plan;
                    return result;
                }
                return AiResponse.Fail("Claude replied without a drawing plan: " + textFallback);
            }

            return AiResponse.Fail("Claude returned no usable content.");
        }
    }

    // ---------------------------------------------------------------- Gemini

    public class GeminiProvider : IAiProvider
    {
        private readonly AiCadConfig _config;

        public GeminiProvider(AiCadConfig config)
        {
            _config = config;
        }

        public string DisplayName
        {
            get { return "Gemini (" + _config.GeminiModel + ")"; }
        }

        /// <summary>
        /// Tries the configured model, then each fallback. Free tiers meter
        /// every model separately, so a second name usually keeps things moving
        /// when the first is exhausted.
        /// </summary>
        public AiResponse RequestPlan(string systemPrompt, List<ChatTurn> history, string userMessage)
        {
            if (string.IsNullOrEmpty(_config.GeminiApiKey))
                return AiResponse.Fail("No Gemini API key. Run AICADCONFIG, or set GEMINI_API_KEY.");

            List<string> models = _config.ModelChain();
            AiResponse last = null;

            for (int i = 0; i < models.Count; i++)
            {
                // "High demand" is temporary by definition, and the whole chain
                // can be busy at once during a spike. Walking straight through
                // every model in under a second only turns one failure into
                // three, so each model gets a short second chance.
                AiResponse r = null;
                for (int attempt = 0; attempt < Backoff.Attempts; attempt++)
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(Backoff.PauseMs * attempt);

                    AiProgress.Say(attempt == 0
                        ? models[i]
                        : models[i] + " (retry " + attempt.ToString(CultureInfo.InvariantCulture) + ")");

                    r = Attempt(models[i], systemPrompt, history, userMessage, true);

                    // Some models reject thinkingConfig outright; retry without it.
                    if (!r.Success && r.Retryable == false && r.Error != null &&
                        r.Error.IndexOf("invalid argument", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        r = Attempt(models[i], systemPrompt, history, userMessage, false);
                    }

                    if (r.Success) break;
                    if (!r.IsOverloaded) break;
                    AiProgress.Say(models[i] + " is busy");
                }

                if (r.Success)
                {
                    r.UsedModel = models[i];
                    return r;
                }

                last = r;
                if (!r.Retryable) return r;
                if (i + 1 < models.Count) AiProgress.Say("falling back from " + models[i]);
            }

            if (last == null) return AiResponse.Fail("No Gemini model was configured.");
            if (models.Count > 1)
                last.Error = "All " + models.Count.ToString(CultureInfo.InvariantCulture) +
                             " configured models failed. Last error: " + last.Error;
            return last;
        }

        private AiResponse Attempt(string model, string systemPrompt, List<ChatTurn> history,
                                   string userMessage, bool includeThinking)
        {
            return Attempt(model, systemPrompt, history, userMessage, includeThinking, true);
        }

        /// <summary>
        /// One request to one model.
        ///
        /// streamed asks for server-sent events so the plan can be reported as
        /// it is written. That is an optimisation, and it is not allowed to cost
        /// anything: if a streamed reply comes back unusable - cut short, or
        /// frames that did not parse - the same request is made again without
        /// streaming, which is exactly what it did before.
        /// </summary>
        private AiResponse Attempt(string model, string systemPrompt, List<ChatTurn> history,
                                   string userMessage, bool includeThinking, bool streamed)
        {
            StringBuilder body = new StringBuilder();
            body.Append("{\"systemInstruction\":{\"parts\":[{\"text\":")
                .Append(JsonValue.Quote(systemPrompt)).Append("}]}");
            body.Append(",\"generationConfig\":{\"responseMimeType\":\"application/json\"");
            body.Append(",\"temperature\":0.2,\"maxOutputTokens\":")
                .Append(_config.MaxTokens.ToString(CultureInfo.InvariantCulture));
            // Bound the reasoning, otherwise it can eat the whole output budget
            // and the plan comes back truncated.
            if (includeThinking && _config.ThinkingBudget >= 0)
            {
                body.Append(",\"thinkingConfig\":{\"thinkingBudget\":")
                    .Append(_config.ThinkingBudget.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            body.Append('}');
            body.Append(",\"contents\":[");

            bool first = true;
            if (history != null)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    if (!first) body.Append(',');
                    first = false;
                    string role = history[i].Role == "assistant" ? "model" : "user";
                    body.Append("{\"role\":").Append(JsonValue.Quote(role));
                    body.Append(",\"parts\":[{\"text\":")
                        .Append(JsonValue.Quote(history[i].Content)).Append('}');
                    if (history[i].HasImage)
                    {
                        body.Append(",{\"inline_data\":{\"mime_type\":\"image/png\",\"data\":")
                            .Append(JsonValue.Quote(history[i].ImagePngBase64)).Append("}}");
                    }
                    body.Append("]}");
                }
            }
            if (!first) body.Append(',');
            body.Append("{\"role\":\"user\",\"parts\":[{\"text\":")
                .Append(JsonValue.Quote(userMessage)).Append("}]}");
            body.Append("]}");

            // Key goes in a header, never the query string.
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["x-goog-api-key"] = _config.GeminiApiKey;

            // Streamed, the plan can be reported while it is being written
            // rather than after. The reply is server-sent events: one "data:"
            // line per chunk, each a partial candidate.
            string url = "https://generativelanguage.googleapis.com/v1beta/models/" +
                         Uri.EscapeDataString(model) +
                         (streamed ? ":streamGenerateContent?alt=sse" : ":generateContent");

            string error;
            int status;
            PlanStream stream = new PlanStream();
            int lastSaid = 0;
            string raw;

            if (streamed)
            {
                raw = Http.PostJsonStreamed(url, body.ToString(), headers,
                    _config.TimeoutSeconds,
                    delegate(string line)
                    {
                        string chunk = SseText(line);
                        if (chunk == null) return;

                        int ops = stream.Append(chunk);
                        // Only when the count moves, so a long plan costs a handful
                        // of updates rather than one per chunk.
                        if (ops == lastSaid) return;
                        lastSaid = ops;
                        AiProgress.Say(model + " - writing the plan, " +
                                       ops.ToString(CultureInfo.InvariantCulture) + " operations so far");
                    },
                    out error, out status);
            }
            else
            {
                raw = Http.PostJson(url, body.ToString(), headers, _config.TimeoutSeconds,
                                    out error, out status);
            }
            if (raw == null)
            {
                AiResponse failure = AiResponse.Fail("[" + model + "] " + error);
                // 404 too: model names are retired without notice, and moving on
                // to the next one beats failing the whole request over a name.
                failure.Status = status;
                failure.Retryable = status == 429 || status == 503 || status == 500 ||
                                    status == 404;
                return failure;
            }

            AiResponse result = new AiResponse();
            result.RawText = raw;

            // The streamed reply is a sequence of frames, not one JSON document,
            // so the answer is the text the stream accumulated. The last frame
            // carries the finish reason.
            StringBuilder text = new StringBuilder(streamed ? stream.Text : "");
            string finish = streamed ? LastFinishReason(raw) : "";

            if (text.Length == 0)
            {
                // Nothing came through as SSE: fall back to reading the body as
                // a single document, which is what a non-streaming reply is.
                JsonValue whole = JsonValue.ParseLenient(raw);
                if (whole == null) return AiResponse.Fail("Could not parse the Gemini response.");

                List<JsonValue> candidates = whole.GetArray("candidates");
                if (candidates.Count == 0)
                    return AiResponse.Fail("Gemini returned no candidates (check the model name and key).");

                JsonValue candidate = candidates[0];
                if (string.IsNullOrEmpty(finish)) finish = candidate.GetString("finishReason", "");
                JsonValue contentNode = candidate["content"];
                if (contentNode != null)
                {
                    List<JsonValue> parts = contentNode.GetArray("parts");
                    for (int i = 0; i < parts.Count; i++) text.Append(parts[i].GetString("text", ""));
                }
            }

            if (text.Length == 0)
                return AiResponse.Fail("Gemini returned empty content" +
                                       (string.IsNullOrEmpty(finish) ? "." : " (finishReason: " + finish + ")."));

            // Gemini 3.x counts its internal reasoning against maxOutputTokens, so
            // a plan can be cut off well before the visible output looks large.
            if (finish == "MAX_TOKENS")
                return AiResponse.Fail("The reply was cut off before the plan was complete. " +
                                       "Raise \"Max tokens\" in Settings (32000 is a good value) and try again.");

            JsonValue plan = JsonValue.ParseLenient(text.ToString(), "ops");
            if (plan == null)
            {
                // A streamed reply that did not survive is not a model failure
                // until the plain request has failed too - the stream can be cut
                // short, or arrive in frames this did not read.
                if (streamed)
                {
                    AiProgress.Say(model + " - stream incomplete, asking again");
                    return Attempt(model, systemPrompt, history, userMessage, includeThinking, false);
                }

                return AiResponse.Fail("Gemini did not return a usable drawing plan. " +
                                       Explain(text.ToString(), finish, "gemini"));
            }

            result.Success = true;
            result.Plan = plan;
            return result;
        }

        /// <summary>
        /// The text carried by one server-sent-events line, or null when the
        /// line is a comment, a blank separator, or carries no text part.
        /// </summary>
        private static string SseText(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

            string payload = line.Substring(5).Trim();
            if (payload.Length == 0 || payload == "[DONE]") return null;

            JsonValue frame = JsonValue.ParseLenient(payload);
            if (frame == null) return null;

            List<JsonValue> candidates = frame.GetArray("candidates");
            if (candidates.Count == 0) return null;

            JsonValue content = candidates[0]["content"];
            if (content == null) return null;

            StringBuilder text = new StringBuilder();
            List<JsonValue> parts = content.GetArray("parts");
            for (int i = 0; i < parts.Count; i++) text.Append(parts[i].GetString("text", ""));

            return text.Length == 0 ? null : text.ToString();
        }

        /// <summary>The finish reason from the last frame that stated one.</summary>
        private static string LastFinishReason(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            string found = "";
            string[] lines = raw.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("data:", StringComparison.Ordinal)) continue;
                JsonValue frame = JsonValue.ParseLenient(lines[i].Substring(5).Trim());
                if (frame == null) continue;

                List<JsonValue> candidates = frame.GetArray("candidates");
                if (candidates.Count == 0) continue;

                string reason = candidates[0].GetString("finishReason", "");
                if (!string.IsNullOrEmpty(reason)) found = reason;
            }
            return found;
        }

        internal static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        /// <summary>
        /// Says why a reply could not be used, and keeps the whole thing on disk.
        ///
        /// The old message pasted the first 300 characters, which for a plan that
        /// starts correctly and fails somewhere in the middle showed only the part
        /// that was fine - there was no way to tell a truncated reply from one
        /// missing "ops" from one wrapped in prose. The reply is saved so the next
        /// occurrence can actually be read.
        /// </summary>
        internal static string Explain(string text, string finishReason, string provider)
        {
            text = text ?? "";

            StringBuilder why = new StringBuilder();
            bool looksCut = LooksTruncated(text);

            if (looksCut)
                why.Append("The JSON stops part-way through, so the reply was cut off. ")
                   .Append("Raise \"Max tokens\" in Settings, or ask for a simpler drawing.");
            else if (text.IndexOf("\"ops\"", StringComparison.OrdinalIgnoreCase) < 0)
                why.Append("It contains no \"ops\" list, so there was nothing to draw.");
            else
                why.Append("The JSON could not be parsed.");

            why.Append(" (");
            why.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(" characters");
            if (!string.IsNullOrEmpty(finishReason)) why.Append(", finish reason ").Append(finishReason);
            why.Append(')');

            string saved = SaveReply(text, provider);
            if (saved != null) why.Append(" The full reply is in ").Append(saved).Append('.');

            return why.ToString();
        }

        /// <summary>
        /// Unclosed braces or brackets mean the text ended mid-structure. Counted
        /// outside string literals so a brace inside a label does not fool it.
        /// </summary>
        private static bool LooksTruncated(string text)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return depth > 0 || inString;
        }

        private static string SaveReply(string text, string provider)
        {
            try
            {
                string folder = AiCadConfig.Folder;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "last-bad-reply.txt");

                StringBuilder body = new StringBuilder();
                body.Append("provider: ").Append(provider).Append('\n');
                body.Append("when: ").Append(DateTime.Now.ToString("u", CultureInfo.InvariantCulture));
                body.Append("\n\n").Append(text);

                File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                // Diagnostics must never be the reason a request fails.
                return null;
            }
        }
    }

    // ------------------------------------------------- OpenAI-compatible

    /// <summary>
    /// Speaks the OpenAI chat-completions API, which nearly every other server
    /// implements too: Groq, OpenRouter, Together, and local runtimes such as
    /// Ollama and LM Studio. One implementation, many back ends.
    /// </summary>
    public class OpenAiCompatibleProvider : IAiProvider
    {
        private readonly AiCadConfig _config;

        public OpenAiCompatibleProvider(AiCadConfig config)
        {
            _config = config;
        }

        public string DisplayName
        {
            get { return Host + " (" + _config.OpenAiModel + ")"; }
        }

        private string Host
        {
            get
            {
                string url = _config.OpenAiBaseUrl ?? "";
                if (url.IndexOf("groq", StringComparison.OrdinalIgnoreCase) >= 0) return "Groq";
                if (url.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0) return "OpenRouter";
                if (url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    url.IndexOf("127.0.0.1", StringComparison.Ordinal) >= 0) return "Local";
                if (url.IndexOf("openai.com", StringComparison.OrdinalIgnoreCase) >= 0) return "OpenAI";
                return "OpenAI-compatible";
            }
        }

        public AiResponse RequestPlan(string systemPrompt, List<ChatTurn> history, string userMessage)
        {
            if (string.IsNullOrEmpty(_config.OpenAiBaseUrl))
                return AiResponse.Fail("No server URL configured for this provider.");

            List<string> models = _config.ModelChain();
            AiResponse last = null;

            for (int i = 0; i < models.Count; i++)
            {
                AiResponse r = null;
                for (int attempt = 0; attempt < Backoff.Attempts; attempt++)
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(Backoff.PauseMs * attempt);
                    r = Attempt(models[i], systemPrompt, history, userMessage);
                    if (r.Success) break;
                    if (!r.IsOverloaded) break;
                    AiProgress.Say(models[i] + " is busy");
                }

                if (r.Success) { r.UsedModel = models[i]; return r; }
                last = r;
                if (!r.Retryable) return r;
                if (i + 1 < models.Count) AiProgress.Say("falling back from " + models[i]);
            }

            if (last == null) return AiResponse.Fail("No model was configured.");
            return last;
        }

        private AiResponse Attempt(string model, string systemPrompt, List<ChatTurn> history,
                                   string userMessage)
        {
            StringBuilder body = new StringBuilder();
            body.Append("{\"model\":").Append(JsonValue.Quote(model));
            body.Append(",\"temperature\":0.2");
            body.Append(",\"max_tokens\":").Append(_config.MaxTokens.ToString(CultureInfo.InvariantCulture));
            // Widely supported hint; servers that ignore it still return JSON
            // because the system prompt demands it.
            body.Append(",\"response_format\":{\"type\":\"json_object\"}");
            body.Append(",\"messages\":[");
            body.Append("{\"role\":\"system\",\"content\":").Append(JsonValue.Quote(systemPrompt)).Append('}');

            if (history != null)
            {
                for (int i = 0; i < history.Count; i++)
                {
                    body.Append(",{\"role\":").Append(JsonValue.Quote(history[i].Role));
                    body.Append(",\"content\":").Append(JsonValue.Quote(history[i].Content)).Append('}');
                }
            }
            body.Append(",{\"role\":\"user\",\"content\":")
                .Append(JsonValue.Quote(userMessage)).Append('}');
            body.Append("]}");

            Dictionary<string, string> headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_config.OpenAiApiKey))
                headers["Authorization"] = "Bearer " + _config.OpenAiApiKey;

            string baseUrl = _config.OpenAiBaseUrl.TrimEnd('/');
            string url = baseUrl + "/chat/completions";

            string error;
            int status;
            string raw = Http.PostJson(url, body.ToString(), headers, _config.TimeoutSeconds,
                                       out error, out status);
            if (raw == null)
            {
                AiResponse failure = AiResponse.Fail("[" + model + "] " + error);
                failure.Status = status;
                failure.Retryable = status == 429 || status == 503 || status == 500 ||
                                    status == 502 || status == 404;
                return failure;
            }

            JsonValue root = JsonValue.ParseLenient(raw);
            if (root == null) return AiResponse.Fail("Could not parse the response from " + Host + ".");

            List<JsonValue> choices = root.GetArray("choices");
            if (choices.Count == 0)
                return AiResponse.Fail(Host + " returned no choices (check the model name).");

            JsonValue message = choices[0]["message"];
            string content = message != null ? message.GetString("content", "") : "";
            string finish = choices[0].GetString("finish_reason", "");

            if (string.IsNullOrEmpty(content))
                return AiResponse.Fail(Host + " returned empty content" +
                                       (string.IsNullOrEmpty(finish) ? "." : " (finish_reason: " + finish + ")."));

            if (finish == "length")
                return AiResponse.Fail("The reply was cut off before the plan was complete. " +
                                       "Raise \"Max tokens\" in Settings and try again.");

            JsonValue plan = JsonValue.ParseLenient(content, "ops");
            if (plan == null)
                return AiResponse.Fail(Host + " did not return a usable drawing plan. " +
                                       GeminiProvider.Explain(content, finish, Host));

            AiResponse result = new AiResponse();
            result.RawText = raw;
            result.Success = true;
            result.Plan = plan;
            return result;
        }
    }

    public static class ProviderFactory
    {
        public static IAiProvider Create(AiCadConfig config)
        {
            if (config.IsOpenAiCompatible) return new OpenAiCompatibleProvider(config);
            if (config.IsGemini) return new GeminiProvider(config);
            return new ClaudeProvider(config);
        }
    }
}
