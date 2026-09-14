using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AiCad.Execution
{
    /// <summary>
    /// Looks over a finished 2D drawing for the faults that make one unusable
    /// but are invisible in the plan: labels written on top of each other,
    /// geometry lying across the title block, anything outside the border, and a
    /// drawing left as a speck in the middle of a sheet sized for something much
    /// bigger.
    ///
    /// The model cannot see what it drew, so these warnings are its only
    /// correction - the same bargain <see cref="ModelReview"/> makes for solids.
    /// That also makes a false alarm expensive: it sends the next attempt off
    /// fixing something that was never wrong. So every check here is one where
    /// the fault is unambiguous. Text sitting on a wire is normal draughting and
    /// is not reported; text sitting on other text never is.
    /// </summary>
    public static class SheetReview
    {
        /// <summary>
        /// Two labels must share more than this share of the smaller one before
        /// it counts. Glyph extents are approximate, and descenders on adjacent
        /// rows of a table graze each other without anyone minding.
        /// </summary>
        private const double OverlapShare = 0.18;

        /// <summary>Pairwise cost grows as the square, so stop before it bites.</summary>
        private const int MaxTexts = 400;

        /// <summary>
        /// Below this share of the border, the drawing is a speck on the sheet.
        /// Generous, because a schematic legitimately leaves white space and
        /// only a drawing that is plainly the wrong size should be reported.
        /// </summary>
        private const double MinSheetFill = 0.18;

        private const int MaxReports = 6;

        public static void Check(DrawContext ctx)
        {
            try { Review(ctx); }
            catch (Exception)
            {
                // A review that cannot run must never fail the drawing: the
                // geometry is already correct or not, and this only comments.
            }
        }

        private static void Review(DrawContext ctx)
        {
            List<DBText> texts = new List<DBText>();
            List<Extents3d> textBoxes = new List<Extents3d>();
            List<Extents3d> drawn = new List<Extents3d>();
            List<Extents3d> symbols = new List<Extents3d>();

            Collect(ctx, texts, textBoxes, drawn, symbols);

            int reports = 0;
            reports += ReportOverlappingText(ctx, texts, textBoxes, MaxReports);
            reports += ReportTextTooSmall(ctx, texts, textBoxes, drawn, MaxReports - reports);
            reports += ReportSymbolsTooSmall(ctx, symbols, drawn, MaxReports - reports);
            reports += ReportUntaggedSymbols(ctx, MaxReports - reports);
            reports += ReportDanglingWires(ctx, drawn, MaxReports - reports);

            if (ctx.HasSheet)
            {
                reports += ReportOverTitleBlock(ctx, texts, textBoxes, drawn, MaxReports - reports);
                reports += ReportOutsideBorder(ctx, texts, textBoxes, MaxReports - reports);
                ReportSheetTooBig(ctx, drawn, MaxReports - reports);
            }
        }

        // ------------------------------------------------------ spread too thin

        /// <summary>
        /// Text smaller than this share of the drawing's width is unreadable once
        /// the view zooms to the whole thing. The guidance asks for about 1/100;
        /// this only complains at well past that, so ordinary variation passes.
        /// </summary>
        private const double MinTextShare = 1.0 / 260.0;

        /// <summary>
        /// Catches the drawing that is correct but spread so wide that everything
        /// on it is a speck. Absolute sizes cannot show this - only the ratio of
        /// the lettering to the overall extents, which is what the eye sees after
        /// the view is zoomed to the drawing.
        /// </summary>
        private static int ReportTextTooSmall(DrawContext ctx, List<DBText> texts,
                                              List<Extents3d> boxes, List<Extents3d> drawn,
                                              int budget)
        {
            if (budget <= 0 || texts.Count == 0 || drawn.Count == 0) return 0;

            Extents3d used = drawn[0];
            for (int i = 1; i < drawn.Count; i++) used = Merge(used, drawn[i]);

            double span = Math.Max(Width(used), Height(used));
            if (span <= 0) return 0;

            // The tallest label: if even that is lost, the drawing is too spread.
            double tallest = 0;
            for (int i = 0; i < boxes.Count; i++)
                tallest = Math.Max(tallest, Height(boxes[i]));

            if (tallest <= 0 || tallest / span >= MinTextShare) return 0;

            double wanted = span * (1.0 / 100.0);
            ctx.Warn("Everything is too small to read: the drawing spans " + Round(span) +
                     " units but the largest label is only " + Round2(tallest) +
                     " tall, about 1/" + Round(span / tallest) + " of it. Either draw the same " +
                     "content over a much smaller area - a schematic should span roughly 50 " +
                     "symbol-heights, not " + Round(span / Math.Max(1.0, tallest * 4)) +
                     " - or raise every symbol scale and text height together (text about " +
                     Round2(wanted) + " for this width).");
            return 1;
        }

        // ------------------------------------------------------ text on text

        /// <summary>
        /// Two labels in the same place is always a mistake and is the single
        /// most common way one of these drawings comes out unreadable.
        /// </summary>
        private static int ReportOverlappingText(DrawContext ctx, List<DBText> texts,
                                                 List<Extents3d> boxes, int budget)
        {
            if (budget <= 0 || texts.Count > MaxTexts) return 0;

            int reported = 0;
            for (int i = 0; i < texts.Count && reported < budget; i++)
            {
                for (int j = i + 1; j < texts.Count && reported < budget; j++)
                {
                    double shared = SharedArea(boxes[i], boxes[j]);
                    if (shared <= 0) continue;

                    double smaller = Math.Min(Area(boxes[i]), Area(boxes[j]));
                    if (smaller <= 0 || shared / smaller < OverlapShare) continue;

                    ctx.Warn("The labels " + Quote(texts[i]) + " and " + Quote(texts[j]) +
                             " are written on top of each other near " + At(boxes[i]) +
                             ". Space the items further apart, shorten the labels, or set " +
                             "\"rotation\": 90 so they read vertically.");
                    reported++;
                }
            }
            return reported;
        }

        /// <summary>
        /// Device symbols smaller than this share of the drawing read as specks.
        /// The guidance asks for about 1/15 of a rung; this fires only well past
        /// that, so a legitimately busy schematic is not nagged.
        /// </summary>
        private const double MinSymbolShare = 1.0 / 45.0;

        /// <summary>
        /// The commonest way one of these comes out unreadable: the geometry and
        /// the tags are right, but every symbol was left at scale 1 and spread
        /// across a ladder ten times too wide.
        /// </summary>
        private static int ReportSymbolsTooSmall(DrawContext ctx, List<Extents3d> symbols,
                                                 List<Extents3d> drawn, int budget)
        {
            if (budget <= 0 || symbols.Count == 0 || drawn.Count == 0) return 0;

            Extents3d used = drawn[0];
            for (int i = 1; i < drawn.Count; i++) used = Merge(used, drawn[i]);

            double span = Math.Max(Width(used), Height(used));
            if (span <= 0) return 0;

            // The biggest symbol: if even that is lost, they all are.
            double tallest = 0;
            for (int i = 0; i < symbols.Count; i++)
                tallest = Math.Max(tallest, Height(symbols[i]));

            if (tallest <= 0 || tallest / span >= MinSymbolShare) return 0;

            // Advice has to be a MULTIPLIER of whatever scale was used, not an
            // absolute scale: how big a block is at scale 1 varies by library,
            // and quoting a fixed number told the model to use the scale it had
            // already chosen.
            double wanted = span / 15.0;
            double times = wanted / tallest;

            ctx.Warn("The symbols are far too small to see: the drawing spans " + Round(span) +
                     " units but the largest symbol is only " + Round2(tallest) + " tall, about 1/" +
                     Round(span / tallest) + " of it. MULTIPLY the \"scale\" of every symbol by " +
                     "about " + Round(times) + " - so a symbol currently at scale 1 becomes scale " +
                     Round(times) + " - to make them roughly " + Round(wanted) + " units tall. " +
                     "Scaling a symbol moves its terminals outwards by the same factor, so move " +
                     "the wire endpoints and device spacing to match, or the circuit will come " +
                     "apart. Alternatively draw the same circuit over a much smaller area.");
            return 1;
        }

        /// <summary>
        /// A schematic whose devices carry no tags cannot be wired from, read in
        /// a panel, or cross-referenced - it is a picture of a circuit rather
        /// than a drawing. Reported once for the whole plan, and only when most
        /// symbols are bare, because the odd untagged terminal or rail is normal.
        /// </summary>
        private static int ReportUntaggedSymbols(DrawContext ctx, int budget)
        {
            if (budget <= 0) return 0;

            int total = 0;
            int tagged = 0;

            for (int i = 0; i < ctx.Created.Count; i++)
            {
                BlockReference block =
                    ctx.Tr.GetObject(ctx.Created[i], OpenMode.ForRead) as BlockReference;
                if (block == null) continue;

                total++;
                if (HasAnyAttribute(ctx, block)) tagged++;
            }

            // Too few symbols to judge, or enough of them are tagged already.
            if (total < 3 || tagged * 2 > total) return 0;

            ctx.Warn(total.ToString(CultureInfo.InvariantCulture) + " symbols were placed but " +
                     (total - tagged).ToString(CultureInfo.InvariantCulture) + " carry no tag, so " +
                     "the drawing does not say what any device is. Give every \"symbol\" op an " +
                     "\"attributes\" entry - for example {\"TAG1\":\"KM1\",\"DESC1\":\"MAIN " +
                     "CONTACTOR\"} - or label it with a \"text\" op beside the symbol.");
            return 1;
        }

        private static bool HasAnyAttribute(DrawContext ctx, BlockReference block)
        {
            try
            {
                foreach (ObjectId id in block.AttributeCollection)
                {
                    AttributeReference attribute =
                        ctx.Tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                    if (attribute == null) continue;
                    if (!string.IsNullOrEmpty((attribute.TextString ?? "").Trim())) return true;
                }
            }
            catch (Exception)
            {
                // Unreadable attributes are not evidence of anything; say nothing.
                return true;
            }
            return false;
        }

        /// <summary>
        /// Report only when a large share of wire ends lead nowhere. A ladder
        /// legitimately has a few open ends - the tops and bottoms of the rails -
        /// so a couple of loose endpoints mean nothing, while most of them loose
        /// means the circuit was drawn to the wrong size and came apart.
        /// </summary>
        private const double DanglingShare = 0.25;

        /// <summary>
        /// Wire endpoints that touch nothing. This is what "the symbols are the
        /// right size now but nothing is connected" looks like from the outside:
        /// the symbols were scaled without moving the wires to their terminals.
        /// </summary>
        private static int ReportDanglingWires(DrawContext ctx, List<Extents3d> drawn, int budget)
        {
            if (budget <= 0 || drawn.Count == 0) return 0;

            Extents3d used = drawn[0];
            for (int i = 1; i < drawn.Count; i++) used = Merge(used, drawn[i]);
            double span = Math.Max(Width(used), Height(used));
            if (span <= 0) return 0;

            // Generous: a wire that stops a whisker short still reads as joined.
            double tolerance = span * 0.004;

            List<ObjectId> ids = ctx.Created;
            List<Point3d> loose = new List<Point3d>();
            int endpoints = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                Line line = ctx.Tr.GetObject(ids[i], OpenMode.ForRead) as Line;
                if (line == null) continue;

                Point3d[] ends = new Point3d[] { line.StartPoint, line.EndPoint };
                for (int e = 0; e < ends.Length; e++)
                {
                    endpoints++;
                    if (!TouchesAnything(ctx, ids, i, ends[e], tolerance)) loose.Add(ends[e]);
                }
            }

            if (endpoints < 8) return 0;
            if (loose.Count < 2 || loose.Count < endpoints * DanglingShare) return 0;

            StringBuilder where = new StringBuilder();
            for (int i = 0; i < loose.Count && i < 4; i++)
            {
                if (i > 0) where.Append(", ");
                where.Append(Corner(loose[i]));
            }
            if (loose.Count > 4) where.Append(" and more");

            ctx.Warn(loose.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                     endpoints.ToString(CultureInfo.InvariantCulture) + " wire ends stop in empty " +
                     "space, so the circuit is not continuous - for example at " + where +
                     ". Every wire must finish exactly on a symbol terminal or on another wire. " +
                     "If you changed a symbol's \"scale\", its terminals moved with it: recompute " +
                     "the wire endpoints from the scaled symbol instead of leaving them where " +
                     "they were.");
            return 1;
        }

        /// <summary>True when the point falls inside any other created entity's box.</summary>
        private static bool TouchesAnything(DrawContext ctx, List<ObjectId> ids, int self,
                                            Point3d point, double tolerance)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (i == self) continue;

                Entity other = ctx.Tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                if (other == null) continue;

                Extents3d box;
                try { box = other.GeometricExtents; }
                catch (Exception) { continue; }

                if (point.X >= box.MinPoint.X - tolerance && point.X <= box.MaxPoint.X + tolerance &&
                    point.Y >= box.MinPoint.Y - tolerance && point.Y <= box.MaxPoint.Y + tolerance)
                    return true;
            }
            return false;
        }

        // -------------------------------------------------- over the furniture

        /// <summary>
        /// Anything of the drawing's own sitting on the title block.
        ///
        /// This used to look at text alone, so a busbar, a wire or a symbol run
        /// straight through the title block went unreported - which is most of
        /// what actually overlaps it. Everything the plan drew is checked now,
        /// and reported as one count rather than one complaint per line.
        /// </summary>
        private static int ReportOverTitleBlock(DrawContext ctx, List<DBText> texts,
                                                List<Extents3d> boxes, List<Extents3d> drawn,
                                                int budget)
        {
            if (budget <= 0) return 0;

            int over = 0;
            for (int i = 0; i < drawn.Count; i++)
                if (SharedArea(drawn[i], ctx.TitleBlockArea) > 0) over++;

            if (over == 0) return 0;

            // Naming something that overlaps makes the fault findable.
            string example = null;
            for (int i = 0; i < texts.Count; i++)
            {
                if (IsSheetFurniture(ctx, texts[i])) continue;
                if (SharedArea(boxes[i], ctx.TitleBlockArea) <= 0) continue;
                example = Quote(texts[i]);
                break;
            }

            ctx.Warn(over.ToString(CultureInfo.InvariantCulture) +
                     " parts of the drawing lie across the title block, which occupies " +
                     Corner(ctx.TitleBlockArea.MinPoint) + " to " +
                     Corner(ctx.TitleBlockArea.MaxPoint) +
                     (example != null ? " - " + example + " among them" : "") +
                     ". That area is reserved. Move the drawing left and up so it clears the " +
                     "title block, or enlarge the sheet, keeping everything inside the border.");
            return 1;
        }

        private static int ReportOutsideBorder(DrawContext ctx, List<DBText> texts,
                                               List<Extents3d> boxes, int budget)
        {
            if (budget <= 0) return 0;

            int reported = 0;
            for (int i = 0; i < texts.Count && reported < budget; i++)
            {
                if (IsSheetFurniture(ctx, texts[i])) continue;
                if (Contains(ctx.SheetInner, boxes[i])) continue;

                ctx.Warn("The label " + Quote(texts[i]) + " at " + At(boxes[i]) +
                         " falls outside the border, which runs from " +
                         Corner(ctx.SheetInner.MinPoint) + " to " + Corner(ctx.SheetInner.MaxPoint) +
                         ". Everything must sit inside it.");
                reported++;
            }
            return reported;
        }

        // ---------------------------------------------------- sheet too large

        /// <summary>
        /// A correct drawing on a sheet three sizes too big reads as a tiny
        /// diagram beside an enormous title block. The fix is the sheet, not the
        /// drawing, so that is what the warning says.
        /// </summary>
        private static void ReportSheetTooBig(DrawContext ctx, List<Extents3d> drawn, int budget)
        {
            if (budget <= 0 || drawn.Count == 0) return;

            double sheet = Area(ctx.SheetInner);
            if (sheet <= 0) return;

            Extents3d used = drawn[0];
            for (int i = 1; i < drawn.Count; i++) used = Merge(used, drawn[i]);

            double fill = Area(used) / sheet;
            if (fill >= MinSheetFill) return;

            ctx.Warn("The drawing fills only " + Percent(fill) + " of the sheet: it measures " +
                     Size(used) + " inside a border of " + Size(ctx.SheetInner) +
                     ", so it prints as a small diagram beside an oversized title block. " +
                     "Either draw the sheet to fit the drawing - border roughly " +
                     Round(Width(used) * 1.35) + " by " + Round(Height(used) * 1.35) +
                     " - or scale the drawing up to fill the one you chose.");
        }

        // ------------------------------------------------------------ helpers

        private static void Collect(DrawContext ctx, List<DBText> texts,
                                    List<Extents3d> boxes, List<Extents3d> drawn,
                                    List<Extents3d> symbols)
        {
            for (int i = 0; i < ctx.Created.Count; i++)
            {
                Entity entity = ctx.Tr.GetObject(ctx.Created[i], OpenMode.ForRead) as Entity;
                if (entity == null) continue;

                Extents3d box;
                try { box = entity.GeometricExtents; }
                catch (Exception) { continue; }

                DBText text = entity as DBText;
                if (text != null && !string.IsNullOrEmpty(text.TextString))
                {
                    texts.Add(text);
                    boxes.Add(box);
                }

                // Device symbols arrive as block references, whatever library
                // they came from, so their size can be judged the same way.
                if (entity is BlockReference) symbols.Add(box);

                // The border and title block would otherwise count as the
                // drawing filling the sheet, which is exactly what is in
                // question, so they are left out of the extent.
                if (!IsSheetFurnitureIndex(ctx, i)) drawn.Add(box);
            }
        }

        private static bool IsSheetFurnitureIndex(DrawContext ctx, int index)
        {
            return ctx.SheetOwnFrom >= 0 && index >= ctx.SheetOwnFrom && index < ctx.SheetOwnTo;
        }

        private static bool IsSheetFurniture(DrawContext ctx, Entity entity)
        {
            if (ctx.SheetOwnFrom < 0) return false;
            for (int i = ctx.SheetOwnFrom; i < ctx.SheetOwnTo && i < ctx.Created.Count; i++)
                if (ctx.Created[i] == entity.ObjectId) return true;
            return false;
        }

        private static double SharedArea(Extents3d a, Extents3d b)
        {
            double dx = Overlap(a.MinPoint.X, a.MaxPoint.X, b.MinPoint.X, b.MaxPoint.X);
            double dy = Overlap(a.MinPoint.Y, a.MaxPoint.Y, b.MinPoint.Y, b.MaxPoint.Y);
            return dx <= 0 || dy <= 0 ? 0 : dx * dy;
        }

        private static double Overlap(double aMin, double aMax, double bMin, double bMax)
        {
            double lo = Math.Max(aMin, bMin);
            double hi = Math.Min(aMax, bMax);
            return hi - lo;
        }

        /// <summary>A hair of tolerance: a label touching the border is not outside it.</summary>
        private static bool Contains(Extents3d outer, Extents3d inner)
        {
            double slack = Math.Max(Width(outer), Height(outer)) * 0.002;
            return inner.MinPoint.X >= outer.MinPoint.X - slack
                && inner.MinPoint.Y >= outer.MinPoint.Y - slack
                && inner.MaxPoint.X <= outer.MaxPoint.X + slack
                && inner.MaxPoint.Y <= outer.MaxPoint.Y + slack;
        }

        private static Extents3d Merge(Extents3d a, Extents3d b)
        {
            return new Extents3d(
                new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X),
                            Math.Min(a.MinPoint.Y, b.MinPoint.Y), 0),
                new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X),
                            Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), 0));
        }

        private static double Width(Extents3d e) { return e.MaxPoint.X - e.MinPoint.X; }
        private static double Height(Extents3d e) { return e.MaxPoint.Y - e.MinPoint.Y; }
        private static double Area(Extents3d e) { return Math.Max(0, Width(e)) * Math.Max(0, Height(e)); }

        /// <summary>Enough of the label to recognise, without pasting an essay into the warning.</summary>
        private static string Quote(DBText text)
        {
            string value = (text.TextString ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length > 28) value = value.Substring(0, 27) + "…";
            return "\"" + value + "\"";
        }

        private static string At(Extents3d box)
        {
            return Corner(new Point3d((box.MinPoint.X + box.MaxPoint.X) / 2,
                                      (box.MinPoint.Y + box.MaxPoint.Y) / 2, 0));
        }

        private static string Corner(Point3d p)
        {
            return "[" + Round(p.X) + "," + Round(p.Y) + "]";
        }

        private static string Size(Extents3d e)
        {
            return Round(Width(e)) + " by " + Round(Height(e));
        }

        private static string Percent(double fraction)
        {
            return Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static string Round(double value)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>For sizes under a unit, where rounding to whole numbers says nothing.</summary>
        private static string Round2(double value)
        {
            return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
        }
    }
}
