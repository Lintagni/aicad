using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AiCad.Json;
using AiCad.Model;

namespace AiCad.Execution
{
    public class ExecutionResult
    {
        public bool Success;
        public int EntityCount;
        public List<string> Errors;
        public List<string> Warnings;

        /// <summary>Ids of the entities drawn, used to zoom to the result.</summary>
        public List<ObjectId> Created;

        /// <summary>
        /// Handles of the entities drawn. Unlike ObjectIds these survive across
        /// sessions, so the app can ask for exactly this drawing to be replaced
        /// when the user refines the request.
        /// </summary>
        public List<string> Handles;

        /// <summary>Where the plan was placed, so a replacement lands identically.</summary>
        public double InsertionX;
        public double InsertionY;

        public ExecutionResult()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
            Created = new List<ObjectId>();
            Handles = new List<string>();
        }
    }

    /// <summary>
    /// Everything the model returns passes through here before a single entity
    /// is created: unknown ops, bad numbers and oversized plans are rejected up
    /// front rather than half-drawn.
    /// </summary>
    public static class PlanValidator
    {
        public static OpIssues Validate(DrawPlan plan, OpRegistry registry)
        {
            OpIssues issues = new OpIssues();

            if (plan == null || plan.Ops == null || plan.Ops.Count == 0)
            {
                issues.Errors.Add("The plan contains no drawing operations.");
                return issues;
            }

            int total = DrawPlan.CountOps(plan.Ops);
            if (total > Limits.MaxOps)
            {
                issues.Errors.Add("Plan has " + total.ToString(CultureInfo.InvariantCulture) +
                                  " operations; the limit is " + Limits.MaxOps + ".");
                return issues;
            }

            ValidateList(issues, registry, plan.Ops, 0, 0);
            return issues;
        }

        private static void ValidateList(OpIssues issues, OpRegistry registry, List<JsonValue> ops,
                                         int depth, int indexBase)
        {
            if (depth > Limits.MaxNestingDepth)
            {
                issues.Errors.Add("Operations nested deeper than " + Limits.MaxNestingDepth + ".");
                return;
            }

            for (int i = 0; i < ops.Count; i++)
            {
                JsonValue op = ops[i];
                issues.OpIndex = indexBase + i;

                if (op == null || op.Kind != JsonKind.Object)
                {
                    issues.Error("is not an object");
                    continue;
                }

                string name = op.GetString("op", null);
                if (string.IsNullOrEmpty(name))
                {
                    issues.Error("has no 'op' field");
                    continue;
                }

                IOp handler = registry.Find(name);
                if (handler == null)
                {
                    issues.Error("unknown op '" + name + "'");
                    continue;
                }

                try
                {
                    handler.Validate(issues, op);
                    Mounting.Validate(issues, op);
                }
                catch (Exception ex)
                {
                    issues.Error("could not be validated (" + ex.Message + ")");
                }

                List<JsonValue> nested = op.GetArray("ops");
                if (nested.Count > 0)
                {
                    int saved = issues.OpIndex;
                    ValidateList(issues, registry, nested, depth + 1, 0);
                    issues.OpIndex = saved;
                }

                if (issues.Errors.Count > 40)
                {
                    issues.Errors.Add("... further errors suppressed.");
                    return;
                }
            }

            issues.OpIndex = -1;
        }
    }

    public static class PlanExecutor
    {
        /// <summary>
        /// Erases entities by handle. A handle that no longer resolves is simply
        /// skipped: the user may have deleted the geometry themselves.
        /// </summary>
        private static void EraseByHandle(Database db, Transaction tr, List<string> handles, DrawContext ctx)
        {
            int erased = 0;
            for (int i = 0; i < handles.Count; i++)
            {
                try
                {
                    long value = Convert.ToInt64(handles[i], 16);
                    Handle h = new Handle(value);
                    ObjectId id;
                    if (!db.TryGetObjectId(h, out id)) continue;
                    if (id.IsNull || id.IsErased) continue;

                    Entity entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null) continue;
                    entity.Erase();
                    erased++;
                }
                catch (Exception)
                {
                }
            }
            if (erased > 0 && ctx != null)
                ctx.Warn("Replaced the previous drawing (" +
                         erased.ToString(CultureInfo.InvariantCulture) + " entities removed).");
        }

        /// <summary>
        /// Validates then draws the plan in one transaction. On any failure the
        /// transaction is aborted, so the drawing is never left half-modified.
        /// A successful run is a single UNDO step.
        /// </summary>
        public static ExecutionResult Execute(Document doc, DrawPlan plan, Point3d insertionPoint)
        {
            return Execute(doc, plan, insertionPoint, null);
        }

        /// <summary>
        /// Draws the plan, first erasing any entities named in eraseHandles.
        /// Both happen in one transaction, so replacing a drawing is a single
        /// undo step and never leaves the old copy behind on failure.
        /// </summary>
        public static ExecutionResult Execute(Document doc, DrawPlan plan, Point3d insertionPoint,
                                              List<string> eraseHandles)
        {
            ExecutionResult result = new ExecutionResult();
            OpRegistry registry = Capabilities.Registry;

            OpIssues issues = PlanValidator.Validate(plan, registry);
            if (!issues.Ok)
            {
                result.Errors.AddRange(issues.Errors);
                return result;
            }

            Database db = doc.Database;
            DrawContext ctx = new DrawContext();

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        BlockTableRecord space =
                            (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                        ctx.Db = db;
                        ctx.Tr = tr;
                        ctx.Space = space;
                        ctx.Ed = doc.Editor;
                        ctx.Registry = registry;
                        ctx.Origin = insertionPoint.GetAsVector();
                        ctx.DefaultLayer = "0";

                        // Remove the previous version before drawing the new one.
                        if (eraseHandles != null) EraseByHandle(db, tr, eraseHandles, ctx);

                        // Declared layers first, so ops can reference them by name.
                        for (int i = 0; i < plan.Layers.Count; i++)
                        {
                            LayerSpec spec = plan.Layers[i];
                            ctx.EnsureLayer(spec.Name, spec.Color, spec.Linetype, spec.LineWeight);
                        }

                        ctx.RunOps(plan.Ops);

                        // Done before the commit, while the solids are still open
                        // in this transaction. Interference is reported, never
                        // corrected: the geometry the user asked for is what gets
                        // drawn, and the warning says what looks wrong about it.
                        ModelReview.Check(ctx);
                        SheetReview.Check(ctx);

                        tr.Commit();
                        result.Success = true;
                        result.EntityCount = ctx.Created.Count;
                        result.Created.AddRange(ctx.Created);
                        result.InsertionX = insertionPoint.X;
                        result.InsertionY = insertionPoint.Y;
                        for (int i = 0; i < ctx.Created.Count; i++)
                        {
                            try { result.Handles.Add(ctx.Created[i].Handle.ToString()); }
                            catch (Exception) { }
                        }
                        result.Warnings.AddRange(ctx.Warnings);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception acEx)
                    {
                        tr.Abort();
                        result.Errors.Add("AutoCAD rejected the plan: " + acEx.Message);
                    }
                    catch (Exception ex)
                    {
                        tr.Abort();
                        result.Errors.Add("Plan aborted: " + ex.Message);
                    }
                }
            }

            if (result.Success)
            {
                try
                {
                    doc.Editor.UpdateScreen();
                }
                catch (Exception)
                {
                    // Redraw failures are cosmetic; the geometry is already committed.
                }
            }

            return result;
        }
    }
}
