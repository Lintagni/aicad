using System;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>
    /// What the app is doing right now, for AutoCAD to show.
    ///
    /// While a plan is being generated the app is waiting on an HTTPS call and
    /// AutoCAD is genuinely idle - so the drawing sat there looking as though
    /// nothing had been asked of it, sometimes for minutes. AutoCAD cannot see
    /// the browser, so the app leaves its status here and the engine picks it up
    /// on idle.
    ///
    /// Deliberately tiny: written several times a second at worst, and read on
    /// AutoCAD's idle loop, so both sides stay cheap. Kept free of AutoCAD types
    /// so the app can write it.
    /// </summary>
    public static class DrawProgress
    {
        /// <summary>
        /// Older than this and the writer is gone - the app was closed, or it
        /// crashed - so the status is stale and AutoCAD stops showing it.
        /// </summary>
        private const int StaleAfterSeconds = 90;

        public static string Path
        {
            get { return System.IO.Path.Combine(AiCadConfig.Folder, "draw-progress.json"); }
        }

        /// <summary>Records a one-line status, or clears it when text is empty.</summary>
        public static void Set(string text)
        {
            if (string.IsNullOrEmpty(text)) { Clear(); return; }

            try
            {
                JsonValue root = JsonValue.NewObject();
                root["text"] = JsonValue.New(text);
                root["at"] = JsonValue.New(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                Directory.CreateDirectory(AiCadConfig.Folder);
                File.WriteAllText(Path, root.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Status is a courtesy; it must never break a drawing.
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception) { }
        }

        /// <summary>The current status, or null when there is nothing to show.</summary>
        public static string Read()
        {
            try
            {
                if (!File.Exists(Path)) return null;

                JsonValue root = JsonValue.ParseLenient(File.ReadAllText(Path, Encoding.UTF8));
                if (root == null) return null;

                DateTime at;
                if (!DateTime.TryParse(root.GetString("at", null), CultureInfo.InvariantCulture,
                                       System.Globalization.DateTimeStyles.AdjustToUniversal |
                                       System.Globalization.DateTimeStyles.AssumeUniversal, out at))
                    return null;

                if ((DateTime.UtcNow - at).TotalSeconds > StaleAfterSeconds) return null;

                string text = root.GetString("text", "");
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// When the status last changed, so a reader can skip parsing the file
        /// on every idle tick. Ticks are frequent; the file changes rarely.
        /// </summary>
        public static DateTime StampUtc()
        {
            try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue; }
            catch (Exception) { return DateTime.MinValue; }
        }
    }
}
