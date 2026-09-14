using System;
using System.Collections.Generic;
using AiCad.Ai;
using AiCad.Config;
using AiCad.Json;

namespace AiCadServer
{
    /// <summary>
    /// Asks the selected provider which models the key can actually reach.
    ///
    /// Hard-coding a model list goes stale fast: providers retire names without
    /// warning, and free tiers differ per account. Asking the API means the
    /// dropdown only ever offers models that will really work.
    /// </summary>
    public static class ModelCatalog
    {
        public static JsonValue List(AiCadConfig config, out string error)
        {
            error = null;
            JsonValue names = JsonValue.NewArray();

            if (config.IsGemini)
            {
                if (string.IsNullOrEmpty(config.GeminiApiKey))
                {
                    error = "Enter a Gemini API key first.";
                    return names;
                }

                Dictionary<string, string> headers = new Dictionary<string, string>();
                headers["x-goog-api-key"] = config.GeminiApiKey;

                string raw = Http.GetJson(
                    "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200",
                    headers, config.TimeoutSeconds, out error);
                if (raw == null) return names;

                JsonValue root = JsonValue.ParseLenient(raw);
                if (root == null) { error = "Unexpected reply from Google."; return names; }

                List<JsonValue> models = root.GetArray("models");
                for (int i = 0; i < models.Count; i++)
                {
                    // Only models that can answer a generateContent call are useful here.
                    List<JsonValue> methods = models[i].GetArray("supportedGenerationMethods");
                    bool usable = false;
                    for (int m = 0; m < methods.Count; m++)
                        if (methods[m].Text == "generateContent") usable = true;
                    if (!usable) continue;

                    string name = models[i].GetString("name", "");
                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(7);
                    if (name.Length > 0) names.Add(JsonValue.New(name));
                }
                return names;
            }

            if (config.IsOpenAiCompatible)
            {
                Dictionary<string, string> headers = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(config.OpenAiApiKey))
                    headers["Authorization"] = "Bearer " + config.OpenAiApiKey;

                string baseUrl = (config.OpenAiBaseUrl ?? "").TrimEnd('/');
                if (baseUrl.Length == 0) { error = "Set a server URL first."; return names; }

                string raw = Http.GetJson(baseUrl + "/models", headers, config.TimeoutSeconds, out error);
                if (raw == null) return names;

                JsonValue root = JsonValue.ParseLenient(raw);
                if (root == null) { error = "Unexpected reply from the server."; return names; }

                List<JsonValue> data = root.GetArray("data");
                for (int i = 0; i < data.Count; i++)
                {
                    string id = data[i].GetString("id", "");
                    if (id.Length > 0) names.Add(JsonValue.New(id));
                }
                return names;
            }

            // Claude
            if (string.IsNullOrEmpty(config.ClaudeApiKey))
            {
                error = "Enter an Anthropic API key first.";
                return names;
            }

            Dictionary<string, string> claudeHeaders = new Dictionary<string, string>();
            claudeHeaders["x-api-key"] = config.ClaudeApiKey;
            claudeHeaders["anthropic-version"] = "2023-06-01";

            string body = Http.GetJson("https://api.anthropic.com/v1/models?limit=100",
                                       claudeHeaders, config.TimeoutSeconds, out error);
            if (body == null) return names;

            JsonValue parsed = JsonValue.ParseLenient(body);
            if (parsed == null) { error = "Unexpected reply from Anthropic."; return names; }

            List<JsonValue> list = parsed.GetArray("data");
            for (int i = 0; i < list.Count; i++)
            {
                string id = list[i].GetString("id", "");
                if (id.Length > 0) names.Add(JsonValue.New(id));
            }
            return names;
        }
    }
}
