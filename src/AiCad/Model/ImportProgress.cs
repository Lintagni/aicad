using System;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>
    /// How far the reference import has got, written by the engine and read by
    /// the app.
    ///
    /// Reading a 70 MB drawing takes minutes, and without this the user watches
    /// a spinner with no idea whether anything is happening, which file is being
    /// read, or how many are left. AutoCAD is busy for the whole command, so the
    /// progress cannot be asked for - it has to be left behind as it goes.
    ///
    /// Kept free of AutoCAD types so both sides can use it.
    /// </summary>
    public static class ImportProgress
    {
        /// <summary>
        /// Older than this and the writer is gone - AutoCAD was closed, or the
        /// command was cancelled - so the progress is stale and ignored.
        /// </summary>
        private const int StaleAfterMinutes = 30;

        public static string Path
        {
            get { return System.IO.Path.Combine(AiCadConfig.Folder, "import-progress.json"); }
        }

        public class Status
        {
            public bool Running;
            public string Current;
            public int Done;
            public int Total;
        }

        public static void Report(string title, int index, int total)
        {
            try
            {
                JsonValue root = JsonValue.NewObject();
                root["current"] = JsonValue.New(title ?? "");
                root["done"] = JsonValue.New((double)index);
                root["total"] = JsonValue.New((double)total);
                root["at"] = JsonValue.New(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                Directory.CreateDirectory(AiCadConfig.Folder);
                File.WriteAllText(Path, root.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Progress is a courtesy; never let it break the import.
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception) { }
        }

        public static Status Read()
        {
            Status status = new Status();
            try
            {
                if (!File.Exists(Path)) return status;

                JsonValue root = JsonValue.ParseLenient(File.ReadAllText(Path, Encoding.UTF8));
                if (root == null) return status;

                DateTime at;
                string stamp = root.GetString("at", null);
                if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AdjustToUniversal |
                                       System.Globalization.DateTimeStyles.AssumeUniversal, out at))
                    return status;

                if ((DateTime.UtcNow - at).TotalMinutes > StaleAfterMinutes)
                {
                    Clear();
                    return status;
                }

                status.Running = true;
                status.Current = root.GetString("current", "");
                status.Done = root.GetInt("done", 0);
                status.Total = root.GetInt("total", 0);
            }
            catch (Exception)
            {
            }
            return status;
        }
    }
}
