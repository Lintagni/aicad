using System;
using System.Collections.Generic;
using System.Globalization;
using AiCad.Execution;
using AiCad.Json;

namespace AiCad.Generators
{
    /// <summary>
    /// Single-line diagram symbols drawn to IEC 60617, for use where the
    /// AutoCAD Electrical IEC block library is not installed.
    ///
    /// Every symbol is drawn in a local frame of height 20 at scale 1, centred
    /// on the given position, with its two terminals at the vertical extremes.
    /// That makes devices stackable: place them on a common x and join terminal
    /// to terminal with a line.
    /// </summary>
    public class IecSymbolGenerator : IOp
    {
        /// <summary>Nominal symbol height at scale 1; terminals sit at +/- half.</summary>
        public const double Height = 20.0;

        public string Name { get { return "iec"; } }

        public string Usage
        {
            get
            {
                return "iec: {op,type,position:[x,y],scale?(1),label?,rating?,wireUp?,wireDown?,layer?} " +
                       " - IEC 60617 single-line symbol. PREFER wireUp/wireDown over drawing your own " +
                       "connecting lines: each takes a Y coordinate and the symbol runs a wire from its " +
                       "own terminal to that Y, so the wire always meets the terminal exactly. " +
                       "Terminals sit HALF the symbol height above and below 'position': at " +
                       "[x, y + 10*scale] and [x, y - 10*scale]. Worked example: a symbol at [0,240] " +
                       "with scale 2 is 40 units tall and its terminals are at [0,260] and [0,220] - " +
                       "NOT [0,280] and [0,200]. " +
                       "types: breaker (CB/MCCB/MCB), isolator, switch, contactor, fuse, fuseswitch, " +
                       "transformer, motor, earth, spd, ct, lamp, meter, generator, battery. " +
                       "Stack devices on one x and join terminals with lines. " +
                       "label goes left of the symbol, rating right.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);

            string type = op.GetString("type", null);
            if (string.IsNullOrEmpty(type))
            {
                issues.Error("iec needs a 'type'");
                return;
            }
            if (!Known(type))
                issues.Error("unknown iec type '" + type + "'");

            if (op.Has("scale") && op.GetDouble("scale", 1) <= 0)
                issues.Error("iec 'scale' must be greater than zero");

            if (op.Has("wireUp") && op["wireUp"].Kind != JsonKind.Number)
                issues.Error("iec 'wireUp' must be the Y coordinate to run the wire to");
            if (op.Has("wireDown") && op["wireDown"].Kind != JsonKind.Number)
                issues.Error("iec 'wireDown' must be the Y coordinate to run the wire to");
        }

        private static bool Known(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "breaker": case "mccb": case "mcb": case "cb":
                case "isolator": case "disconnector": case "switch":
                case "contactor": case "fuse": case "fuseswitch":
                case "transformer": case "motor": case "earth":
                case "spd": case "arrester": case "ct":
                case "lamp": case "meter": case "generator": case "battery":
                    return true;
                default:
                    return false;
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue pos = op["position"];
            double x = pos.At(0).Number;
            double y = pos.At(1).Number;
            double s = op.GetDouble("scale", 1.0);
            string layer = op.GetString("layer", Layers.Outline);
            string type = op.GetString("type", "breaker").ToLowerInvariant();

            List<JsonValue> ops = new List<JsonValue>();
            switch (type)
            {
                case "breaker": case "mccb": case "mcb": case "cb":
                    Switch(ops, x, y, s, layer, true, false); break;
                case "isolator": case "disconnector": case "switch":
                    Switch(ops, x, y, s, layer, false, false); break;
                case "contactor":
                    Switch(ops, x, y, s, layer, false, true); break;
                case "fuse":
                    Fuse(ops, x, y, s, layer); break;
                case "fuseswitch":
                    Fuse(ops, x, y, s, layer);
                    Blade(ops, x, y, s, layer); break;
                case "transformer":
                    Transformer(ops, x, y, s, layer); break;
                case "motor":
                    Round(ops, x, y, s, layer, "M"); break;
                case "generator":
                    Round(ops, x, y, s, layer, "G"); break;
                case "meter":
                    Round(ops, x, y, s, layer, op.GetString("rating", "A")); break;
                case "earth":
                    Earth(ops, x, y, s, layer); break;
                case "spd": case "arrester":
                    Arrester(ops, x, y, s, layer); break;
                case "ct":
                    CurrentTransformer(ops, x, y, s, layer); break;
                case "lamp":
                    Lamp(ops, x, y, s, layer); break;
                case "battery":
                    Battery(ops, x, y, s, layer); break;
            }

            // Lead wires, drawn from the terminals this generator already knows.
            //
            // Asking the model for these coordinates does not work: it has to
            // halve the symbol height and multiply by the scale, and it gets it
            // wrong often enough that most wires miss their terminal. Here the
            // model supplies only the destination - the busbar's Y, say - which
            // it already knows, and the arithmetic happens where the geometry is.
            double half = Height / 2.0 * s;

            if (op.Has("wireUp"))
            {
                double target = op.GetDouble("wireUp", y + half);
                if (Math.Abs(target - (y + half)) > 1e-9)
                    ops.Add(Ob.Op("line").P("start", x, y + half).P("end", x, target)
                              .S("layer", layer));
            }

            if (op.Has("wireDown"))
            {
                double target = op.GetDouble("wireDown", y - half);
                if (Math.Abs(target - (y - half)) > 1e-9)
                    ops.Add(Ob.Op("line").P("start", x, y - half).P("end", x, target)
                              .S("layer", layer));
            }

            // Tag on the left, rating on the right, both clear of the symbol.
            double textHeight = Math.Max(2.0, 3.2 * s);
            string label = op.GetString("label", null);
            if (!string.IsNullOrEmpty(label))
            {
                ops.Add(Ob.Op("text").P("position", x - 8 * s, y - textHeight / 2)
                          .S("value", label).N("height", textHeight)
                          .S("justify", "right").S("layer", Layers.Text));
            }

            string rating = op.GetString("rating", null);
            if (!string.IsNullOrEmpty(rating) && type != "meter")
            {
                ops.Add(Ob.Op("text").P("position", x + 8 * s, y - textHeight / 2)
                          .S("value", rating).N("height", textHeight).S("layer", Layers.Text));
            }

            ctx.RunOps(ops);
        }

        // ------------------------------------------------------------ shapes

        /// <summary>
        /// The shared switch body: leads top and bottom, a hinged blade, and
        /// optionally the cross that marks a circuit breaker or the arc that
        /// marks a contactor.
        /// </summary>
        private static void Switch(List<JsonValue> ops, double x, double y, double s, string layer,
                                   bool breakerCross, bool contactorArc)
        {
            double half = Height * s / 2.0;
            double gap = 4 * s;

            Lead(ops, x, y - half, x, y - gap, layer);
            Lead(ops, x, y + gap, x, y + half, layer);
            Blade(ops, x, y, s, layer);

            if (breakerCross)
            {
                // The "x" at the fixed contact denotes a breaker in IEC 60617.
                double c = 1.8 * s;
                ops.Add(Ob.Op("line").P("start", x - c, y + gap - c).P("end", x + c, y + gap + c).S("layer", layer));
                ops.Add(Ob.Op("line").P("start", x - c, y + gap + c).P("end", x + c, y + gap - c).S("layer", layer));
            }

            if (contactorArc)
            {
                // Half-circle on the moving contact marks a contactor.
                ops.Add(Ob.Op("arc").P("center", x + 2.2 * s, y).N("radius", 1.8 * s)
                          .N("startAngle", 0).N("endAngle", 180).S("layer", layer));
            }
        }

        /// <summary>The hinged blade, drawn open at roughly 30 degrees.</summary>
        private static void Blade(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double gap = 4 * s;
            ops.Add(Ob.Op("line").P("start", x, y - gap).P("end", x + 4.5 * s, y + gap).S("layer", layer));
        }

        private static void Fuse(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            double w = 3.0 * s;
            double h = 6.0 * s;

            Lead(ops, x, y - half, x, y - h, layer);
            Lead(ops, x, y + h, x, y + half, layer);
            ops.Add(Ob.Op("rectangle").P("corner", x - w, y - h).N("width", w * 2).N("height", h * 2)
                      .S("layer", layer));
            // The element line through the body.
            ops.Add(Ob.Op("line").P("start", x, y - h).P("end", x, y + h).S("layer", layer));
        }

        private static void Transformer(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            double r = 5.0 * s;

            ops.Add(Ob.Op("circle").P("center", x, y - r * 0.6).N("radius", r).S("layer", layer));
            ops.Add(Ob.Op("circle").P("center", x, y + r * 0.6).N("radius", r).S("layer", layer));
            Lead(ops, x, y - half, x, y - r * 0.6 - r, layer);
            Lead(ops, x, y + r * 0.6 + r, x, y + half, layer);
        }

        private static void Round(List<JsonValue> ops, double x, double y, double s, string layer, string letter)
        {
            double half = Height * s / 2.0;
            double r = 6.0 * s;

            ops.Add(Ob.Op("circle").P("center", x, y).N("radius", r).S("layer", layer));
            Lead(ops, x, y - half, x, y - r, layer);
            Lead(ops, x, y + r, x, y + half, layer);
            ops.Add(Ob.Op("text").P("position", x, y - 2.0 * s).S("value", letter)
                      .N("height", Math.Max(2.0, 4.0 * s)).S("justify", "center").S("layer", Layers.Text));
        }

        private static void Earth(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            Lead(ops, x, y + half, x, y, layer);
            // Three bars of decreasing width.
            ops.Add(Ob.Op("line").P("start", x - 5 * s, y).P("end", x + 5 * s, y).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - 3 * s, y - 2.2 * s).P("end", x + 3 * s, y - 2.2 * s).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - 1.4 * s, y - 4.4 * s).P("end", x + 1.4 * s, y - 4.4 * s).S("layer", layer));
        }

        private static void Arrester(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            double w = 3.0 * s;
            double h = 5.5 * s;

            Lead(ops, x, y - half, x, y - h, layer);
            Lead(ops, x, y + h, x, y + half, layer);
            ops.Add(Ob.Op("rectangle").P("corner", x - w, y - h).N("width", w * 2).N("height", h * 2)
                      .S("layer", layer));
            // Arrow pointing to earth.
            ops.Add(Ob.Op("line").P("start", x, y + h * 0.6).P("end", x, y - h * 0.6).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - 1.5 * s, y - h * 0.1).P("end", x, y - h * 0.6).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x + 1.5 * s, y - h * 0.1).P("end", x, y - h * 0.6).S("layer", layer));
        }

        private static void CurrentTransformer(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            // The conductor runs straight through; the core sits beside it.
            Lead(ops, x, y - half, x, y + half, layer);
            ops.Add(Ob.Op("circle").P("center", x + 4.0 * s, y).N("radius", 3.5 * s).S("layer", layer));
        }

        private static void Lamp(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            double r = 5.0 * s;
            double d = r * 0.707;

            ops.Add(Ob.Op("circle").P("center", x, y).N("radius", r).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - d, y - d).P("end", x + d, y + d).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - d, y + d).P("end", x + d, y - d).S("layer", layer));
            Lead(ops, x, y - half, x, y - r, layer);
            Lead(ops, x, y + r, x, y + half, layer);
        }

        private static void Battery(List<JsonValue> ops, double x, double y, double s, string layer)
        {
            double half = Height * s / 2.0;
            Lead(ops, x, y - half, x, y - 2 * s, layer);
            Lead(ops, x, y + 2 * s, x, y + half, layer);
            ops.Add(Ob.Op("line").P("start", x - 4 * s, y + 2 * s).P("end", x + 4 * s, y + 2 * s).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - 2 * s, y).P("end", x + 2 * s, y).S("layer", layer));
            ops.Add(Ob.Op("line").P("start", x - 4 * s, y - 2 * s).P("end", x + 4 * s, y - 2 * s).S("layer", layer));
        }

        private static void Lead(List<JsonValue> ops, double x1, double y1, double x2, double y2, string layer)
        {
            ops.Add(Ob.Op("line").P("start", x1, y1).P("end", x2, y2).S("layer", layer));
        }
    }

    /// <summary>A busbar: a heavy horizontal run with optional tap points.</summary>
    public class BusbarGenerator : IOp
    {
        public string Name { get { return "busbar"; } }

        public string Usage
        {
            get
            {
                return "busbar: {op,start:[x,y],length,width?(1.5),taps?:[x1,x2,...],tapLength?(10)," +
                       "label?,layer?}  - heavy horizontal bar with optional drop lines at the tap x positions";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "start", true);
            issues.RequirePositive(op, "length");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue start = op["start"];
            double x = start.At(0).Number;
            double y = start.At(1).Number;
            double length = op.GetDouble("length", 200);
            double width = op.GetDouble("width", 1.5);
            double tapLength = op.GetDouble("tapLength", 10);
            string layer = op.GetString("layer", Layers.Outline);

            List<JsonValue> ops = new List<JsonValue>();

            // A wide polyline reads as a busbar rather than a plain wire.
            JsonValue points = JsonValue.NewArray();
            points.Add(Ob.Pt(x, y));
            points.Add(Ob.Pt(x + length, y));
            ops.Add(Ob.Op("polyline").Arr("points", points).N("width", width).S("layer", layer));

            List<JsonValue> taps = op.GetArray("taps");
            for (int i = 0; i < taps.Count; i++)
            {
                if (taps[i] == null || taps[i].Kind != JsonKind.Number) continue;
                double tx = taps[i].Number;
                ops.Add(Ob.Op("line").P("start", tx, y).P("end", tx, y - tapLength).S("layer", layer));
            }

            string label = op.GetString("label", null);
            if (!string.IsNullOrEmpty(label))
            {
                ops.Add(Ob.Op("text").P("position", x + length + 6, y - 2)
                          .S("value", label).N("height", Math.Max(2.5, length / 60.0))
                          .S("layer", Layers.Text));
            }

            ctx.RunOps(ops);
        }
    }
}
