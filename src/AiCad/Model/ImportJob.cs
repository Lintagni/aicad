using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Json;

namespace AiCad.Model
{
    /// <summary>
    /// The list of drawing files waiting to be imported as examples.
    ///
    /// The app picks the files (it owns the file dialog) and the engine reads
    /// them (it owns the DWG reader), so the list has to pass between the two.
    /// Kept free of AutoCAD types for exactly that reason.
    /// </summary>
    public static class ImportJob
    {
        public static string Path
        {
            get { return System.IO.Path.Combine(AiCadConfig.Folder, "import.json"); }
        }

        public static void Write(List<ReferenceFile> files)
        {
            JsonValue root = JsonValue.NewObject();
            JsonValue list = JsonValue.NewArray();
            if (files != null)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    JsonValue entry = JsonValue.NewObject();
                    entry["path"] = JsonValue.New(files[i].Path);
                    entry["id"] = JsonValue.New(files[i].Id);
                    entry["title"] = JsonValue.New(files[i].Title);
                    list.Add(entry);
                }
            }
            root["files"] = list;

            Directory.CreateDirectory(AiCadConfig.Folder);
            File.WriteAllText(Path, root.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Reads and removes the job, so it is never imported twice.</summary>
        public static List<ReferenceFile> Take()
        {
            List<ReferenceFile> files = new List<ReferenceFile>();
            try
            {
                if (!File.Exists(Path)) return files;

                JsonValue root = JsonValue.ParseLenient(File.ReadAllText(Path, Encoding.UTF8));
                if (root != null)
                {
                    List<JsonValue> list = root.GetArray("files");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] == null || list[i].Kind != JsonKind.Object) continue;
                        ReferenceFile file = new ReferenceFile();
                        file.Path = list[i].GetString("path", null);
                        file.Id = list[i].GetString("id", null);
                        file.Title = list[i].GetString("title", "Reference drawing");
                        if (!string.IsNullOrEmpty(file.Path)) files.Add(file);
                    }
                }
                File.Delete(Path);
            }
            catch (Exception)
            {
            }
            return files;
        }
    }
}
