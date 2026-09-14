using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AiCad.Json;

namespace AiCad.Config
{
    /// <summary>
    /// User settings, stored per-Windows-user outside the repository so an API
    /// key never lands in source control.
    /// </summary>
    public class AiCadConfig
    {
        public string Provider;          // "claude" or "gemini"
        public string ClaudeApiKey;
        public string ClaudeModel;
        public string GeminiApiKey;
        public string GeminiModel;
        public int MaxTokens;
        public int TimeoutSeconds;

        /// <summary>
        /// Gemini charges internal reasoning against the output budget, and left
        /// unbounded it can consume all of it and truncate the plan. 0 disables
        /// thinking; a negative value lets the model decide.
        /// </summary>
        public int ThinkingBudget;

        /// <summary>Base URL of any OpenAI-compatible server (Groq, OpenRouter, Ollama, LM Studio).</summary>
        public string OpenAiBaseUrl;
        public string OpenAiModel;
        public string OpenAiApiKey;

        /// <summary>
        /// Comma-separated models tried in order when the primary one is out of
        /// quota or overloaded. Free tiers meter each model separately, so a
        /// second name is usually enough to keep working.
        /// </summary>
        /// <summary>
        /// Tried in order when the main model is out of quota or overloaded.
        ///
        /// Per provider, because the names are not interchangeable: a Gemini
        /// fallback list left over from before a switch to Claude sends
        /// "gemini-3.8-flash" to Anthropic, which can only fail.
        /// </summary>
        /// <summary>
        /// How many times a drawing may be measured and redrawn automatically
        /// before the remaining faults are simply reported. Each pass costs one
        /// more model call, so this is deliberately small; 0 turns it off.
        /// </summary>
        public int AutoFixAttempts;

        public string GeminiFallbackModels;
        public string ClaudeFallbackModels;
        public string OpenAiFallbackModels;
        public string Units;
        public string ExtraInstructions;

        public AiCadConfig()
        {
            Provider = "claude";
            ClaudeApiKey = "";
            ClaudeModel = "claude-sonnet-5";
            GeminiApiKey = "";
            GeminiModel = "gemini-3.5-flash";
            MaxTokens = 32000;
            TimeoutSeconds = 120;
            ThinkingBudget = 2048;
            OpenAiBaseUrl = "https://api.groq.com/openai/v1";
            OpenAiModel = "llama-3.3-70b-versatile";
            OpenAiApiKey = "";
            // Verified against the API on 2026-09-08. Each model meters its own
            // quota, so a chain survives both a spike (503) and an exhausted
            // free tier (429) without the user touching anything.
            AutoFixAttempts = 1;
            GeminiFallbackModels = "gemini-3.8-flash,gemini-3.7-flash,gemini-3.6-flash,gemini-flash-latest";
            // Down the capability ladder, so a busy Opus falls to Sonnet and
            // then to Haiku rather than failing the request outright.
            ClaudeFallbackModels = "claude-opus-5,claude-sonnet-5,claude-haiku-4-5";
            OpenAiFallbackModels = "";
            Units = "mm";
            ExtraInstructions = "";
        }

        /// <summary>Path the settings were actually read from; where Save writes back.</summary>
        public string LoadedFrom;

        /// <summary>Where the active API key actually came from, for display.</summary>
        public string KeySource;

        public const string ProjectFileName = "aicad.config.json";

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AiCad");
            }
        }

        /// <summary>The per-user location, used when no project config exists.</summary>
        public static string UserFilePath
        {
            get { return Path.Combine(Folder, "config.json"); }
        }

        /// <summary>
        /// Search order, first hit wins:
        ///   1. aicad.config.json beside the DLL      (build\)
        ///   2. aicad.config.json one level up        (the project root)
        ///   3. %APPDATA%\AiCad\config.json
        /// A project-local file keeps the key next to the code being worked on;
        /// the AppData copy is the fallback for an installed deployment.
        /// </summary>
        public static List<string> CandidatePaths()
        {
            List<string> paths = new List<string>();
            try
            {
                string assembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(assembly))
                {
                    string dir = Path.GetDirectoryName(assembly);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        paths.Add(Path.Combine(dir, ProjectFileName));
                        DirectoryInfo parent = Directory.GetParent(dir);
                        if (parent != null) paths.Add(Path.Combine(parent.FullName, ProjectFileName));
                    }
                }
            }
            catch (Exception)
            {
                // Assembly location is unavailable in some hosts; fall through.
            }

            paths.Add(UserFilePath);
            return paths;
        }

        public static string ResolveFilePath()
        {
            List<string> candidates = CandidatePaths();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            return UserFilePath;
        }

        /// <summary>
        /// Reads just the API keys out of a config file, ignoring everything
        /// else. Used to recover a key stored in one location when the app is
        /// running from another.
        /// </summary>
        private static void ReadKeysOnly(string path, out string claudeKey, out string geminiKey)
        {
            claudeKey = "";
            geminiKey = "";
            try
            {
                if (!File.Exists(path)) return;
                JsonValue root = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (root == null || root.Kind != JsonKind.Object) return;
                claudeKey = IgnorePlaceholder(root.GetString("claudeApiKey", ""));
                geminiKey = IgnorePlaceholder(root.GetString("geminiApiKey", ""));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Config file first, then environment variables as a fallback so a key
        /// can be supplied without writing it to disk at all.
        /// </summary>
        public static AiCadConfig Load()
        {
            AiCadConfig cfg = new AiCadConfig();
            string path = ResolveFilePath();
            cfg.LoadedFrom = path;
            try
            {
                if (File.Exists(path))
                {
                    JsonValue root = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (root != null && root.Kind == JsonKind.Object)
                    {
                        cfg.Provider = root.GetString("provider", cfg.Provider);
                        cfg.ClaudeApiKey = root.GetString("claudeApiKey", cfg.ClaudeApiKey);
                        cfg.ClaudeModel = root.GetString("claudeModel", cfg.ClaudeModel);
                        cfg.GeminiApiKey = root.GetString("geminiApiKey", cfg.GeminiApiKey);
                        cfg.GeminiModel = root.GetString("geminiModel", cfg.GeminiModel);
                        cfg.MaxTokens = root.GetInt("maxTokens", cfg.MaxTokens);
                        cfg.TimeoutSeconds = root.GetInt("timeoutSeconds", cfg.TimeoutSeconds);
                        cfg.ThinkingBudget = root.GetInt("thinkingBudget", cfg.ThinkingBudget);
                        cfg.OpenAiBaseUrl = root.GetString("openAiBaseUrl", cfg.OpenAiBaseUrl);
                        cfg.OpenAiModel = root.GetString("openAiModel", cfg.OpenAiModel);
                        cfg.OpenAiApiKey = root.GetString("openAiApiKey", cfg.OpenAiApiKey);
                        // "fallbackModels" is the old single-provider key. It
                        // only ever held Gemini names, so that is where it goes.
                        cfg.AutoFixAttempts = root.GetInt("autoFixAttempts", cfg.AutoFixAttempts);
                        cfg.GeminiFallbackModels = root.GetString("geminiFallbackModels",
                            root.GetString("fallbackModels", cfg.GeminiFallbackModels));
                        cfg.ClaudeFallbackModels = root.GetString("claudeFallbackModels", cfg.ClaudeFallbackModels);
                        cfg.OpenAiFallbackModels = root.GetString("openAiFallbackModels", cfg.OpenAiFallbackModels);
                        cfg.Units = root.GetString("units", cfg.Units);
                        cfg.ExtraInstructions = root.GetString("extraInstructions", cfg.ExtraInstructions);
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt config must not stop AutoCAD loading the plug-in.
            }

            // An untouched template must not look like a configured key.
            cfg.ClaudeApiKey = IgnorePlaceholder(cfg.ClaudeApiKey);
            cfg.GeminiApiKey = IgnorePlaceholder(cfg.GeminiApiKey);

            // The app and the in-AutoCAD engine run from different folders and
            // can therefore resolve different config files. Rather than making
            // the user enter the key twice, borrow it from any other known
            // location that has one.
            if (string.IsNullOrEmpty(cfg.ClaudeApiKey) || string.IsNullOrEmpty(cfg.GeminiApiKey))
            {
                List<string> candidates = CandidatePaths();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (string.Equals(candidates[i], path, StringComparison.OrdinalIgnoreCase)) continue;

                    string otherClaude, otherGemini;
                    ReadKeysOnly(candidates[i], out otherClaude, out otherGemini);

                    if (string.IsNullOrEmpty(cfg.ClaudeApiKey) && !string.IsNullOrEmpty(otherClaude))
                    {
                        cfg.ClaudeApiKey = otherClaude;
                        cfg.KeySource = candidates[i];
                    }
                    if (string.IsNullOrEmpty(cfg.GeminiApiKey) && !string.IsNullOrEmpty(otherGemini))
                    {
                        cfg.GeminiApiKey = otherGemini;
                        cfg.KeySource = candidates[i];
                    }
                }
            }

            if (string.IsNullOrEmpty(cfg.ClaudeApiKey))
            {
                cfg.ClaudeApiKey = ReadEnv("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(cfg.ClaudeApiKey)) cfg.KeySource = "ANTHROPIC_API_KEY";
            }
            if (string.IsNullOrEmpty(cfg.GeminiApiKey))
            {
                cfg.GeminiApiKey = ReadEnv("GEMINI_API_KEY");
                if (!string.IsNullOrEmpty(cfg.GeminiApiKey)) cfg.KeySource = "GEMINI_API_KEY";
            }

            if (string.IsNullOrEmpty(cfg.KeySource)) cfg.KeySource = path;

            if (cfg.MaxTokens < 8000) cfg.MaxTokens = 8000;
            // Two passes is already four model calls on a bad plan; past that the
            // cost stops being worth the diminishing improvement.
            if (cfg.AutoFixAttempts < 0) cfg.AutoFixAttempts = 0;
            if (cfg.AutoFixAttempts > 2) cfg.AutoFixAttempts = 2;
            if (cfg.TimeoutSeconds < 10) cfg.TimeoutSeconds = 10;
            return cfg;
        }

        /// <summary>Treats an unedited template value as "no key".</summary>
        private static string IgnorePlaceholder(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string upper = key.Trim().ToUpperInvariant();
            if (upper.StartsWith("PASTE") || upper.StartsWith("YOUR-") || upper.StartsWith("<")) return "";
            return key.Trim();
        }

        private static string ReadEnv(string name)
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(name);
                return v ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public void Save()
        {
            // Write back to the file the settings came from, so editing a
            // project-local config in the dialog does not silently move it.
            string path = string.IsNullOrEmpty(LoadedFrom) ? UserFilePath : LoadedFrom;

            // An empty key box must never erase a stored key. Only an explicit
            // new value replaces what is already on disk.
            string existingClaude, existingGemini;
            ReadKeysOnly(path, out existingClaude, out existingGemini);
            string claudeToWrite = string.IsNullOrEmpty(ClaudeApiKey) ? existingClaude : ClaudeApiKey;
            string geminiToWrite = string.IsNullOrEmpty(GeminiApiKey) ? existingGemini : GeminiApiKey;

            string existingOpenAi = "";
            try
            {
                if (File.Exists(path))
                {
                    JsonValue prior = JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (prior != null && prior.Kind == JsonKind.Object)
                        existingOpenAi = IgnorePlaceholder(prior.GetString("openAiApiKey", ""));
                }
            }
            catch (Exception) { }
            string openAiToWrite = string.IsNullOrEmpty(OpenAiApiKey) ? existingOpenAi : OpenAiApiKey;

            JsonValue root = JsonValue.NewObject();
            root["provider"] = JsonValue.New(Provider);
            root["claudeApiKey"] = JsonValue.New(claudeToWrite ?? "");
            root["claudeModel"] = JsonValue.New(ClaudeModel ?? "");
            root["geminiApiKey"] = JsonValue.New(geminiToWrite ?? "");
            root["geminiModel"] = JsonValue.New(GeminiModel ?? "");
            root["maxTokens"] = JsonValue.New((double)MaxTokens);
            root["timeoutSeconds"] = JsonValue.New((double)TimeoutSeconds);
            root["thinkingBudget"] = JsonValue.New((double)ThinkingBudget);
            root["openAiBaseUrl"] = JsonValue.New(OpenAiBaseUrl ?? "");
            root["openAiModel"] = JsonValue.New(OpenAiModel ?? "");
            root["openAiApiKey"] = JsonValue.New(openAiToWrite ?? "");
            root["autoFixAttempts"] = JsonValue.New((double)AutoFixAttempts);
            root["geminiFallbackModels"] = JsonValue.New(GeminiFallbackModels ?? "");
            root["claudeFallbackModels"] = JsonValue.New(ClaudeFallbackModels ?? "");
            root["openAiFallbackModels"] = JsonValue.New(OpenAiFallbackModels ?? "");
            root["units"] = JsonValue.New(Units ?? "mm");
            root["extraInstructions"] = JsonValue.New(ExtraInstructions ?? "");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToString(), new UTF8Encoding(false));
        }

        public string ActiveKey
        {
            get
            {
                // A local server such as Ollama needs no key at all, so an empty
                // one must not read as "not configured".
                if (IsOpenAiCompatible) return string.IsNullOrEmpty(OpenAiApiKey) ? "local" : OpenAiApiKey;
                return IsGemini ? GeminiApiKey : ClaudeApiKey;
            }
        }

        /// <summary>Any server speaking the OpenAI chat-completions API.</summary>
        public bool IsOpenAiCompatible
        {
            get
            {
                if (string.IsNullOrEmpty(Provider)) return false;
                string p = Provider.Trim().ToLowerInvariant();
                return p == "openai" || p == "groq" || p == "openrouter" ||
                       p == "ollama" || p == "local" || p == "lmstudio";
            }
        }

        /// <summary>Model actually in use, whichever provider is selected.</summary>
        public string ActiveModel
        {
            get
            {
                if (IsOpenAiCompatible) return OpenAiModel;
                return IsGemini ? GeminiModel : ClaudeModel;
            }
        }

        /// <summary>The fallback list belonging to the selected provider.</summary>
        public string FallbackModels
        {
            get
            {
                if (IsOpenAiCompatible) return OpenAiFallbackModels;
                return IsGemini ? GeminiFallbackModels : ClaudeFallbackModels;
            }
            set
            {
                if (IsOpenAiCompatible) OpenAiFallbackModels = value;
                else if (IsGemini) GeminiFallbackModels = value;
                else ClaudeFallbackModels = value;
            }
        }

        /// <summary>Primary model first, then any configured fallbacks.</summary>
        public List<string> ModelChain()
        {
            List<string> chain = new List<string>();
            string primary = ActiveModel;
            if (!string.IsNullOrEmpty(primary)) chain.Add(primary.Trim());

            if (!string.IsNullOrEmpty(FallbackModels))
            {
                string[] parts = FallbackModels.Split(new char[] { ',', ';' });
                for (int i = 0; i < parts.Length; i++)
                {
                    string name = parts[i].Trim();
                    if (name.Length == 0) continue;
                    if (chain.Contains(name)) continue;
                    chain.Add(name);
                }
            }
            return chain;
        }

        public bool IsGemini
        {
            get
            {
                if (IsOpenAiCompatible) return false;
                return !string.IsNullOrEmpty(Provider) &&
                       Provider.Trim().ToLowerInvariant().StartsWith("gem");
            }
        }
    }
}
