using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AiCad.Execution;
using AiCad.Json;

namespace AiCad.Ops
{
    /// <summary>Registers every primitive drawing op.</summary>
    public static class Primitives
    {
        public static void RegisterAll(OpRegistry registry)
        {
            registry.Register(new LineOp());
            registry.Register(new PolylineOp());
            registry.Register(new RectangleOp());
            registry.Register(new CircleOp());
            registry.Register(new ArcOp());
            registry.Register(new EllipseOp());
            registry.Register(new PointOp());
            registry.Register(new TextOp());
            registry.Register(new MTextOp());
            registry.Register(new DimLinearOp());
            registry.Register(new DimAlignedOp());
            registry.Register(new DimRadialOp());
            registry.Register(new DimDiameterOp());
            registry.Register(new HatchOp());
            registry.Register(new InsertOp());
            registry.Register(new RepeatOp());
            registry.Register(new GroupOp());
            registry.Register(new SymbolOp());
            Solids.RegisterAll(registry);
        }

        /// <summary>Quarter-circle bulge, used for rounded corners.</summary>
        public const double QuarterBulge = 0.41421356237309503;
    }

    // ------------------------------------------------------------------ lines

    public class LineOp : IOp
    {
        public string Name { get { return "line"; } }
        public string Usage
        {
            get { return "line: {op,start:[x,y],end:[x,y],layer?,color?}"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "start", true);
            issues.RequirePoint(op, "end", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Line line = new Line(ctx.Point(op, "start"), ctx.Point(op, "end"));
            ctx.Add(line, op);
        }
    }

    public class PolylineOp : IOp
    {
        public string Name { get { return "polyline"; } }
        public string Usage
        {
            get
            {
                return "polyline: {op,points:[[x,y],...],closed?:bool,width?:number," +
                       "bulges?:[number,...],layer?,color?}  (bulge = tan(includedAngle/4), 0 = straight)";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            JsonValue pts = op["points"];
            if (pts == null || pts.Kind != JsonKind.Array || pts.Count < 2)
            {
                issues.Error("polyline needs at least 2 'points'");
                return;
            }
            if (pts.Count > Limits.MaxPolylinePoints)
            {
                issues.Error("polyline exceeds " + Limits.MaxPolylinePoints + " points");
                return;
            }
            for (int i = 0; i < pts.Count; i++)
            {
                JsonValue p = pts.At(i);
                if (p == null || p.Kind != JsonKind.Array || p.Count < 2)
                {
                    issues.Error("polyline point " + i + " is not [x,y]");
                    return;
                }
                for (int c = 0; c < 2; c++)
                {
                    JsonValue n = p.At(c);
                    if (n == null || n.Kind != JsonKind.Number || double.IsNaN(n.Number) ||
                        Math.Abs(n.Number) > Limits.MaxCoordinate)
                    {
                        issues.Error("polyline point " + i + " has a bad coordinate");
                        return;
                    }
                }
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue pts = op["points"];
            List<JsonValue> bulges = op.GetArray("bulges");
            double width = op.GetDouble("width", 0);

            Polyline pl = new Polyline();
            pl.SetDatabaseDefaults();
            for (int i = 0; i < pts.Count; i++)
            {
                Point3d p = ctx.Point(pts.At(i));
                double bulge = 0;
                if (i < bulges.Count && bulges[i] != null && bulges[i].Kind == JsonKind.Number)
                    bulge = bulges[i].Number;
                pl.AddVertexAt(i, new Point2d(p.X, p.Y), bulge, width, width);
            }
            pl.Closed = op.GetBool("closed", false);
            ctx.Add(pl, op);
        }
    }

    public class RectangleOp : IOp
    {
        public string Name { get { return "rectangle"; } }
        public string Usage
        {
            get
            {
                return "rectangle: {op,corner:[x,y],width,height,cornerRadius?:number," +
                       "layer?,color?}  (corner = lower-left)";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "corner", true);
            double w = issues.RequirePositive(op, "width");
            double h = issues.RequirePositive(op, "height");
            double r = op.GetDouble("cornerRadius", 0);
            if (r < 0) issues.Error("'cornerRadius' cannot be negative");
            if (r > 0 && (r > w / 2.0 || r > h / 2.0))
                issues.Error("'cornerRadius' larger than half the rectangle");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Point3d c = ctx.Point(op, "corner");
            double w = op.GetDouble("width", 0);
            double h = op.GetDouble("height", 0);
            double r = op.GetDouble("cornerRadius", 0);

            Polyline pl = new Polyline();
            pl.SetDatabaseDefaults();

            if (r <= 0)
            {
                pl.AddVertexAt(0, new Point2d(c.X, c.Y), 0, 0, 0);
                pl.AddVertexAt(1, new Point2d(c.X + w, c.Y), 0, 0, 0);
                pl.AddVertexAt(2, new Point2d(c.X + w, c.Y + h), 0, 0, 0);
                pl.AddVertexAt(3, new Point2d(c.X, c.Y + h), 0, 0, 0);
            }
            else
            {
                // Eight vertices: straight run, then a quarter-arc at each corner.
                double b = Primitives.QuarterBulge;
                int i = 0;
                pl.AddVertexAt(i++, new Point2d(c.X + r, c.Y), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X + w - r, c.Y), b, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X + w, c.Y + r), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X + w, c.Y + h - r), b, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X + w - r, c.Y + h), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X + r, c.Y + h), b, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X, c.Y + h - r), 0, 0, 0);
                pl.AddVertexAt(i++, new Point2d(c.X, c.Y + r), b, 0, 0);
            }

            pl.Closed = true;
            ctx.Add(pl, op);
        }
    }

    // ---------------------------------------------------------------- curves

    public class CircleOp : IOp
    {
        public string Name { get { return "circle"; } }
        public string Usage { get { return "circle: {op,center:[x,y],radius,layer?,color?}"; } }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "radius");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Circle circle = new Circle(ctx.Point(op, "center"), Vector3d.ZAxis, op.GetDouble("radius", 1));
            ctx.Add(circle, op);
        }
    }

    public class ArcOp : IOp
    {
        public string Name { get { return "arc"; } }
        public string Usage
        {
            get { return "arc: {op,center:[x,y],radius,startAngle,endAngle,layer?}  (degrees, counter-clockwise)"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "radius");
            if (op["startAngle"] == null || op["endAngle"] == null)
                issues.Error("arc needs 'startAngle' and 'endAngle' in degrees");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Arc arc = new Arc(
                ctx.Point(op, "center"),
                op.GetDouble("radius", 1),
                DrawContext.Deg2Rad(op.GetDouble("startAngle", 0)),
                DrawContext.Deg2Rad(op.GetDouble("endAngle", 90)));
            ctx.Add(arc, op);
        }
    }

    public class EllipseOp : IOp
    {
        public string Name { get { return "ellipse"; } }
        public string Usage
        {
            get { return "ellipse: {op,center:[x,y],majorAxis:[dx,dy],ratio:0..1,layer?}"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePoint(op, "majorAxis", true);
            double ratio = op.GetDouble("ratio", 0.5);
            if (ratio <= 0 || ratio > 1) issues.Error("ellipse 'ratio' must be in (0,1]");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue axis = op["majorAxis"];
            Vector3d major = new Vector3d(axis.At(0).Number, axis.At(1).Number, 0);
            Ellipse ellipse = new Ellipse(ctx.Point(op, "center"), Vector3d.ZAxis, major,
                                          op.GetDouble("ratio", 0.5), 0, 2 * Math.PI);
            ctx.Add(ellipse, op);
        }
    }

    public class PointOp : IOp
    {
        public string Name { get { return "point"; } }
        public string Usage { get { return "point: {op,position:[x,y],layer?}"; } }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            ctx.Add(new DBPoint(ctx.Point(op, "position")), op);
        }
    }

    // ------------------------------------------------------------------ text

    public class TextOp : IOp
    {
        public string Name { get { return "text"; } }
        public string Usage
        {
            get
            {
                return "text: {op,position:[x,y],value,height,rotation?:deg," +
                       "justify?:left|center|right|middle,layer?,color?}";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
            issues.RequireText(op, "value");
            issues.RequirePositive(op, "height");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Point3d pos = ctx.Point(op, "position");
            DBText text = new DBText();
            text.SetDatabaseDefaults();
            text.TextString = op.GetString("value", "");
            text.Height = op.GetDouble("height", 2.5);
            text.Rotation = DrawContext.Deg2Rad(op.GetDouble("rotation", 0));
            text.Position = pos;

            string justify = op.GetString("justify", "left").ToLowerInvariant();
            if (justify != "left")
            {
                if (justify == "center") text.HorizontalMode = TextHorizontalMode.TextCenter;
                else if (justify == "right") text.HorizontalMode = TextHorizontalMode.TextRight;
                else if (justify == "middle")
                {
                    text.HorizontalMode = TextHorizontalMode.TextCenter;
                    text.VerticalMode = TextVerticalMode.TextVerticalMid;
                }
                // AlignmentPoint only takes effect once the mode is not left/base.
                text.AlignmentPoint = pos;
            }

            ctx.Add(text, op);
        }
    }

    public class MTextOp : IOp
    {
        public string Name { get { return "mtext"; } }
        public string Usage
        {
            get { return "mtext: {op,position:[x,y],value,height,width?,rotation?:deg,layer?}  (\\P = new line)"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
            issues.RequireText(op, "value");
            issues.RequirePositive(op, "height");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            MText mt = new MText();
            mt.SetDatabaseDefaults();
            mt.Location = ctx.Point(op, "position");
            mt.Contents = op.GetString("value", "");
            mt.TextHeight = op.GetDouble("height", 2.5);
            mt.Width = op.GetDouble("width", 0);
            mt.Rotation = DrawContext.Deg2Rad(op.GetDouble("rotation", 0));
            ctx.Add(mt, op);
        }
    }

    // ------------------------------------------------------------ dimensions

    public class DimLinearOp : IOp
    {
        public string Name { get { return "dimlinear"; } }
        public string Usage
        {
            get
            {
                return "dimlinear: {op,p1:[x,y],p2:[x,y],linePosition:[x,y]," +
                       "rotation?:deg(0=horizontal,90=vertical),text?,layer?}";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "p1", true);
            issues.RequirePoint(op, "p2", true);
            issues.RequirePoint(op, "linePosition", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            RotatedDimension dim = new RotatedDimension(
                DrawContext.Deg2Rad(op.GetDouble("rotation", 0)),
                ctx.Point(op, "p1"),
                ctx.Point(op, "p2"),
                ctx.Point(op, "linePosition"),
                op.GetString("text", ""),
                ctx.Db.Dimstyle);
            dim.SetDatabaseDefaults();
            ctx.Add(dim, op);
        }
    }

    public class DimAlignedOp : IOp
    {
        public string Name { get { return "dimaligned"; } }
        public string Usage
        {
            get { return "dimaligned: {op,p1:[x,y],p2:[x,y],linePosition:[x,y],text?,layer?}"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "p1", true);
            issues.RequirePoint(op, "p2", true);
            issues.RequirePoint(op, "linePosition", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            AlignedDimension dim = new AlignedDimension(
                ctx.Point(op, "p1"),
                ctx.Point(op, "p2"),
                ctx.Point(op, "linePosition"),
                op.GetString("text", ""),
                ctx.Db.Dimstyle);
            dim.SetDatabaseDefaults();
            ctx.Add(dim, op);
        }
    }

    public class DimRadialOp : IOp
    {
        public string Name { get { return "dimradial"; } }
        public string Usage
        {
            get { return "dimradial: {op,center:[x,y],chordPoint:[x,y],leaderLength?,text?,layer?}"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePoint(op, "chordPoint", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            RadialDimension dim = new RadialDimension(
                ctx.Point(op, "center"),
                ctx.Point(op, "chordPoint"),
                op.GetDouble("leaderLength", 0),
                op.GetString("text", ""),
                ctx.Db.Dimstyle);
            dim.SetDatabaseDefaults();
            ctx.Add(dim, op);
        }
    }

    public class DimDiameterOp : IOp
    {
        public string Name { get { return "dimdiameter"; } }
        public string Usage
        {
            get { return "dimdiameter: {op,chordPoint:[x,y],farChordPoint:[x,y],leaderLength?,text?,layer?}"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "chordPoint", true);
            issues.RequirePoint(op, "farChordPoint", true);
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            DiametricDimension dim = new DiametricDimension(
                ctx.Point(op, "chordPoint"),
                ctx.Point(op, "farChordPoint"),
                op.GetDouble("leaderLength", 0),
                op.GetString("text", ""),
                ctx.Db.Dimstyle);
            dim.SetDatabaseDefaults();
            ctx.Add(dim, op);
        }
    }

    // ----------------------------------------------------------------- hatch

    public class HatchOp : IOp
    {
        public string Name { get { return "hatch"; } }
        public string Usage
        {
            get
            {
                return "hatch: {op,boundary:[[x,y],...],pattern?:SOLID|ANSI31|ANSI37|NET," +
                       "scale?,angle?:deg,layer?,color?}";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            JsonValue b = op["boundary"];
            if (b == null || b.Kind != JsonKind.Array || b.Count < 3)
            {
                issues.Error("hatch needs a 'boundary' of at least 3 points");
                return;
            }
            if (b.Count > Limits.MaxPolylinePoints) issues.Error("hatch boundary too large");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue b = op["boundary"];
            Point2dCollection pts = new Point2dCollection();
            for (int i = 0; i < b.Count; i++)
            {
                Point3d p = ctx.Point(b.At(i));
                pts.Add(new Point2d(p.X, p.Y));
            }
            // AppendLoop wants an explicitly closed loop.
            if (pts[0] != pts[pts.Count - 1]) pts.Add(pts[0]);

            DoubleCollection bulges = new DoubleCollection();
            for (int i = 0; i < pts.Count; i++) bulges.Add(0.0);

            Hatch hatch = new Hatch();
            hatch.SetDatabaseDefaults();
            // Must live in the database before the pattern and loops are set.
            ctx.Add(hatch, op);

            // The pattern has to be applied once to make scale and angle settable,
            // then re-applied so the pattern definition picks those values up.
            string pattern = op.GetString("pattern", "ANSI31");
            hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            hatch.PatternScale = op.GetDouble("scale", 1.0);
            hatch.PatternAngle = DrawContext.Deg2Rad(op.GetDouble("angle", 0));
            hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            hatch.Associative = false;
            hatch.AppendLoop(HatchLoopTypes.Outermost, pts, bulges);
            hatch.EvaluateHatch(true);
        }
    }

    // ---------------------------------------------------------------- blocks

    public class InsertOp : IOp
    {
        public string Name { get { return "insert"; } }
        public string Usage
        {
            get
            {
                return "insert: {op,block:\"NAME\",position:[x,y],scale?,rotation?:deg," +
                       "attributes?:{TAG:\"value\"},layer?}  (block must already exist in the drawing)";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
            string block = op.GetString("block", null);
            if (string.IsNullOrEmpty(block)) issues.Error("insert needs a 'block' name");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            string blockName = op.GetString("block", "");
            BlockTable bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(blockName))
            {
                // Never fail the whole plan because the model guessed a block name.
                ctx.Warn("Block '" + blockName + "' is not in this drawing; insert skipped.");
                return;
            }

            ObjectId defId = bt[blockName];
            BlockReference br = new BlockReference(ctx.Point(op, "position"), defId);
            double scale = op.GetDouble("scale", 1.0);
            if (scale > 0) br.ScaleFactors = new Scale3d(scale);
            br.Rotation = DrawContext.Deg2Rad(op.GetDouble("rotation", 0));
            ctx.Add(br, op);

            JsonValue attrs = op["attributes"];
            BlockTableRecord def = (BlockTableRecord)ctx.Tr.GetObject(defId, OpenMode.ForRead);
            if (!def.HasAttributeDefinitions) return;

            foreach (ObjectId id in def)
            {
                AttributeDefinition ad = ctx.Tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant) continue;

                AttributeReference ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                if (attrs != null && attrs.Kind == JsonKind.Object)
                {
                    JsonValue value = attrs[ad.Tag];
                    if (value != null) ar.TextString = attrs.GetString(ad.Tag, ar.TextString);
                }
                br.AttributeCollection.AppendAttribute(ar);
                ctx.Tr.AddNewlyCreatedDBObject(ar, true);
            }
        }
    }

    // ------------------------------------------------------------- structure

    public class RepeatOp : IOp
    {
        public string Name { get { return "repeat"; } }
        public string Usage
        {
            get
            {
                return "repeat: {op,count:int,offset:[dx,dy],ops:[...]}  " +
                       "(draws the nested ops count times, shifting by offset each time)";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            int count = op.GetInt("count", 0);
            if (count < 1 || count > Limits.MaxRepeatCount)
                issues.Error("repeat 'count' must be between 1 and " + Limits.MaxRepeatCount);
            issues.RequirePoint(op, "offset", true);
            JsonValue ops = op["ops"];
            if (ops == null || ops.Kind != JsonKind.Array || ops.Count == 0)
                issues.Error("repeat needs a non-empty 'ops' array");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            int count = op.GetInt("count", 1);
            JsonValue offset = op["offset"];
            double dx = offset != null && offset.Count > 0 ? offset.At(0).Number : 0;
            double dy = offset != null && offset.Count > 1 ? offset.At(1).Number : 0;
            List<JsonValue> ops = op.GetArray("ops");

            Vector3d saved = ctx.Origin;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ctx.Origin = saved + new Vector3d(dx * i, dy * i, 0);
                    ctx.RunOps(ops);
                }
            }
            finally
            {
                ctx.Origin = saved;
            }
        }
    }

    public class GroupOp : IOp
    {
        public string Name { get { return "group"; } }
        public string Usage
        {
            get { return "group: {op,offset?:[dx,dy],layer?,ops:[...]}  (nested ops, optionally shifted)"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            JsonValue ops = op["ops"];
            if (ops == null || ops.Kind != JsonKind.Array)
                issues.Error("group needs an 'ops' array");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue offset = op["offset"];
            Vector3d saved = ctx.Origin;
            string savedLayer = ctx.DefaultLayer;
            try
            {
                if (offset != null && offset.Kind == JsonKind.Array && offset.Count >= 2)
                    ctx.Origin = saved + new Vector3d(offset.At(0).Number, offset.At(1).Number, 0);
                string layer = op.GetString("layer", null);
                if (!string.IsNullOrEmpty(layer)) ctx.DefaultLayer = layer;
                ctx.RunOps(op.GetArray("ops"));
            }
            finally
            {
                ctx.Origin = saved;
                ctx.DefaultLayer = savedLayer;
            }
        }
    }
}
