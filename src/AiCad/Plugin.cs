using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AiCad.Config;
using AiCad.Execution;
using AiCad.Json;
using AiCad.Model;
using AiCad.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AiCad.Plugin))]
[assembly: CommandClass(typeof(AiCad.Plugin))]

namespace AiCad
{
    /// <summary>Owns the docked palette so only one ever exists.</summary>
    public static class PaletteHost
    {
        private static PaletteSet _paletteSet;
        private static AiPaletteControl _control;

        // Stable id, so AutoCAD remembers the docked position between sessions.
        private static readonly Guid PaletteId = new Guid("6C2F0B4E-2E7A-4C1B-9E52-3A5B8D91F7A1");

        public static AiPaletteControl Control { get { return _control; } }

        public static void Show()
        {
            if (_paletteSet == null)
            {
                _control = new AiPaletteControl();
                _paletteSet = new PaletteSet("AiCad", PaletteId);
                _paletteSet.Style = PaletteSetStyles.ShowAutoHideButton |
                                    PaletteSetStyles.ShowCloseButton |
                                    PaletteSetStyles.ShowPropertiesMenu;
                _paletteSet.MinimumSize = new Size(300, 300);
                _paletteSet.Add("Assistant", _control);
                _paletteSet.Dock = DockSides.Left;
            }
            _paletteSet.Visible = true;
        }

        /// <summary>Echoes command-side results back into the chat transcript.</summary>
        public static void Report(string message, bool isError)
        {
            if (_control == null) return;
            if (isError) _control.WriteError(message);
            else _control.WriteResult(message);
        }
    }

    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            // The ribbon and palette cannot be built during load, so defer until
            // AutoCAD is idle and its UI is fully constructed.
            Autodesk.AutoCAD.ApplicationServices.Application.Idle += OnFirstIdle;
        }

        // ------------------------------------------------- progress watching

        private static DateTime _statusStampUtc = DateTime.MinValue;
        private static DateTime _lastPollUtc = DateTime.MinValue;
        private static string _lastStatus;

        /// <summary>
        /// Reports what the app is doing, on AutoCAD's idle loop.
        ///
        /// Generating a plan happens in the app, over the network, and can take
        /// minutes when the provider is busy - all of it invisible from inside
        /// AutoCAD, which simply sat there looking untouched. This surfaces it
        /// where the user is actually looking.
        ///
        /// AutoCAD is NOT locked or blocked while this runs: it has no work to
        /// do during the wait, and taking the drawing away from the user for
        /// minutes to prove the app is busy would be a poor trade. Only changes
        /// are printed, so a long wait costs a handful of lines, not a stream.
        /// </summary>
        private static void OnIdleWatchProgress(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPollUtc).TotalMilliseconds < 700) return;
            _lastPollUtc = now;

            try
            {
                // The file's timestamp is far cheaper than parsing it, and it
                // changes rarely compared with how often idle fires.
                DateTime stamp = DrawProgress.StampUtc();
                if (stamp == _statusStampUtc) return;
                _statusStampUtc = stamp;

                string status = DrawProgress.Read();
                if (status == _lastStatus) return;
                _lastStatus = status;

                if (string.IsNullOrEmpty(status)) return;

                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\nAiCad: " + status + "\n");
                PaletteHost.Report("AiCad: " + status, false);
            }
            catch (System.Exception)
            {
                // Never let a status update disturb AutoCAD.
            }
        }

        private static void OnFirstIdle(object sender, EventArgs e)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnFirstIdle;
            Autodesk.AutoCAD.ApplicationServices.Application.Idle += OnIdleWatchProgress;

            // Publish what this engine can draw, so the standalone app's prompt
            // always matches the installed version.
            try
            {
                Capabilities.PublishCatalogue();
                PlanInbox.PurgeStale();
            }
            catch (System.Exception)
            {
            }

            try
            {
                RibbonBuilder.Install();
            }
            catch (System.Exception)
            {
                // A missing ribbon must not stop the palette from working.
            }

            try
            {
                PaletteHost.Show();
            }
            catch (System.Exception)
            {
            }

            try
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage(
                        "\nAiCad loaded. Use the AiCad ribbon tab, or the commands " +
                        "AICAD, AICADCONFIG, AICADTEST, AICADOPS.\n");
                }
            }
            catch (System.Exception)
            {
                // Never let a banner failure block the load.
            }
        }

        public void Terminate()
        {
        }

        [CommandMethod("AICAD", CommandFlags.Modal)]
        public void ShowPalette()
        {
            PaletteHost.Show();
        }

        [CommandMethod("AICADCONFIG", CommandFlags.Modal)]
        public void ShowConfig()
        {
            AiCadConfig config = AiCadConfig.Load();
            using (SettingsForm form = new SettingsForm(config))
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
            }
        }

        /// <summary>
        /// Draws the plan the palette queued. It runs as a real command so the
        /// editor can prompt for an insertion point and so the whole plan lands
        /// in a single undo step.
        /// </summary>
        [CommandMethod("AICADDRAW", CommandFlags.Modal)]
        public void DrawPendingPlan()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            bool askForPoint;
            DrawPlan plan = PlanQueue.Take(out askForPoint);
            if (plan == null)
            {
                ed.WriteMessage("\nNo AI plan is waiting. Open the assistant with AICAD.\n");
                return;
            }

            Point3d insertion = Placement.NextFreeSpot(doc);
            if (askForPoint)
            {
                PromptPointOptions options = new PromptPointOptions(
                    "\nSpecify insertion point or press Enter for the drawing origin: ");
                options.AllowNone = true;
                PromptPointResult result = ed.GetPoint(options);
                if (result.Status == PromptStatus.OK) insertion = result.Value;
                else if (result.Status != PromptStatus.None)
                {
                    ed.WriteMessage("\nCancelled - nothing was drawn.\n");
                    PaletteHost.Report("Cancelled - nothing was drawn.", true);
                    return;
                }
            }

            Execute(doc, plan, insertion);
        }

        /// <summary>
        /// Draws a built-in sample so the whole pipeline can be verified before
        /// any API key exists.
        /// </summary>
        [CommandMethod("AICADTEST", CommandFlags.Modal)]
        public void DrawSample()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            JsonValue root = JsonValue.ParseLenient(SamplePlan.Json);
            if (root == null)
            {
                ed.WriteMessage("\nThe built-in sample plan failed to parse.\n");
                return;
            }

            // No prompt: the sample is placed automatically in free space and
            // the view moves to it, so it simply appears.
            DrawPlan plan = DrawPlan.FromJson(root);
            Execute(doc, plan, Placement.NextFreeSpot(doc));
        }

        /// <summary>
        /// Draws every plan the standalone app has queued. The app writes a
        /// request file and then asks AutoCAD to run this command over COM.
        /// </summary>
        [CommandMethod("AICADSYNC", CommandFlags.Modal)]
        public void SyncInbox()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            List<string> requests = PlanInbox.PendingRequests();
            if (requests.Count == 0)
            {
                // Naming the folder and the running engine turns "nothing
                // happened" into something diagnosable: if the app queued a
                // plan and the engine reports an empty inbox, the two are
                // looking at different places, and this says which.
                ed.WriteMessage("\nAiCad: nothing queued." +
                                "\n  inbox : " + PlanInbox.Folder +
                                "\n  engine: " + EngineLocation() + "\n");
                return;
            }

            for (int i = 0; i < requests.Count; i++) ProcessRequest(doc, requests[i]);
        }

        /// <summary>Where the running engine was loaded from, for diagnostics.</summary>
        private static string EngineLocation()
        {
            try
            {
                string location = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location) ? "(unknown)" : location;
            }
            catch (System.Exception)
            {
                return "(unknown)";
            }
        }

        private static void ProcessRequest(Document doc, string path)
        {
            Editor ed = doc.Editor;

            string fileName = Path.GetFileName(path);
            string id = fileName.EndsWith(PlanInbox.RequestSuffix, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - PlanInbox.RequestSuffix.Length)
                : Path.GetFileNameWithoutExtension(fileName);

            JsonValue envelope = null;
            try
            {
                envelope = JsonValue.ParseLenient(File.ReadAllText(path, System.Text.Encoding.UTF8));
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nAiCad: could not read queued plan: " + ex.Message + "\n");
            }

            // Remove the request first: a plan that crashes must not be retried
            // on every subsequent sync.
            try { File.Delete(path); }
            catch (System.Exception) { }

            if (envelope == null)
            {
                PlanInbox.WriteResult(id, false, 0,
                    new List<string>(new string[] { "The queued plan file was unreadable." }),
                    new List<string>());
                return;
            }

            DrawPlan plan = DrawPlan.FromJson(envelope["plan"]);

            // Refining a drawing replaces it: the old entities are erased and the
            // new version lands on the same spot, instead of a second copy
            // appearing beside the first.
            List<string> eraseHandles = new List<string>();
            JsonValue handleList = envelope["eraseHandles"];
            if (handleList != null && handleList.Kind == JsonKind.Array)
            {
                for (int i = 0; i < handleList.Count; i++)
                {
                    string h = handleList.At(i) != null ? handleList.At(i).Text : null;
                    if (!string.IsNullOrEmpty(h)) eraseHandles.Add(h);
                }
            }

            Point3d insertion;
            JsonValue given = envelope["insertion"];
            if (given != null && given.Kind == JsonKind.Array && given.Count >= 2)
                insertion = new Point3d(given.At(0).Number, given.At(1).Number, 0);
            else
                insertion = Placement.NextFreeSpot(doc);

            if (envelope.GetBool("askForPoint", false))
            {
                PromptPointOptions options = new PromptPointOptions(
                    "\nSpecify insertion point or press Enter for the drawing origin: ");
                options.AllowNone = true;
                PromptPointResult picked = ed.GetPoint(options);
                if (picked.Status == PromptStatus.OK) insertion = picked.Value;
                else if (picked.Status != PromptStatus.None)
                {
                    PlanInbox.WriteResult(id, false, 0,
                        new List<string>(new string[] { "Cancelled at the insertion point prompt." }),
                        new List<string>());
                    ed.WriteMessage("\nAiCad: cancelled.\n");
                    return;
                }
            }

            ExecutionResult result = PlanExecutor.Execute(doc, plan, insertion, eraseHandles);
            PlanInbox.WriteResult(id, result.Success, result.EntityCount, result.Errors, result.Warnings,
                                  result.Handles, result.Success, result.InsertionX, result.InsertionY);

            if (result.Success)
            {
                // The interactive command has always done this; the queued path
                // did not, so a drawing requested from the app landed correctly
                // but off-screen and read as "it says drawn, but nothing is
                // there". Geometry goes into free space, which is rarely where
                // the user happens to be looking.
                Placement.ZoomTo(doc, result.Created);

                ed.WriteMessage("\nAiCad drew " + result.EntityCount.ToString(CultureInfo.InvariantCulture) +
                                " entities for \"" + plan.Name + "\".\n");
            }
            else
            {
                ed.WriteMessage("\nAiCad could not draw the plan:\n");
                for (int i = 0; i < result.Errors.Count; i++)
                    ed.WriteMessage("  " + result.Errors[i] + "\n");
            }
        }

        /// <summary>
        /// Turns a drawing the user already has into a worked example, so the
        /// assistant can learn their conventions without them redrawing anything.
        /// </summary>
        [CommandMethod("AICADCAPTURE", CommandFlags.Modal)]
        public void CaptureExample()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            PromptSelectionResult selection = ed.GetSelection();
            if (selection.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nNothing selected.\n");
                return;
            }

            PromptStringOptions ask = new PromptStringOptions(
                "\nDescribe this drawing (used to match it to future requests): ");
            ask.AllowSpaces = true;
            PromptResult described = ed.GetString(ask);
            if (described.Status != PromptStatus.OK) return;

            string description = described.StringResult;
            if (string.IsNullOrEmpty(description))
            {
                ed.WriteMessage("\nA description is needed so the example can be matched later.\n");
                return;
            }

            ObjectId[] ids = selection.Value.GetObjectIds();
            int captured = 0, skipped = 0;
            JsonValue plan = null;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                try
                {
                    plan = DrawingCapture.Capture(doc.Database, tr, ids, description,
                                                  out captured, out skipped);
                    tr.Commit();
                }
                catch (System.Exception ex)
                {
                    tr.Abort();
                    ed.WriteMessage("\nCapture failed: " + ex.Message + "\n");
                    return;
                }
            }

            if (plan == null || captured == 0)
            {
                ed.WriteMessage("\nNothing in that selection could be captured.\n");
                return;
            }

            try
            {
                ExampleStore.Save(description, description, plan);
                ed.WriteMessage("\nSaved as an example: " +
                                captured.ToString(CultureInfo.InvariantCulture) + " entities" +
                                (skipped > 0
                                    ? ", " + skipped.ToString(CultureInfo.InvariantCulture) +
                                      " skipped (no equivalent operation)"
                                    : "") + ".\n");
                ed.WriteMessage("AiCad will use it when a similar drawing is requested.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCould not save the example: " + ex.Message + "\n");
            }
        }

        /// <summary>
        /// Imports the drawing files the app queued, each becoming an example.
        /// Files are read into side databases, so nothing the user has open is
        /// touched and a whole folder can be processed in one pass.
        /// </summary>
        [CommandMethod("AICADIMPORT", CommandFlags.Modal)]
        public void ImportExamples()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            List<ReferenceFile> files = ImportJob.Take();
            if (files.Count == 0)
            {
                ed.WriteMessage("\nAiCad: no files were queued for import.\n");
                return;
            }

            int ok = 0, failed = 0;
            ed.WriteMessage("\nAiCad: importing " +
                            files.Count.ToString(CultureInfo.InvariantCulture) + " file(s)...\n");

            for (int i = 0; i < files.Count; i++)
            {
                ImportProgress.Report(files[i].Title, i, files.Count);

                DrawingImport.Outcome outcome =
                    DrawingImport.ImportFile(files[i].Path, files[i].Id, files[i].Title);
                if (outcome.Ok)
                {
                    ok++;
                    // A drawing that reads now clears whatever stopped it before.
                    ReferenceLibrary.ClearFailure(files[i].Id);
                    ed.WriteMessage("  " + outcome.Title + ": " +
                                    outcome.Captured.ToString(CultureInfo.InvariantCulture) + " entities" +
                                    (outcome.Skipped > 0
                                        ? ", " + outcome.Skipped.ToString(CultureInfo.InvariantCulture) + " skipped"
                                        : "") + "\n");
                }
                else
                {
                    failed++;
                    // Recorded, so a file that cannot be converted is not read
                    // again on every request. Editing the drawing changes its id
                    // and clears the mark by itself.
                    ReferenceLibrary.MarkFailed(files[i].Id, outcome.Error);
                    ed.WriteMessage("  " + outcome.Title + ": " + (outcome.Error ?? "failed") + "\n");
                }
            }

            ImportProgress.Clear();
            ed.WriteMessage("AiCad: " + ok.ToString(CultureInfo.InvariantCulture) + " added, " +
                            failed.ToString(CultureInfo.InvariantCulture) + " failed.\n");
        }

        /// <summary>Prints the op catalogue, i.e. everything the AI can draw.</summary>
        [CommandMethod("AICADOPS", CommandFlags.Modal)]
        public void ListOps()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.Editor.WriteMessage("\nAiCad operations:\n" + Capabilities.UsageCatalogue() + "\n");
        }

        private static void Execute(Document doc, DrawPlan plan, Point3d insertion)
        {
            Editor ed = doc.Editor;
            ExecutionResult result = PlanExecutor.Execute(doc, plan, insertion);

            if (result.Success)
            {
                Placement.ZoomTo(doc, result.Created);
                string message = "Drew " + result.EntityCount.ToString(CultureInfo.InvariantCulture) +
                                 " entities for \"" + plan.Name + "\".";
                ed.WriteMessage("\n" + message + "\n");
                PaletteHost.Report(message, false);

                for (int i = 0; i < result.Warnings.Count; i++)
                {
                    ed.WriteMessage("  warning: " + result.Warnings[i] + "\n");
                    PaletteHost.Report("  warning: " + result.Warnings[i], true);
                }
            }
            else
            {
                ed.WriteMessage("\nAiCad could not draw the plan:\n");
                for (int i = 0; i < result.Errors.Count; i++)
                {
                    ed.WriteMessage("  " + result.Errors[i] + "\n");
                    PaletteHost.Report("  " + result.Errors[i], true);
                }
            }
        }

    }
}
