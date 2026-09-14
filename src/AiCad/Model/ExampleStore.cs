using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>One worked example: what was asked for, and the plan that satisfied it.</summary>
    public class DrawingExample
    {
        public string Id;
        public string Title;
        public string Prompt;
        public JsonValue Plan;
        public bool Shipped;      // came with the download rather than added locally
        public DateTime Created;

        public int OpCount
        {
            get { return Plan == null ? 0 : DrawPlan.CountOps(Plan.GetArray("ops")); }
        }
    }

    /// <summary>
    /// The user's own drawings, kept as prompt/plan pairs and fed back to the
    /// model as worked examples.
    ///
    /// This is deliberately not fine-tuning. A handful of examples changes the
    /// output noticeably, costs nothing, works with every provider, and can be
    /// edited or deleted by hand. The stored shape is also exactly what a
    /// fine-tuning dataset needs later, so nothing is wasted if the collection
    /// grows large enough to be worth training on.
    /// </summary>
    public static class ExampleStore
    {
        /// <summary>Examples the user captured or saved.</summary>
        public static string UserFolder
        {
            get { return Path.Combine(AiCadConfig.Folder, "examples"); }
        }

        /// <summary>
        /// Examples shipped in the download, so a new user starts with a house
        /// style rather than a blank slate.
        /// </summary>
        public static string ShippedFolder
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (string.IsNullOrEmpty(dir)) return null;

                    // Beside the binary, or one level up next to the sources.
                    string beside = Path.Combine(dir, "examples");
                    if (Directory.Exists(beside)) return beside;

                    DirectoryInfo parent = Directory.GetParent(dir);
                    if (parent != null)
                    {
                        string above = Path.Combine(parent.FullName, "examples");
                        if (Directory.Exists(above)) return above;
                    }
                }
                catch (Exception)
                {
                }
                return null;
            }
        }

        public static string NewId()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                   "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static string Save(string title, string prompt, JsonValue plan)
        {
            return Save(NewId(), title, prompt, plan);
        }

        /// <summary>True when an example with this id already exists.</summary>
        public static bool Exists(string id)
        {
            try
            {
                return !string.IsNullOrEmpty(id) &&
                       File.Exists(Path.Combine(UserFolder, id + ".json"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Saves under a caller-chosen id. Reference drawings use an id derived
        /// from the file and its timestamp, so re-scanning the folder does not
        /// convert the same drawing twice.
        /// </summary>
        public static string Save(string id, string title, string prompt, JsonValue plan)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (string.IsNullOrEmpty(id)) id = NewId();

            Directory.CreateDirectory(UserFolder);

            JsonValue root = JsonValue.NewObject();
            root["id"] = JsonValue.New(id);
            root["title"] = JsonValue.New(title ?? "Example");
            root["prompt"] = JsonValue.New(prompt ?? title ?? "");
            root["created"] = JsonValue.New(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            root["plan"] = plan;

            File.WriteAllText(Path.Combine(UserFolder, id + ".json"),
                              root.ToString(), new UTF8Encoding(false));
            return id;
        }

        public static List<DrawingExample> All()
        {
            List<DrawingExample> examples = new List<DrawingExample>();
            AddFrom(ShippedFolder, true, examples);
            AddFrom(UserFolder, false, examples);
            return examples;
        }

        private static void AddFrom(string folder, bool shipped, List<DrawingExample> into)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                string[] files = Directory.GetFiles(folder, "*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        JsonValue root = JsonValue.ParseLenient(File.ReadAllText(files[i], Encoding.UTF8));
                        if (root == null) continue;

                        DrawingExample e = new DrawingExample();
                        e.Id = root.GetString("id", Path.GetFileNameWithoutExtension(files[i]));
                        e.Title = root.GetString("title", "Example");
                        e.Prompt = root.GetString("prompt", e.Title);
                        e.Plan = root["plan"];
                        e.Shipped = shipped;
                        e.Created = File.GetLastWriteTimeUtc(files[i]);
                        if (e.Plan != null) into.Add(e);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        public static bool Delete(string id)
        {
            try
            {
                string path = Path.Combine(UserFolder, id + ".json");
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Picks the examples most likely to help with this request, by word
        /// overlap with their titles and prompts. Crude, but it reliably puts an
        /// SLD example in front of an SLD request, and examples are expensive
        /// enough in tokens that sending them all is not an option.
        /// </summary>
        public static List<DrawingExample> Select(string request, int max)
        {
            List<DrawingExample> all = All();
            if (all.Count == 0 || max <= 0) return new List<DrawingExample>();

            List<string> wanted = Words(request);
            List<DrawingExample> scored = new List<DrawingExample>(all);

            Dictionary<string, int> scores = new Dictionary<string, int>();
            for (int i = 0; i < scored.Count; i++)
            {
                List<string> have = Words(scored[i].Title + " " + scored[i].Prompt);
                int score = 0;
                for (int w = 0; w < wanted.Count; w++)
                    if (have.Contains(wanted[w])) score++;
                scores[scored[i].Id] = score;
            }

            scored.Sort(delegate(DrawingExample a, DrawingExample b)
            {
                int byScore = scores[b.Id].CompareTo(scores[a.Id]);
                if (byScore != 0) return byScore;
                // Newest first among equals, so recent corrections win.
                return b.Created.CompareTo(a.Created);
            });

            List<DrawingExample> chosen = new List<DrawingExample>();
            for (int i = 0; i < scored.Count && chosen.Count < max; i++)
            {
                // An example with nothing in common is noise, not guidance.
                if (scores[scored[i].Id] == 0 && chosen.Count > 0) break;
                chosen.Add(scored[i]);
            }
            return chosen;
        }

        private static readonly string[] Noise =
        {
            "the", "and", "with", "for", "a", "an", "of", "to", "in", "on", "at",
            "create", "draw", "make", "add", "please", "then", "that", "this", "is", "are"
        };

        private static List<string> Words(string text)
        {
            List<string> words = new List<string>();
            if (string.IsNullOrEmpty(text)) return words;

            StringBuilder token = new StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? char.ToLowerInvariant(text[i]) : ' ';
                bool part = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (part) { token.Append(c); continue; }

                if (token.Length > 2)
                {
                    string word = token.ToString();
                    if (Array.IndexOf(Noise, word) < 0 && !words.Contains(word)) words.Add(word);
                }
                token.Length = 0;
            }
            return words;
        }

        /// <summary>Renders the chosen examples for the system prompt.</summary>
        public static string Format(List<DrawingExample> examples, int maxCharsEach)
        {
            if (examples == null || examples.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < examples.Count; i++)
            {
                string plan = examples[i].Plan.ToString();
                if (plan.Length > maxCharsEach) continue;   // too big to be worth the tokens

                sb.AppendLine("Example " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                              " - request: " + examples[i].Prompt);
                sb.AppendLine(plan);
                sb.AppendLine();
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
    }
}
