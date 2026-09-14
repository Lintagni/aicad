using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AiCad.Json;

namespace AiCad.Execution
{
    /// <summary>
    /// One drawing capability the AI is allowed to invoke. Primitives create
    /// entities directly; generators expand into other ops via ctx.RunOps.
    /// Everything the model can do is an IOp, so the catalogue is the contract.
    /// </summary>
    public interface IOp
    {
        /// <summary>Value of the "op" field, lower case.</summary>
        string Name { get; }

        /// <summary>One-line signature injected into the system prompt.</summary>
        string Usage { get; }

        /// <summary>Reject nonsense before anything touches the database.</summary>
        void Validate(OpIssues issues, JsonValue op);

        void Draw(DrawContext ctx, JsonValue op);
    }

    /// <summary>Collected validation problems for a whole plan.</summary>
    public class OpIssues
    {
        public List<string> Errors;
        public int OpIndex;

        public OpIssues()
        {
            Errors = new List<string>();
            OpIndex = -1;
        }

        public void Error(string message)
        {
            string prefix = OpIndex >= 0 ? "op[" + OpIndex.ToString(CultureInfo.InvariantCulture) + "] " : "";
            Errors.Add(prefix + message);
        }

        public bool Ok { get { return Errors.Count == 0; } }

        /// <summary>Finite, in-range coordinate check. Catches NaN and runaway values.</summary>
        public void RequirePoint(JsonValue op, string field, bool required)
        {
            JsonValue v = op[field];
            if (v == null)
            {
                if (required) Error("missing required point '" + field + "'");
                return;
            }
            if (v.Kind != JsonKind.Array || v.Count < 2)
            {
                Error("'" + field + "' must be [x,y] or [x,y,z]");
                return;
            }
            for (int i = 0; i < v.Count && i < 3; i++)
            {
                JsonValue c = v.At(i);
                if (c == null || c.Kind != JsonKind.Number || double.IsNaN(c.Number) || double.IsInfinity(c.Number))
                {
                    Error("'" + field + "' has a non-numeric coordinate");
                    return;
                }
                if (Math.Abs(c.Number) > Limits.MaxCoordinate)
                {
                    Error("'" + field + "' coordinate out of range (max " +
                          Limits.MaxCoordinate.ToString(CultureInfo.InvariantCulture) + ")");
                    return;
                }
            }
        }

        public double RequirePositive(JsonValue op, string field)
        {
            JsonValue v = op[field];
            if (v == null || v.Kind != JsonKind.Number)
            {
                Error("missing or non-numeric '" + field + "'");
                return 0;
            }
            if (double.IsNaN(v.Number) || double.IsInfinity(v.Number) || v.Number <= 0)
            {
                Error("'" + field + "' must be greater than zero");
                return 0;
            }
            if (v.Number > Limits.MaxCoordinate)
            {
                Error("'" + field + "' is unreasonably large");
                return 0;
            }
            return v.Number;
        }

        public void RequireText(JsonValue op, string field)
        {
            string s = op.GetString(field, null);
            if (s == null) { Error("missing '" + field + "'"); return; }
            if (s.Length > Limits.MaxTextLength) Error("'" + field + "' exceeds " + Limits.MaxTextLength + " characters");
        }
    }

    /// <summary>Hard ceilings. Nothing the model returns can push past these.</summary>
    public static class Limits
    {
        public const double MaxCoordinate = 1.0e7;
        public const int MaxOps = 4000;
        public const int MaxEntities = 20000;
        public const int MaxNestingDepth = 6;
        public const int MaxRepeatCount = 2000;
        public const int MaxTextLength = 2000;
        public const int MaxPolylinePoints = 2000;
    }

    /// <summary>Name -> capability, plus the prompt fragment describing them all.</summary>
    public class OpRegistry
    {
        private readonly Dictionary<string, IOp> _ops =
            new Dictionary<string, IOp>(StringComparer.OrdinalIgnoreCase);

        public void Register(IOp op)
        {
            _ops[op.Name] = op;
        }

        public IOp Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            IOp op;
            if (_ops.TryGetValue(name, out op)) return op;
            return null;
        }

        public IEnumerable<IOp> All { get { return _ops.Values; } }

        public List<string> Names
        {
            get
            {
                List<string> names = new List<string>(_ops.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names;
            }
        }
    }

    /// <summary>
    /// Everything an op needs to draw: the open transaction, the target space,
    /// the placement offset, and the entity budget.
    /// </summary>
    public class DrawContext
    {
        public Database Db;
        public Transaction Tr;
        public BlockTableRecord Space;
        public Editor Ed;
        public OpRegistry Registry;

        /// <summary>Applied to every point, so a whole plan lands at the picked point.</summary>
        public Vector3d Origin;

        /// <summary>Layer used when an op does not name one.</summary>
        public string DefaultLayer;

        public List<ObjectId> Created;

        /// <summary>
        /// Where each named part actually ended up, so a later op can be
        /// mounted against it instead of guessing coordinates.
        /// </summary>
        public Dictionary<string, Extents3d> Parts;
        public List<string> Warnings;
        public int EntityBudget;
        public int Depth;

        /// <summary>
        /// Where the sheet border and title block ended up, recorded by the
        /// title block generator. The review pass needs them to tell "inside the
        /// drawing area" from "over the title block", which is the difference
        /// between a label and a collision.
        /// </summary>
        public bool HasSheet;
        public Extents3d SheetInner;
        public Extents3d TitleBlockArea;

        /// <summary>
        /// The slice of Created that the sheet furniture itself owns, so the
        /// review does not report the title block for sitting on the title block.
        /// </summary>
        public int SheetOwnFrom = -1;
        public int SheetOwnTo = -1;

        public DrawContext()
        {
            Origin = new Vector3d(0, 0, 0);
            DefaultLayer = "0";
            Created = new List<ObjectId>();
            Parts = new Dictionary<string, Extents3d>(StringComparer.OrdinalIgnoreCase);
            Warnings = new List<string>();
            EntityBudget = Limits.MaxEntities;
            Depth = 0;
        }

        public void Warn(string message)
        {
            if (Warnings.Count < 50) Warnings.Add(message);
        }

        // ---------- geometry helpers ----------

        /// <summary>Reads [x,y] / [x,y,z] and applies the plan origin.</summary>
        public Point3d Point(JsonValue arr)
        {
            if (arr == null || arr.Kind != JsonKind.Array || arr.Count < 2) return new Point3d(Origin.X, Origin.Y, Origin.Z);
            double x = arr.At(0).Number;
            double y = arr.At(1).Number;
            double z = arr.Count > 2 && arr.At(2).Kind == JsonKind.Number ? arr.At(2).Number : 0.0;
            return new Point3d(x + Origin.X, y + Origin.Y, z + Origin.Z);
        }

        public Point3d Point(JsonValue op, string field)
        {
            return Point(op[field]);
        }

        public Point3d Point(JsonValue op, string field, Point3d fallback)
        {
            JsonValue v = op[field];
            if (v == null) return fallback;
            return Point(v);
        }

        public static double Deg2Rad(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        // ---------- symbol table helpers ----------

        public ObjectId EnsureLayer(string name, int colorIndex, string linetype, double lineWeightMm)
        {
            if (string.IsNullOrEmpty(name)) name = "0";
            name = SanitizeSymbolName(name);

            LayerTable lt = (LayerTable)Tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];

            lt.UpgradeOpen();
            LayerTableRecord ltr = new LayerTableRecord();
            ltr.Name = name;
            if (colorIndex >= 0 && colorIndex <= 255)
                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
            if (!string.IsNullOrEmpty(linetype))
            {
                ObjectId ltypeId = EnsureLinetype(linetype);
                if (!ltypeId.IsNull) ltr.LinetypeObjectId = ltypeId;
            }
            if (lineWeightMm > 0)
                ltr.LineWeight = NearestLineWeight(lineWeightMm);

            ObjectId id = lt.Add(ltr);
            Tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        private ObjectId EnsureLinetype(string name)
        {
            LinetypeTable ltt = (LinetypeTable)Tr.GetObject(Db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name)) return ltt[name];
            try
            {
                // Pulls the definition out of acad.lin / acadiso.lin on demand.
                Db.LoadLineTypeFile(name, "acadiso.lin");
                ltt = (LinetypeTable)Tr.GetObject(Db.LinetypeTableId, OpenMode.ForRead);
                if (ltt.Has(name)) return ltt[name];
            }
            catch (Exception)
            {
                Warn("Linetype '" + name + "' not found; layer left CONTINUOUS.");
            }
            return ObjectId.Null;
        }

        private static LineWeight NearestLineWeight(double mm)
        {
            int hundredths = (int)Math.Round(mm * 100.0);
            int[] valid = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90,
                            100, 106, 120, 140, 158, 200, 211 };
            int best = valid[0];
            int bestDelta = int.MaxValue;
            for (int i = 0; i < valid.Length; i++)
            {
                int delta = Math.Abs(valid[i] - hundredths);
                if (delta < bestDelta) { bestDelta = delta; best = valid[i]; }
            }
            return (LineWeight)best;
        }

        /// <summary>AutoCAD forbids &lt; &gt; / \ " : ; ? * | , = ` in symbol names.</summary>
        public static string SanitizeSymbolName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "0";
            char[] bad = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };
            string result = name.Trim();
            for (int i = 0; i < bad.Length; i++) result = result.Replace(bad[i], '_');
            if (result.Length > 255) result = result.Substring(0, 255);
            if (result.Length == 0) result = "0";
            return result;
        }

        // ---------- entity plumbing ----------

        /// <summary>
        /// Applies the op's layer/colour, appends to the target space, and books
        /// it against the entity budget.
        /// </summary>
        public void Add(Entity entity, JsonValue op)
        {
            if (EntityBudget <= 0)
                throw new InvalidOperationException("Entity budget exhausted; plan refused.");
            EntityBudget--;

            string layer = op != null ? op.GetString("layer", null) : null;
            if (string.IsNullOrEmpty(layer)) layer = DefaultLayer;
            if (!string.IsNullOrEmpty(layer))
            {
                int color = op != null ? op.GetInt("layerColor", -1) : -1;
                EnsureLayer(layer, color, null, 0);
                // Assign by name: valid on an entity that is not database-resident
                // yet, which is the state every entity is in at this point.
                entity.Layer = SanitizeSymbolName(layer);
            }

            if (op != null && op.Has("color"))
            {
                int aci = op.GetInt("color", -1);
                if (aci >= 0 && aci <= 255)
                    entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            }

            Space.AppendEntity(entity);
            Tr.AddNewlyCreatedDBObject(entity, true);
            Created.Add(entity.ObjectId);
        }

        /// <summary>Runs a nested op list (used by repeat/group and generators).</summary>
        public void RunOps(List<JsonValue> ops)
        {
            if (ops == null) return;
            if (Depth >= Limits.MaxNestingDepth)
                throw new InvalidOperationException("Op nesting deeper than " + Limits.MaxNestingDepth + ".");

            Depth++;
            try
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    JsonValue op = ops[i];
                    if (op == null || op.Kind != JsonKind.Object) continue;
                    string name = op.GetString("op", null);
                    IOp handler = Registry.Find(name);
                    if (handler == null)
                    {
                        Warn("Unknown op '" + (name ?? "?") + "' skipped.");
                        continue;
                    }
                    // Where this op's geometry starts, so it can be moved into
                    // place afterwards and remembered under its name.
                    int firstEntity = Created.Count;
                    handler.Draw(this, op);
                    Mounting.Apply(this, op, firstEntity);
                    Mounting.Record(this, op, firstEntity);
                }
            }
            finally
            {
                Depth--;
            }
        }
    }
}
