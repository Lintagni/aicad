using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using AiCad.Config;
using AiCad.Json;
using AiCad.Model;

namespace AiCad.Execution
{
    /// <summary>
    /// Imports whole drawing files as examples, without opening them in the
    /// editor. Each file is read into a side database, its model space is
    /// converted to a plan, and the result is stored in the example library.
    ///
    /// This is how a user turns a folder of past work into house style in one
    /// go, rather than capturing drawings one selection at a time.
    /// </summary>
    public static class DrawingImport
    {
        public class Outcome
        {
            public string File;
            public string Title;
            public bool Ok;
            public int Captured;
            public int Skipped;
            public string Error;
        }

        public static Outcome ImportFile(string path)
        {
            return ImportFile(path, null, null);
        }

        /// <summary>
        /// Imports one drawing under a given id, so a reference file that has
        /// not changed is never converted twice.
        /// </summary>
        public static Outcome ImportFile(string path, string id, string title)
        {
            Outcome outcome = new Outcome();
            outcome.File = path;
            outcome.Title = string.IsNullOrEmpty(title) ? TitleFrom(path) : title;

            try
            {
                if (!File.Exists(path))
                {
                    outcome.Error = "File not found.";
                    return outcome;
                }

                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".dwg" && extension != ".dxf")
                {
                    outcome.Error = "Only DWG and DXF files can be read as drawings.";
                    return outcome;
                }

                // A side database: the file is never opened in the editor, so
                // importing a folder does not disturb what the user is working on.
                using (Database source = new Database(false, true))
                {
                    if (extension == ".dxf") source.DxfIn(path, null);
                    else source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);

                    using (Transaction tr = source.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord space =
                            (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                        List<ObjectId> ids = new List<ObjectId>();
                        foreach (ObjectId entityId in space) ids.Add(entityId);

                        if (ids.Count == 0)
                        {
                            tr.Commit();
                            outcome.Error = "Model space is empty.";
                            return outcome;
                        }

                        int captured, skipped;
                        JsonValue plan = DrawingCapture.Capture(source, tr, ids.ToArray(),
                                                                outcome.Title, out captured, out skipped);
                        tr.Commit();

                        if (captured == 0)
                        {
                            outcome.Error = "Nothing in it could be converted.";
                            return outcome;
                        }

                        ExampleStore.Save(id, outcome.Title, outcome.Title, plan);
                        outcome.Ok = true;
                        outcome.Captured = captured;
                        outcome.Skipped = skipped;
                    }
                }
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
            }
            return outcome;
        }

        /// <summary>
        /// A readable description from the file name, since that is usually how
        /// drawings are labelled: "SLD_250kVA-Panel.dwg" reads as a request.
        /// </summary>
        private static string TitleFrom(string path)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(path) ?? "Drawing";
                name = name.Replace('_', ' ').Replace('-', ' ');
                while (name.IndexOf("  ", StringComparison.Ordinal) >= 0)
                    name = name.Replace("  ", " ");
                return name.Trim();
            }
            catch (Exception)
            {
                return "Drawing";
            }
        }
    }
}
