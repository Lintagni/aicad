using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>One drawing file waiting to be turned into an example.</summary>
    public class ReferenceFile
    {
        public string Path;
        public string Id;
        public string Title;
    }

    /// <summary>
    /// A plain folder of reference drawings.
    ///
    /// The user drops DWG or DXF files in; AiCad converts each one to an example
    /// the first time it sees it, and consults the relevant ones whenever it
    /// draws. No panel, no import button - copying a file into a folder is a
    /// thing everyone already knows how to do.
    ///
    /// Conversion is keyed on the file's modification time, so editing a
    /// reference drawing refreshes its example automatically and untouched files
    /// are never reconverted.
    /// </summary>
    public static class ReferenceLibrary
    {
        public const string FolderName = "reference";

        /// <summary>
        /// Beside the application folder, where the user can actually find it -
        /// falling back to AppData if the install location is read-only.
        /// </summary>
        public static string Folder
        {
            get
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        // Binaries live in build\, so the folder belongs a level up
                        // next to the launcher the user double-clicks.
                        DirectoryInfo parent = Directory.GetParent(dir);
                        string preferred = parent != null
                            ? System.IO.Path.Combine(parent.FullName, FolderName)
                            : System.IO.Path.Combine(dir, FolderName);

                        Directory.CreateDirectory(preferred);
                        return preferred;
                    }
                }
                catch (Exception)
                {
                    // Read-only install location; fall through to AppData.
                }

                string fallback = System.IO.Path.Combine(AiCadConfig.Folder, FolderName);
                try { Directory.CreateDirectory(fallback); }
                catch (Exception) { }
                return fallback;
            }
        }

        public static List<string> DrawingFiles()
        {
            List<string> files = new List<string>();
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder)) return files;
                files.AddRange(Directory.GetFiles(folder, "*.dwg", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(folder, "*.dxf", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
            }
            return files;
        }

        /// <summary>
        /// A stable id per file version. Same file untouched gives the same id,
        /// so it converts once; edit the drawing and the id changes, so the
        /// example is rebuilt.
        /// </summary>
        public static string IdFor(string path)
        {
            string name = "ref";
            long stamp = 0;
            try
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path) ?? "ref";
                stamp = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception)
            {
            }

            char[] safe = name.ToCharArray();
            for (int i = 0; i < safe.Length; i++)
            {
                char c = safe[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) safe[i] = '-';
            }

            string clean = new string(safe);
            if (clean.Length > 40) clean = clean.Substring(0, 40);
            return "ref-" + clean + "-" + stamp.ToString("x", CultureInfo.InvariantCulture);
        }

        public static string TitleFor(string path)
        {
            try
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path) ?? "Reference drawing";
                name = name.Replace('_', ' ').Replace('-', ' ');
                while (name.IndexOf("  ", StringComparison.Ordinal) >= 0)
                    name = name.Replace("  ", " ");
                return name.Trim();
            }
            catch (Exception)
            {
                return "Reference drawing";
            }
        }

        /// <summary>
        /// Reference drawings that have not been converted yet.
        ///
        /// A drawing that already failed is left out. It is keyed on the same
        /// id as the example would be - file name plus modification time - so
        /// editing the drawing clears the mark and it is tried again, but an
        /// unreadable 70 MB file is not re-read on every single request.
        /// </summary>
        public static List<ReferenceFile> Pending()
        {
            List<ReferenceFile> pending = new List<ReferenceFile>();
            List<string> files = DrawingFiles();
            Dictionary<string, string> failed = Failures();

            for (int i = 0; i < files.Count; i++)
            {
                string id = IdFor(files[i]);
                if (ExampleStore.Exists(id)) continue;
                if (failed.ContainsKey(id)) continue;

                ReferenceFile file = new ReferenceFile();
                file.Path = files[i];
                file.Id = id;
                file.Title = TitleFor(files[i]);
                pending.Add(file);
            }
            return pending;
        }

        // ---------------------------------------------------------- failures

        private static string FailurePath
        {
            get { return System.IO.Path.Combine(AiCadConfig.Folder, "reference-failures.json"); }
        }

        /// <summary>Drawing id to the reason it could not be converted.</summary>
        public static Dictionary<string, string> Failures()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FailurePath)) return map;
                JsonValue root = JsonValue.ParseLenient(File.ReadAllText(FailurePath, Encoding.UTF8));
                if (root == null || root.Kind != JsonKind.Object) return map;

                if (root.Members == null) return map;
                foreach (KeyValuePair<string, JsonValue> pair in root.Members)
                {
                    if (pair.Value == null) continue;
                    map[pair.Key] = pair.Value.Text ?? "could not be read";
                }
            }
            catch (Exception)
            {
            }
            return map;
        }

        public static void MarkFailed(string id, string reason)
        {
            if (string.IsNullOrEmpty(id)) return;
            Dictionary<string, string> map = Failures();
            map[id] = string.IsNullOrEmpty(reason) ? "could not be read" : reason;
            WriteFailures(map);
        }

        /// <summary>Called after a drawing converts, so an old mark cannot linger.</summary>
        public static void ClearFailure(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Dictionary<string, string> map = Failures();
            if (!map.Remove(id)) return;
            WriteFailures(map);
        }

        /// <summary>Forgets every failure, so "Reindex" really does try them all again.</summary>
        public static void ClearFailures()
        {
            try { if (File.Exists(FailurePath)) File.Delete(FailurePath); }
            catch (Exception) { }
        }

        private static void WriteFailures(Dictionary<string, string> map)
        {
            try
            {
                JsonValue root = JsonValue.NewObject();
                foreach (KeyValuePair<string, string> pair in map)
                    root[pair.Key] = JsonValue.New(pair.Value);

                Directory.CreateDirectory(AiCadConfig.Folder);
                File.WriteAllText(FailurePath, root.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Losing the record only means a retry later, never a failure.
            }
        }

        /// <summary>
        /// Removes examples whose source drawing is gone or has been edited, so
        /// the library follows the folder rather than accumulating stale copies.
        /// </summary>
        public static int PruneOrphans()
        {
            int removed = 0;
            try
            {
                List<string> live = new List<string>();
                List<string> files = DrawingFiles();
                for (int i = 0; i < files.Count; i++) live.Add(IdFor(files[i]));

                List<DrawingExample> all = ExampleStore.All();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Shipped) continue;
                    if (!all[i].Id.StartsWith("ref-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (live.Contains(all[i].Id)) continue;
                    if (ExampleStore.Delete(all[i].Id)) removed++;
                }
            }
            catch (Exception)
            {
            }
            return removed;
        }
    }
}
