using System;
using System.Collections.Generic;
using System.Globalization;
using AiCad.Execution;
using AiCad.Json;
using Autodesk.AutoCAD.Geometry;

namespace AiCad.Generators
{
    /// <summary>
    /// Terse helpers for assembling op JSON in code. Generators expand into
    /// primitive ops, so they inherit the same validation and undo behaviour.
    /// </summary>
    public static class Ob
    {
        public static JsonValue Op(string name)
        {
            JsonValue o = JsonValue.NewObject();
            o["op"] = JsonValue.New(name);
            return o;
        }

        public static JsonValue Pt(double x, double y)
        {
            JsonValue a = JsonValue.NewArray();
            a.Add(JsonValue.New(x));
            a.Add(JsonValue.New(y));
            return a;
        }

        public static JsonValue S(this JsonValue o, string key, string value)
        {
            o[key] = JsonValue.New(value);
            return o;
        }

        public static JsonValue N(this JsonValue o, string key, double value)
        {
            o[key] = JsonValue.New(value);
            return o;
        }

        public static JsonValue B(this JsonValue o, string key, bool value)
        {
            o[key] = JsonValue.New(value);
            return o;
        }

        public static JsonValue P(this JsonValue o, string key, double x, double y)
        {
            o[key] = Pt(x, y);
            return o;
        }

        public static JsonValue Arr(this JsonValue o, string key, JsonValue array)
        {
            o[key] = array;
            return o;
        }

        public static JsonValue Points(params double[] xy)
        {
            JsonValue a = JsonValue.NewArray();
            for (int i = 0; i + 1 < xy.Length; i += 2) a.Add(Pt(xy[i], xy[i + 1]));
            return a;
        }

        public static string Num(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Layer names the bundled generators draw on.</summary>
    public static class Layers
    {
        public const string Outline = "AI-OUTLINE";
        public const string Plate = "AI-PLATE";
        public const string Rail = "AI-RAIL";
        public const string Hidden = "AI-HIDDEN";
        public const string Dim = "AI-DIM";
        public const string Text = "AI-TEXT";
        public const string Centre = "AI-CENTRE";
    }

    public static class GeneratorPack
    {
        public static void RegisterAll(OpRegistry registry)
        {
            registry.Register(new EnclosureGenerator());
            registry.Register(new DinRailGenerator());
            registry.Register(new BoltPatternGenerator());
            registry.Register(new GridGenerator());
            registry.Register(new TitleBlockGenerator());
            registry.Register(new TerminalStripGenerator());
            registry.Register(new IecSymbolGenerator());
            registry.Register(new BusbarGenerator());
        }
    }

    // -------------------------------------------------------- terminal strip

    /// <summary>
    /// A row of terminals with numbering that is sized and rotated to fit.
    /// Terminals are typically 6 mm wide, which is far too narrow for horizontal
    /// wire numbers, so those are placed vertically.
    /// </summary>
    public class TerminalStripGenerator : IOp
    {
        public string Name { get { return "terminalstrip"; } }

        public string Usage
        {
            get
            {
                return "terminalstrip: {op,position:[x,y],count,terminalWidth?(6),height?(50)," +
                       "startNumber?(1),wirePrefix?,wireStart?,rail?:bool,label?}" +
                       "  - terminal row with correctly sized numbering";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
            int count = op.GetInt("count", 0);
            if (count < 1 || count > 300) issues.Error("terminalstrip 'count' must be 1..300");
            if (op.Has("terminalWidth")) issues.RequirePositive(op, "terminalWidth");
            if (op.Has("height")) issues.RequirePositive(op, "height");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue pos = op["position"];
            double x0 = pos.At(0).Number;
            double y0 = pos.At(1).Number;

            int count = op.GetInt("count", 12);
            double tw = op.GetDouble("terminalWidth", 6);
            double height = op.GetDouble("height", 50);
            int startNumber = op.GetInt("startNumber", 1);
            string wirePrefix = op.GetString("wirePrefix", null);
            int wireStart = op.GetInt("wireStart", 101);
            bool rail = op.GetBool("rail", true);
            string label = op.GetString("label", null);

            double width = tw * count;
            // The pitch is the hard constraint on text size.
            double textHeight = Math.Max(1.5, tw * 0.5);
            double gap = textHeight * 0.8;

            List<JsonValue> ops = new List<JsonValue>();

            if (rail)
            {
                double railHeight = 35;
                ops.Add(Ob.Op("dinrail").P("position", x0 - tw, y0 + height / 2 - railHeight / 2)
                          .N("length", width + 2 * tw));
            }

            ops.Add(Ob.Op("rectangle").P("corner", x0, y0).N("width", width).N("height", height)
                      .S("layer", Layers.Outline));

            // Divider between each pair of terminals.
            for (int i = 1; i < count; i++)
            {
                double x = x0 + tw * i;
                ops.Add(Ob.Op("line").P("start", x, y0).P("end", x, y0 + height).S("layer", Layers.Outline));
            }

            for (int i = 0; i < count; i++)
            {
                double centre = x0 + tw * (i + 0.5);

                string number = (startNumber + i).ToString(CultureInfo.InvariantCulture);
                if (FitsHorizontally(number, textHeight, tw))
                {
                    ops.Add(Ob.Op("text").P("position", centre, y0 + height + gap).S("value", number)
                              .N("height", textHeight).S("justify", "center").S("layer", Layers.Text));
                }
                else
                {
                    ops.Add(Ob.Op("text").P("position", centre - textHeight * 0.4, y0 + height + gap)
                              .S("value", number).N("height", textHeight).N("rotation", 90)
                              .S("layer", Layers.Text));
                }

                if (string.IsNullOrEmpty(wirePrefix)) continue;

                // Wire numbers are long relative to the pitch, so they run vertically
                // downwards from below the strip.
                string wire = wirePrefix + (wireStart + i).ToString(CultureInfo.InvariantCulture);
                double wireLength = EstimateWidth(wire, textHeight);
                ops.Add(Ob.Op("text").P("position", centre - textHeight * 0.4, y0 - gap - wireLength)
                          .S("value", wire).N("height", textHeight).N("rotation", 90)
                          .S("layer", Layers.Text));
            }

            if (!string.IsNullOrEmpty(label))
            {
                // Caption sized to the strip, not to a terminal.
                double captionHeight = Math.Max(textHeight, width / 25.0);
                ops.Add(Ob.Op("text").P("position", x0 + width / 2, y0 + height + gap + textHeight * 2.2)
                          .S("value", label).N("height", captionHeight)
                          .S("justify", "center").S("layer", Layers.Text));
            }

            ctx.RunOps(ops);
        }

        private static double EstimateWidth(string text, double height)
        {
            return text == null ? 0 : text.Length * height * 0.7;
        }

        private static bool FitsHorizontally(string text, double height, double available)
        {
            return EstimateWidth(text, height) <= available * 0.9;
        }
    }

    // ------------------------------------------------------------- enclosure

    /// <summary>Front elevation of a sheet-metal enclosure, optionally with a side view.</summary>
    public class EnclosureGenerator : IOp
    {
        public string Name { get { return "enclosure"; } }

        public string Usage
        {
            get
            {
                return "enclosure: {op,origin?:[x,y],width,height,depth?,cornerRadius?," +
                       "plateInset?(default 25),door?:bool,glandPlateHeight?,railCount?,railSpacing?," +
                       "label?,dimensions?:bool(default true),sideView?:bool}  " +
                       "- front elevation of a panel enclosure in mm";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            double w = issues.RequirePositive(op, "width");
            double h = issues.RequirePositive(op, "height");
            if (w > 0 && w > 6000) issues.Error("enclosure width above 6000 mm looks wrong");
            if (h > 0 && h > 6000) issues.Error("enclosure height above 6000 mm looks wrong");
            if (op.Has("depth") && op.GetDouble("depth", 1) <= 0) issues.Error("enclosure 'depth' must be positive");
            int rails = op.GetInt("railCount", 0);
            if (rails < 0 || rails > 40) issues.Error("enclosure 'railCount' must be 0..40");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            double w = op.GetDouble("width", 600);
            double h = op.GetDouble("height", 800);
            double depth = op.GetDouble("depth", 250);
            double radius = op.GetDouble("cornerRadius", 0);
            double inset = op.GetDouble("plateInset", 25);
            double gland = op.GetDouble("glandPlateHeight", 0);
            int railCount = op.GetInt("railCount", 0);
            double railSpacing = op.GetDouble("railSpacing", 0);
            bool door = op.GetBool("door", true);
            bool dims = op.GetBool("dimensions", true);
            bool sideView = op.GetBool("sideView", false);
            string label = op.GetString("label", null);

            JsonValue origin = op["origin"];
            double ox = origin != null && origin.Count > 0 ? origin.At(0).Number : 0;
            double oy = origin != null && origin.Count > 1 ? origin.At(1).Number : 0;

            List<JsonValue> ops = new List<JsonValue>();

            // Outer shell.
            ops.Add(Ob.Op("rectangle").P("corner", ox, oy).N("width", w).N("height", h)
                      .N("cornerRadius", radius).S("layer", Layers.Outline));

            // Door leaf, drawn just inside the shell.
            if (door)
            {
                double d = 12;
                if (w > 2 * d + 20 && h > 2 * d + 20)
                    ops.Add(Ob.Op("rectangle").P("corner", ox + d, oy + d)
                              .N("width", w - 2 * d).N("height", h - 2 * d).S("layer", Layers.Outline));
            }

            // Back mounting plate, shown hidden because it sits behind the door.
            if (inset > 0 && w > 2 * inset + 10 && h > 2 * inset + 10)
            {
                ops.Add(Ob.Op("rectangle").P("corner", ox + inset, oy + inset)
                          .N("width", w - 2 * inset).N("height", h - 2 * inset).S("layer", Layers.Plate));
            }

            // Gland plate strip along the bottom.
            if (gland > 0 && gland < h / 2)
            {
                ops.Add(Ob.Op("rectangle").P("corner", ox + inset, oy + inset)
                          .N("width", Math.Max(10, w - 2 * inset)).N("height", gland).S("layer", Layers.Plate));
                ops.Add(Ob.Op("text").P("position", ox + w / 2, oy + inset + gland / 2 - 5)
                          .S("value", "GLAND PLATE").N("height", Math.Max(6, w / 60))
                          .S("justify", "center").S("layer", Layers.Text));
            }

            // DIN rails, spread over the usable plate height.
            if (railCount > 0)
            {
                double plateBottom = oy + inset + gland;
                double plateTop = oy + h - inset;
                double usable = plateTop - plateBottom;
                if (usable > 50)
                {
                    double spacing = railSpacing > 0 ? railSpacing : usable / (railCount + 1);
                    double railLength = Math.Max(20, w - 2 * inset - 40);
                    double railX = ox + inset + 20;
                    for (int i = 0; i < railCount; i++)
                    {
                        double y = railSpacing > 0
                            ? plateTop - spacing * (i + 1)
                            : plateBottom + spacing * (i + 1);
                        if (y < plateBottom || y > plateTop) continue;
                        ops.Add(Ob.Op("dinrail").P("position", railX, y).N("length", railLength));
                    }
                }
            }

            // Overall dimensions, offset clear of the shell.
            if (dims)
            {
                double gap = Math.Max(30, w * 0.08);
                ops.Add(Ob.Op("dimlinear").P("p1", ox, oy).P("p2", ox + w, oy)
                          .P("linePosition", ox + w / 2, oy - gap).N("rotation", 0).S("layer", Layers.Dim));
                ops.Add(Ob.Op("dimlinear").P("p1", ox + w, oy).P("p2", ox + w, oy + h)
                          .P("linePosition", ox + w + gap, oy + h / 2).N("rotation", 90).S("layer", Layers.Dim));
            }

            // Side view placed to the right, sharing the same height.
            if (sideView && depth > 0)
            {
                double gapX = Math.Max(80, w * 0.25);
                double sx = ox + w + gapX;
                ops.Add(Ob.Op("rectangle").P("corner", sx, oy).N("width", depth).N("height", h)
                          .N("cornerRadius", radius).S("layer", Layers.Outline));
                if (inset > 0 && depth > inset + 5)
                {
                    // Mounting plate seen edge-on.
                    ops.Add(Ob.Op("line").P("start", sx + depth - inset, oy + inset)
                              .P("end", sx + depth - inset, oy + h - inset).S("layer", Layers.Plate));
                }
                if (dims)
                {
                    double gap = Math.Max(30, w * 0.08);
                    ops.Add(Ob.Op("dimlinear").P("p1", sx, oy).P("p2", sx + depth, oy)
                              .P("linePosition", sx + depth / 2, oy - gap).N("rotation", 0).S("layer", Layers.Dim));
                }
                ops.Add(Ob.Op("text").P("position", sx + depth / 2, oy - Math.Max(60, w * 0.16))
                          .S("value", "SIDE VIEW").N("height", Math.Max(8, w / 50))
                          .S("justify", "center").S("layer", Layers.Text));
            }

            // Caption under the front view.
            string caption = label != null
                ? label
                : "ENCLOSURE " + Ob.Num(w) + " x " + Ob.Num(h) +
                  (op.Has("depth") ? " x " + Ob.Num(depth) : "");
            ops.Add(Ob.Op("text").P("position", ox + w / 2, oy - Math.Max(60, w * 0.16))
                      .S("value", caption).N("height", Math.Max(8, w / 50))
                      .S("justify", "center").S("layer", Layers.Text));

            ctx.RunOps(ops);
        }
    }

    // -------------------------------------------------------------- din rail

    /// <summary>35 mm top-hat DIN rail in front elevation.</summary>
    public class DinRailGenerator : IOp
    {
        public string Name { get { return "dinrail"; } }

        public string Usage
        {
            get
            {
                return "dinrail: {op,position:[x,y],length,height?(default 35)," +
                       "vertical?:bool,label?}  - TS35 rail shown in elevation";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);
            issues.RequirePositive(op, "length");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue pos = op["position"];
            double x = pos.At(0).Number;
            double y = pos.At(1).Number;
            double length = op.GetDouble("length", 200);
            double height = op.GetDouble("height", 35);
            bool vertical = op.GetBool("vertical", false);

            double w = vertical ? height : length;
            double h = vertical ? length : height;

            List<JsonValue> ops = new List<JsonValue>();
            ops.Add(Ob.Op("rectangle").P("corner", x, y).N("width", w).N("height", h)
                      .S("layer", Layers.Rail));

            // Slot line down the middle of the rail.
            if (vertical)
                ops.Add(Ob.Op("line").P("start", x + w / 2, y + 5).P("end", x + w / 2, y + h - 5)
                          .S("layer", Layers.Centre));
            else
                ops.Add(Ob.Op("line").P("start", x + 5, y + h / 2).P("end", x + w - 5, y + h / 2)
                          .S("layer", Layers.Centre));

            string label = op.GetString("label", null);
            if (!string.IsNullOrEmpty(label))
                ops.Add(Ob.Op("text").P("position", x + w / 2, y + h + 4).S("value", label)
                          .N("height", 8).S("justify", "center").S("layer", Layers.Text));

            ctx.RunOps(ops);
        }
    }

    // ---------------------------------------------------------- bolt pattern

    /// <summary>Rectangular or circular hole pattern with centre marks.</summary>
    public class BoltPatternGenerator : IOp
    {
        public string Name { get { return "boltpattern"; } }

        public string Usage
        {
            get
            {
                return "boltpattern: {op,mode:\"rectangular\"|\"circular\",center:[x,y],holeDiameter," +
                       "rectangular: rows,cols,spacingX,spacingY | circular: count,boltCircleDiameter," +
                       "startAngle?,centreMarks?:bool}";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "holeDiameter");
            string mode = op.GetString("mode", "rectangular").ToLowerInvariant();
            if (mode == "circular")
            {
                int count = op.GetInt("count", 0);
                if (count < 1 || count > 500) issues.Error("boltpattern 'count' must be 1..500");
                issues.RequirePositive(op, "boltCircleDiameter");
            }
            else
            {
                int rows = op.GetInt("rows", 0);
                int cols = op.GetInt("cols", 0);
                if (rows < 1 || rows > 200) issues.Error("boltpattern 'rows' must be 1..200");
                if (cols < 1 || cols > 200) issues.Error("boltpattern 'cols' must be 1..200");
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue c = op["center"];
            double cx = c.At(0).Number;
            double cy = c.At(1).Number;
            double holeR = op.GetDouble("holeDiameter", 6) / 2.0;
            bool marks = op.GetBool("centreMarks", true);
            string mode = op.GetString("mode", "rectangular").ToLowerInvariant();
            string layer = op.GetString("layer", Layers.Outline);

            List<JsonValue> ops = new List<JsonValue>();

            if (mode == "circular")
            {
                int count = op.GetInt("count", 4);
                double bcr = op.GetDouble("boltCircleDiameter", 100) / 2.0;
                double start = op.GetDouble("startAngle", 0);

                // Bolt circle itself, as a centre-line reference.
                ops.Add(Ob.Op("circle").P("center", cx, cy).N("radius", bcr).S("layer", Layers.Centre));

                for (int i = 0; i < count; i++)
                {
                    double a = DrawContext.Deg2Rad(start + 360.0 * i / count);
                    double hx = cx + bcr * Math.Cos(a);
                    double hy = cy + bcr * Math.Sin(a);
                    AddHole(ops, hx, hy, holeR, marks, layer);
                }
            }
            else
            {
                int rows = op.GetInt("rows", 2);
                int cols = op.GetInt("cols", 2);
                double sx = op.GetDouble("spacingX", 100);
                double sy = op.GetDouble("spacingY", 100);
                // Centre the grid on the given point.
                double x0 = cx - sx * (cols - 1) / 2.0;
                double y0 = cy - sy * (rows - 1) / 2.0;

                for (int r = 0; r < rows; r++)
                {
                    for (int col = 0; col < cols; col++)
                        AddHole(ops, x0 + sx * col, y0 + sy * r, holeR, marks, layer);
                }
            }

            ctx.RunOps(ops);
        }

        private static void AddHole(List<JsonValue> ops, double x, double y, double r, bool marks, string layer)
        {
            ops.Add(Ob.Op("circle").P("center", x, y).N("radius", r).S("layer", layer));
            if (!marks) return;
            double m = r * 1.6;
            ops.Add(Ob.Op("line").P("start", x - m, y).P("end", x + m, y).S("layer", Layers.Centre));
            ops.Add(Ob.Op("line").P("start", x, y - m).P("end", x, y + m).S("layer", Layers.Centre));
        }
    }

    // ------------------------------------------------------------------ grid

    /// <summary>Reference grid with A,B,C columns and 1,2,3 rows.</summary>
    public class GridGenerator : IOp
    {
        public string Name { get { return "grid"; } }

        public string Usage
        {
            get
            {
                return "grid: {op,origin:[x,y],cols,rows,spacingX,spacingY,labels?:bool}" +
                       "  - reference grid with lettered columns and numbered rows";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "origin", true);
            int cols = op.GetInt("cols", 0);
            int rows = op.GetInt("rows", 0);
            if (cols < 1 || cols > 200) issues.Error("grid 'cols' must be 1..200");
            if (rows < 1 || rows > 200) issues.Error("grid 'rows' must be 1..200");
            issues.RequirePositive(op, "spacingX");
            issues.RequirePositive(op, "spacingY");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue o = op["origin"];
            double ox = o.At(0).Number;
            double oy = o.At(1).Number;
            int cols = op.GetInt("cols", 4);
            int rows = op.GetInt("rows", 4);
            double sx = op.GetDouble("spacingX", 100);
            double sy = op.GetDouble("spacingY", 100);
            bool labels = op.GetBool("labels", true);
            string layer = op.GetString("layer", Layers.Centre);

            double w = sx * cols;
            double h = sy * rows;
            double textHeight = Math.Max(2.5, Math.Min(sx, sy) / 8.0);

            List<JsonValue> ops = new List<JsonValue>();

            for (int i = 0; i <= cols; i++)
            {
                double x = ox + sx * i;
                ops.Add(Ob.Op("line").P("start", x, oy).P("end", x, oy + h).S("layer", layer));
            }
            for (int j = 0; j <= rows; j++)
            {
                double y = oy + sy * j;
                ops.Add(Ob.Op("line").P("start", ox, y).P("end", ox + w, y).S("layer", layer));
            }

            if (labels)
            {
                for (int i = 0; i < cols; i++)
                {
                    ops.Add(Ob.Op("text").P("position", ox + sx * i + sx / 2, oy + h + textHeight)
                              .S("value", ColumnName(i)).N("height", textHeight)
                              .S("justify", "center").S("layer", Layers.Text));
                }
                for (int j = 0; j < rows; j++)
                {
                    ops.Add(Ob.Op("text").P("position", ox - textHeight * 1.5, oy + sy * j + sy / 2)
                              .S("value", (j + 1).ToString(CultureInfo.InvariantCulture))
                              .N("height", textHeight).S("justify", "middle").S("layer", Layers.Text));
                }
            }

            ctx.RunOps(ops);
        }

        /// <summary>0 -> A, 25 -> Z, 26 -> AA.</summary>
        private static string ColumnName(int index)
        {
            string name = "";
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                name = (char)('A' + rem) + name;
                index = (index - 1) / 26;
            }
            return name;
        }
    }

    // ----------------------------------------------------------- title block

    /// <summary>Sheet border plus a bottom-right title block.</summary>
    public class TitleBlockGenerator : IOp
    {
        public string Name { get { return "titleblock"; } }

        public string Usage
        {
            get
            {
                return "titleblock: {op,origin?:[x,y],sheetWidth?(420),sheetHeight?(297),margin?(10)," +
                       "title?,drawing?,drawnBy?,date?,scale?,sheet?,rev?}  - border and title block";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            if (op.Has("sheetWidth")) issues.RequirePositive(op, "sheetWidth");
            if (op.Has("sheetHeight")) issues.RequirePositive(op, "sheetHeight");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue o = op["origin"];
            double ox = o != null && o.Count > 0 ? o.At(0).Number : 0;
            double oy = o != null && o.Count > 1 ? o.At(1).Number : 0;
            double sw = op.GetDouble("sheetWidth", 420);
            double sh = op.GetDouble("sheetHeight", 297);
            double margin = op.GetDouble("margin", 10);

            // Proportioned to the sheet rather than fixed. A block pinned at
            // 180 x 60 swallows a small sheet, which is what produces the
            // "enormous title block beside a tiny diagram" drawing; the
            // fractions below reproduce the familiar 180 x 60 on A3.
            double bw = Math.Min(Clamp(sw * 0.43, 80, 180), sw - 2 * margin);
            double bh = Math.Min(Clamp(sh * 0.20, 26, 60), sh - 2 * margin);
            double bx = ox + sw - margin - bw;
            double by = oy + margin;
            double rowH = bh / 4.0;
            double th = Math.Min(4.0, rowH * 0.45);

            List<JsonValue> ops = new List<JsonValue>();

            ops.Add(Ob.Op("rectangle").P("corner", ox, oy).N("width", sw).N("height", sh).S("layer", Layers.Outline));
            ops.Add(Ob.Op("rectangle").P("corner", ox + margin, oy + margin)
                      .N("width", sw - 2 * margin).N("height", sh - 2 * margin).S("layer", Layers.Outline));
            ops.Add(Ob.Op("rectangle").P("corner", bx, by).N("width", bw).N("height", bh).S("layer", Layers.Outline));

            for (int i = 1; i < 4; i++)
                ops.Add(Ob.Op("line").P("start", bx, by + rowH * i).P("end", bx + bw, by + rowH * i)
                          .S("layer", Layers.Outline));

            // Title spans the top row; the rest is a two-column field list. Each
            // cell is padded on both sides, so the usable width is what is left
            // between the padding and the divider.
            double pad = 3;
            double wide = bw - 2 * pad;
            double half = bw / 2 - 2 * pad;

            // The width each field is allowed, in the order the text ops are
            // added, so the measured pass below can pair them up.
            List<double> cells = new List<double>();

            AddField(ops, cells, bx + pad, by + rowH * 3 + rowH / 2 - th / 2, "TITLE: " + op.GetString("title", ""), th * 1.3, wide);
            AddField(ops, cells, bx + pad, by + rowH * 2 + rowH / 2 - th / 2, "DWG No: " + op.GetString("drawing", ""), th, half);
            AddField(ops, cells, bx + bw / 2 + pad, by + rowH * 2 + rowH / 2 - th / 2, "REV: " + op.GetString("rev", "0"), th, half);
            AddField(ops, cells, bx + pad, by + rowH + rowH / 2 - th / 2, "DRAWN: " + op.GetString("drawnBy", ""), th, half);
            AddField(ops, cells, bx + bw / 2 + pad, by + rowH + rowH / 2 - th / 2, "DATE: " + op.GetString("date", ""), th, half);
            AddField(ops, cells, bx + pad, by + rowH / 2 - th / 2, "SCALE: " + op.GetString("scale", "1:1"), th, half);
            AddField(ops, cells, bx + bw / 2 + pad, by + rowH / 2 - th / 2, "SHEET: " + op.GetString("sheet", "1 of 1"), th, half);

            ops.Add(Ob.Op("line").P("start", bx + bw / 2, by).P("end", bx + bw / 2, by + rowH * 3)
                      .S("layer", Layers.Outline));

            int first = ctx.Created.Count;
            ctx.RunOps(ops);
            FitFieldsToCells(ctx, first, cells);

            // The drawing area and the block are recorded so the review pass can
            // tell a label placed in the drawing from one lying over the sheet
            // furniture, and so it knows which entities are the furniture.
            ctx.HasSheet = true;
            ctx.SheetInner = Box(ctx, ox + margin, oy + margin, sw - 2 * margin, sh - 2 * margin);
            ctx.TitleBlockArea = Box(ctx, bx, by, bw, bh);
            ctx.SheetOwnFrom = first;
            ctx.SheetOwnTo = ctx.Created.Count;
        }

        private static Autodesk.AutoCAD.DatabaseServices.Extents3d Box(
            DrawContext ctx, double x, double y, double w, double h)
        {
            // Plan coordinates are relative; the origin is what puts them on the
            // sheet, and the review compares against entities already placed.
            Point3d min = new Point3d(x + ctx.Origin.X, y + ctx.Origin.Y, 0);
            Point3d max = new Point3d(x + w + ctx.Origin.X, y + h + ctx.Origin.Y, 0);
            return new Autodesk.AutoCAD.DatabaseServices.Extents3d(min, max);
        }

        private static double Clamp(double value, double low, double high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        /// <summary>Writes one field, shrunk or trimmed to stay inside its cell.</summary>
        private static void AddField(List<JsonValue> ops, List<double> cells, double x, double y,
                                     string value, double height, double available)
        {
            string text;
            double used;
            TextFit.Fit(value, height, available, out text, out used);

            if (string.IsNullOrEmpty(text)) return;
            ops.Add(Ob.Op("text").P("position", x, y).S("value", text).N("height", used).S("layer", Layers.Text));
            cells.Add(available);
        }

        /// <summary>
        /// Shrinks any field that still overruns its cell, measured rather than
        /// estimated.
        ///
        /// TextFit works from an assumed character width, which is only ever an
        /// approximation - the drawing's text style decides the real one, and a
        /// wide style let a long title run straight out of the title block and
        /// off the sheet. Here the text exists, so it can simply be asked how
        /// wide it is. Width scales linearly with height, so one pass is exact.
        /// </summary>
        private static void FitFieldsToCells(DrawContext ctx, int first, List<double> cells)
        {
            int index = 0;

            for (int i = first; i < ctx.Created.Count && index < cells.Count; i++)
            {
                Autodesk.AutoCAD.DatabaseServices.DBText text =
                    ctx.Tr.GetObject(ctx.Created[i],
                                     Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                    as Autodesk.AutoCAD.DatabaseServices.DBText;
                if (text == null) continue;

                double allowed = cells[index++];
                if (allowed <= 0) continue;

                double actual;
                try
                {
                    Autodesk.AutoCAD.DatabaseServices.Extents3d box = text.GeometricExtents;
                    actual = box.MaxPoint.X - box.MinPoint.X;
                }
                catch (Exception)
                {
                    continue;
                }

                if (actual <= allowed || actual <= 0) continue;

                double height = text.Height;
                double shrunk = height * (allowed / actual);
                text.UpgradeOpen();

                if (shrunk >= TextFit.MinHeight)
                {
                    text.Height = shrunk;
                    continue;
                }

                // Too long to shrink into the cell and stay readable: hold the
                // floor and drop the characters that do not fit. Width scales
                // with height, so the width at the floor follows from the one
                // just measured.
                text.Height = TextFit.MinHeight;

                double atFloor = actual * (TextFit.MinHeight / height);
                string value = text.TextString ?? "";
                if (atFloor > allowed && value.Length > 1)
                {
                    int keep = (int)Math.Floor(value.Length * (allowed / atFloor));
                    text.TextString = keep > 1 ? value.Substring(0, keep - 1) + "\u2026" : "";
                }
            }
        }
    }
}
