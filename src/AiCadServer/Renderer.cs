using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AiCad.Config;
using AiCad.Json;

namespace AiCadServer
{
    /// <summary>
    /// Captures a picture of what AutoCAD actually drew.
    ///
    /// The model writes a plan blind: it never sees the result, so a part left
    /// floating or a casting that came out as a stack of blocks looks exactly
    /// like a correct one in the JSON. Handing the render back is the difference
    /// between one blind attempt and the look-and-fix loop a person would use.
    ///
    /// This drives AutoCAD over COM rather than from inside the engine, so it
    /// needs no plug-in reinstall and works with whatever engine is loaded.
    /// </summary>
    public static class Renderer
    {
        private const int WaitMilliseconds = 12000;
        private const int PollMilliseconds = 250;

        /// <summary>Renders larger than this are not worth the upload.</summary>
        private const int MaxBytes = 4 * 1024 * 1024;

        public static string Folder
        {
            get { return Path.Combine(AiCadConfig.Folder, "renders"); }
        }

        /// <summary>
        /// The command that produces the picture. FILEDIA is turned off so
        /// PNGOUT takes the filename as an argument instead of opening a dialog,
        /// and the empty string answers its "select objects" prompt with "all".
        /// </summary>
        /// <summary>
        /// True when the plan builds solids, so the drawing wants a 3D view.
        /// Nested ops count: a group full of boxes is still a 3D drawing.
        /// </summary>
        public static bool IsThreeDimensional(JsonValue plan)
        {
            if (plan == null) return false;
            return AnySolidOp(plan.GetArray("ops"), 0);
        }

        private static readonly string[] SolidOps = new string[] {
            "box", "cylinder", "cone", "sphere", "extrude", "revolve",
            "sweep", "loft", "subtract", "union", "view3d"
        };

        private static bool AnySolidOp(List<JsonValue> ops, int depth)
        {
            if (ops == null || depth > 6) return false;

            for (int i = 0; i < ops.Count; i++)
            {
                JsonValue op = ops[i];
                if (op == null || op.Kind != JsonKind.Object) continue;

                string name = op.GetString("op", "");
                for (int j = 0; j < SolidOps.Length; j++)
                    if (string.Equals(name, SolidOps[j], StringComparison.OrdinalIgnoreCase))
                        return true;

                if (AnySolidOp(op.GetArray("ops"), depth + 1)) return true;
            }
            return false;
        }

        public static string BuildCommand(string pngPath, bool threeDimensional)
        {
            string lisp = pngPath.Replace('\\', '/');

            // PNGOUT asks which objects to capture, and an unanswered prompt
            // leaves AutoCAD mid-command: not quiescent, refusing every COM call
            // afterwards, with no dialog to show why. So the sequence cancels
            // anything already pending before it starts, and cancels again at the
            // end, rather than trusting each command to have consumed its input.
            // Nothing here may leave a prompt open - a stuck prompt blocks every
            // drawing that follows it.
            return "(progn" +
                   " (while (> (getvar \"CMDACTIVE\") 0) (command))" +
                   " (setvar \"FILEDIA\" 0)" +
                   // A schematic seen from a corner in a shaded style is just a
                   // wrong drawing. Only solids get the 3D treatment; anything
                   // flat is looked at square-on, as a drawing is meant to be.
                   (threeDimensional
                       ? " (command \"_.-VIEW\" \"_SE\")" +
                         " (command \"_.ZOOM\" \"_E\")" +
                         " (command \"_.VSCURRENT\" \"_Shadedwithedges\")"
                       : " (command \"_.-VIEW\" \"_TOP\")" +
                         " (command \"_.ZOOM\" \"_E\")" +
                         " (command \"_.VSCURRENT\" \"_2dwireframe\")") +
                   " (command \"_.PNGOUT\" \"" + lisp + "\" \"\")" +
                   " (while (> (getvar \"CMDACTIVE\") 0) (command))" +
                   " (setvar \"FILEDIA\" 1) (princ))\n";
        }

        public static string NextPath()
        {
            Directory.CreateDirectory(Folder);
            string name = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
                                                   CultureInfo.InvariantCulture) + ".png";
            return Path.Combine(Folder, name);
        }

        /// <summary>
        /// Waits for AutoCAD to finish writing the file, then reads it. The file
        /// appears before it is complete, so its size has to settle first.
        /// </summary>
        public static string WaitAndEncode(string pngPath)
        {
            long stable = -1;
            int waited = 0;

            while (waited < WaitMilliseconds)
            {
                System.Threading.Thread.Sleep(PollMilliseconds);
                waited += PollMilliseconds;

                if (!File.Exists(pngPath)) continue;

                long size;
                try { size = new FileInfo(pngPath).Length; }
                catch (Exception) { continue; }

                if (size > 0 && size == stable) return Encode(pngPath);
                stable = size;
            }
            return null;
        }

        private static string Encode(string pngPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(pngPath);
                if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;
                return Convert.ToBase64String(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Keeps the folder from growing without limit.</summary>
        public static void Purge()
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-6);
                string[] files = Directory.GetFiles(Folder, "*.png");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(files[i]) < cutoff) File.Delete(files[i]);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
