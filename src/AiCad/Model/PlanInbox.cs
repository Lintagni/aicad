using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>
    /// The channel between the standalone app and the in-AutoCAD engine.
    ///
    /// The app writes a request file and asks AutoCAD to run AICADSYNC over COM;
    /// the engine draws it and writes a result file the app picks up. Files are
    /// written to a temporary name and then moved, so a reader never sees a
    /// half-written request.
    ///
    /// Shared by both sides, and deliberately free of AutoCAD types.
    /// </summary>
    public static class PlanInbox
    {
        public const string RequestSuffix = ".plan.json";
        public const string ResultSuffix = ".result.json";

        public static string Folder
        {
            get { return Path.Combine(AiCadConfig.Folder, "inbox"); }
        }

        // ------------------------------------------------------- app side

        /// <summary>Queues a plan and returns its id.</summary>
        public static string WriteRequest(JsonValue planRoot, bool askForPoint)
        {
            return WriteRequest(planRoot, askForPoint, null, false, 0, 0);
        }

        /// <summary>
        /// Queues a plan, optionally replacing a previous drawing: the engine
        /// erases eraseHandles and reuses the same insertion point.
        /// </summary>
        public static string WriteRequest(JsonValue planRoot, bool askForPoint,
                                          List<string> eraseHandles, bool hasInsertion,
                                          double insertionX, double insertionY)
        {
            Directory.CreateDirectory(Folder);

            string id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            JsonValue envelope = JsonValue.NewObject();
            envelope["id"] = JsonValue.New(id);
            envelope["askForPoint"] = JsonValue.New(askForPoint);

            if (eraseHandles != null && eraseHandles.Count > 0)
            {
                JsonValue list = JsonValue.NewArray();
                for (int i = 0; i < eraseHandles.Count; i++) list.Add(JsonValue.New(eraseHandles[i]));
                envelope["eraseHandles"] = list;
            }
            if (hasInsertion)
            {
                JsonValue point = JsonValue.NewArray();
                point.Add(JsonValue.New(insertionX));
                point.Add(JsonValue.New(insertionY));
                envelope["insertion"] = point;
            }

            envelope["plan"] = planRoot;

            WriteAtomic(Path.Combine(Folder, id + RequestSuffix), envelope.ToString());
            return id;
        }

        /// <summary>Reads and removes the result for an id, or null if not ready.</summary>
        public static JsonValue TryTakeResult(string id)
        {
            string path = Path.Combine(Folder, id + ResultSuffix);
            if (!File.Exists(path)) return null;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                JsonValue result = JsonValue.ParseLenient(text);
                File.Delete(path);
                return result;
            }
            catch (IOException)
            {
                // Still being written; the caller polls again.
                return null;
            }
        }

        // ---------------------------------------------------- engine side

        /// <summary>Queued requests, oldest first. Ids sort chronologically.</summary>
        public static List<string> PendingRequests()
        {
            List<string> files = new List<string>();
            try
            {
                if (!Directory.Exists(Folder)) return files;
                files.AddRange(Directory.GetFiles(Folder, "*" + RequestSuffix));
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
            }
            return files;
        }

        public static void WriteResult(string id, bool success, int entityCount,
                                       List<string> errors, List<string> warnings)
        {
            WriteResult(id, success, entityCount, errors, warnings, null, false, 0, 0);
        }

        public static void WriteResult(string id, bool success, int entityCount,
                                       List<string> errors, List<string> warnings,
                                       List<string> handles, bool hasInsertion,
                                       double insertionX, double insertionY)
        {
            JsonValue result = JsonValue.NewObject();
            result["id"] = JsonValue.New(id);
            result["success"] = JsonValue.New(success);
            result["entityCount"] = JsonValue.New((double)entityCount);
            result["handles"] = ToArray(handles);
            if (hasInsertion)
            {
                JsonValue point = JsonValue.NewArray();
                point.Add(JsonValue.New(insertionX));
                point.Add(JsonValue.New(insertionY));
                result["insertion"] = point;
            }
            result["errors"] = ToArray(errors);
            result["warnings"] = ToArray(warnings);

            try
            {
                Directory.CreateDirectory(Folder);
                WriteAtomic(Path.Combine(Folder, id + ResultSuffix), result.ToString());
            }
            catch (Exception)
            {
                // The app times out and says so rather than hanging.
            }
        }

        /// <summary>Deletes anything older than an hour, so the folder cannot grow forever.</summary>
        public static void PurgeStale()
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                string[] files = Directory.GetFiles(Folder);
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

        // ---------------------------------------------------------- helpers

        private static JsonValue ToArray(List<string> items)
        {
            JsonValue array = JsonValue.NewArray();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++) array.Add(JsonValue.New(items[i]));
            }
            return array;
        }

        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
