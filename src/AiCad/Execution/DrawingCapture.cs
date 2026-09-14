using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AiCad.Json;

namespace AiCad.Execution
{
    /// <summary>
    /// Converts selected AutoCAD entities back into a drawing plan.
    ///
    /// This is what lets a user teach the assistant from drawings they already
    /// have, rather than having to redraw anything. The result is deliberately
    /// plain: primitives only, normalised so the lower-left of the selection
    /// sits at the origin, because an example is about proportion, layering and
    /// labelling conventions, not about where it happened to be drawn.
    /// </summary>
    public static class DrawingCapture
    {
        /// <summary>Entities above this are truncated; an example need not be exhaustive.</summary>
        public const int MaxEntities = 1200;

        public static JsonValue Capture(Database db, Transaction tr, ObjectId[] ids,
                                        string name, out int captured, out int skipped)
        {
            captured = 0;
            skipped = 0;

            // Normalise against the selection's own lower-left corner.
            double minX = double.MaxValue, minY = double.MaxValue;
            for (int i = 0; i < ids.Length; i++)
            {
                Entity e = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                if (e == null) continue;
                try
                {
                    Extents3d ext = e.GeometricExtents;
                    if (ext.MinPoint.X < minX) minX = ext.MinPoint.X;
                    if (ext.MinPoint.Y < minY) minY = ext.MinPoint.Y;
                }
                catch (Exception) { }
            }
            if (minX == double.MaxValue) { minX = 0; minY = 0; }

            JsonValue ops = JsonValue.NewArray();
            List<string> layers = new List<string>();

            for (int i = 0; i < ids.Length && captured < MaxEntities; i++)
            {
                Entity entity = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                if (entity == null) continue;

                JsonValue op = Convert(entity, minX, minY);
                if (op == null) { skipped++; continue; }

                if (!string.IsNullOrEmpty(entity.Layer))
                {
                    op["layer"] = JsonValue.New(entity.Layer);
                    if (!layers.Contains(entity.Layer)) layers.Add(entity.Layer);
                }

                ops.Add(op);
                captured++;
            }

            JsonValue plan = JsonValue.NewObject();
            plan["name"] = JsonValue.New(name ?? "Captured drawing");
            plan["notes"] = JsonValue.New("Captured from an existing drawing.");

            JsonValue layerList = JsonValue.NewArray();
            for (int i = 0; i < layers.Count; i++)
            {
                JsonValue l = JsonValue.NewObject();
                l["name"] = JsonValue.New(layers[i]);
                layerList.Add(l);
            }
            plan["layers"] = layerList;
            plan["ops"] = ops;
            return plan;
        }

        /// <summary>
        /// One entity to one op. Anything with no equivalent op returns null and
        /// is counted as skipped, so the user is told what did not survive.
        /// </summary>
        private static JsonValue Convert(Entity entity, double dx, double dy)
        {
            Line line = entity as Line;
            if (line != null)
            {
                JsonValue op = Op("line");
                op["start"] = Point(line.StartPoint, dx, dy);
                op["end"] = Point(line.EndPoint, dx, dy);
                return op;
            }

            Circle circle = entity as Circle;
            if (circle != null)
            {
                JsonValue op = Op("circle");
                op["center"] = Point(circle.Center, dx, dy);
                op["radius"] = JsonValue.New(Round(circle.Radius));
                return op;
            }

            Arc arc = entity as Arc;
            if (arc != null)
            {
                JsonValue op = Op("arc");
                op["center"] = Point(arc.Center, dx, dy);
                op["radius"] = JsonValue.New(Round(arc.Radius));
                op["startAngle"] = JsonValue.New(Round(arc.StartAngle * 180.0 / Math.PI));
                op["endAngle"] = JsonValue.New(Round(arc.EndAngle * 180.0 / Math.PI));
                return op;
            }

            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                JsonValue points = JsonValue.NewArray();
                JsonValue bulges = JsonValue.NewArray();
                bool anyBulge = false;

                for (int v = 0; v < polyline.NumberOfVertices; v++)
                {
                    Point2d p = polyline.GetPoint2dAt(v);
                    JsonValue pt = JsonValue.NewArray();
                    pt.Add(JsonValue.New(Round(p.X - dx)));
                    pt.Add(JsonValue.New(Round(p.Y - dy)));
                    points.Add(pt);

                    double bulge = polyline.GetBulgeAt(v);
                    if (Math.Abs(bulge) > 1e-9) anyBulge = true;
                    bulges.Add(JsonValue.New(Round(bulge)));
                }

                JsonValue op = Op("polyline");
                op["points"] = points;
                if (anyBulge) op["bulges"] = bulges;
                if (polyline.Closed) op["closed"] = JsonValue.New(true);
                if (polyline.ConstantWidth > 0)
                    op["width"] = JsonValue.New(Round(polyline.ConstantWidth));
                return op;
            }

            DBText text = entity as DBText;
            if (text != null)
            {
                JsonValue op = Op("text");
                Point3d position = text.HorizontalMode == TextHorizontalMode.TextLeft
                    ? text.Position : text.AlignmentPoint;
                op["position"] = Point(position, dx, dy);
                op["value"] = JsonValue.New(text.TextString);
                op["height"] = JsonValue.New(Round(text.Height));
                if (Math.Abs(text.Rotation) > 1e-9)
                    op["rotation"] = JsonValue.New(Round(text.Rotation * 180.0 / Math.PI));
                if (text.HorizontalMode == TextHorizontalMode.TextCenter)
                    op["justify"] = JsonValue.New("center");
                else if (text.HorizontalMode == TextHorizontalMode.TextRight)
                    op["justify"] = JsonValue.New("right");
                return op;
            }

            MText mtext = entity as MText;
            if (mtext != null)
            {
                JsonValue op = Op("mtext");
                op["position"] = Point(mtext.Location, dx, dy);
                op["value"] = JsonValue.New(mtext.Contents);
                op["height"] = JsonValue.New(Round(mtext.TextHeight));
                if (mtext.Width > 0) op["width"] = JsonValue.New(Round(mtext.Width));
                return op;
            }

            BlockReference block = entity as BlockReference;
            if (block != null)
            {
                JsonValue op = Op("insert");
                op["block"] = JsonValue.New(block.Name);
                op["position"] = Point(block.Position, dx, dy);
                if (Math.Abs(block.ScaleFactors.X - 1.0) > 1e-9)
                    op["scale"] = JsonValue.New(Round(block.ScaleFactors.X));
                if (Math.Abs(block.Rotation) > 1e-9)
                    op["rotation"] = JsonValue.New(Round(block.Rotation * 180.0 / Math.PI));
                return op;
            }

            RotatedDimension rotated = entity as RotatedDimension;
            if (rotated != null)
            {
                JsonValue op = Op("dimlinear");
                op["p1"] = Point(rotated.XLine1Point, dx, dy);
                op["p2"] = Point(rotated.XLine2Point, dx, dy);
                op["linePosition"] = Point(rotated.DimLinePoint, dx, dy);
                op["rotation"] = JsonValue.New(Round(rotated.Rotation * 180.0 / Math.PI));
                return op;
            }

            AlignedDimension aligned = entity as AlignedDimension;
            if (aligned != null)
            {
                JsonValue op = Op("dimaligned");
                op["p1"] = Point(aligned.XLine1Point, dx, dy);
                op["p2"] = Point(aligned.XLine2Point, dx, dy);
                op["linePosition"] = Point(aligned.DimLinePoint, dx, dy);
                return op;
            }

            Solid3d solid = entity as Solid3d;
            if (solid != null) return ConvertSolid(solid, dx, dy);

            return null;
        }

        // --------------------------------------------------------- 3D solids

        /// <summary>How close a ratio must be to count as that shape.</summary>
        private const double ShapeTolerance = 0.04;

        /// <summary>Two bounding-box sides this close are treated as equal.</summary>
        private const double SquareTolerance = 0.02;

        /// <summary>
        /// Recovers a primitive from a finished solid, or gives up.
        ///
        /// A Solid3d is a bag of faces: it does not remember that it was once a
        /// box 800 wide, and there is no general way to take one apart into the
        /// ops a plan is written from. What CAN be told reliably is whether it
        /// is still a simple primitive, because each one fills a fixed share of
        /// its own bounding box - a box fills all of it, a cylinder pi/4 of it,
        /// a sphere pi/6, a cone pi/12.
        ///
        /// Anything that matches none of those is skipped rather than flattened
        /// to its bounding box. Capturing a swept casting as a cuboid would not
        /// be a lossy record of it, it would be a wrong one - and since these
        /// examples are shown to the model as house style, it would teach the
        /// very box-stacking that makes these models look crude.
        /// </summary>
        private static JsonValue ConvertSolid(Solid3d solid, double dx, double dy)
        {
            double w, d, h, volume;
            Point3d centre;

            try
            {
                Extents3d box = solid.GeometricExtents;
                w = box.MaxPoint.X - box.MinPoint.X;
                d = box.MaxPoint.Y - box.MinPoint.Y;
                h = box.MaxPoint.Z - box.MinPoint.Z;
                volume = solid.MassProperties.Volume;
                centre = new Point3d((box.MinPoint.X + box.MaxPoint.X) / 2,
                                     (box.MinPoint.Y + box.MaxPoint.Y) / 2,
                                     (box.MinPoint.Z + box.MaxPoint.Z) / 2);
            }
            catch (Exception)
            {
                return null;
            }

            double enclosing = w * d * h;
            if (enclosing <= 0 || volume <= 0) return null;

            double fill = volume / enclosing;

            if (Near(fill, 1.0))
            {
                JsonValue op = Op("box");
                op["center"] = Point3(centre, dx, dy);
                op["width"] = JsonValue.New(Round(w));
                op["depth"] = JsonValue.New(Round(d));
                op["height"] = JsonValue.New(Round(h));
                return op;
            }

            // pi/4: a cylinder standing on the axis whose two cross-section
            // sides are equal. Only the Z-aligned case is claimed - a cylinder
            // lying on its side would need a rotation this cannot recover.
            if (Near(fill, Math.PI / 4.0) && Square(w, d))
            {
                JsonValue op = Op("cylinder");
                op["center"] = Point3(centre, dx, dy);
                op["radius"] = JsonValue.New(Round(w / 2));
                op["height"] = JsonValue.New(Round(h));
                return op;
            }

            if (Near(fill, Math.PI / 6.0) && Square(w, d) && Square(w, h))
            {
                JsonValue op = Op("sphere");
                op["center"] = Point3(centre, dx, dy);
                op["radius"] = JsonValue.New(Round(w / 2));
                return op;
            }

            if (Near(fill, Math.PI / 12.0) && Square(w, d))
            {
                JsonValue op = Op("cone");
                op["center"] = Point3(centre, dx, dy);
                op["radius"] = JsonValue.New(Round(w / 2));
                op["height"] = JsonValue.New(Round(h));
                return op;
            }

            // Something shaped: a sweep, a loft, a boolean. Better recorded as
            // nothing than as a lie.
            return null;
        }

        private static bool Near(double value, double target)
        {
            return Math.Abs(value - target) <= ShapeTolerance;
        }

        private static bool Square(double a, double b)
        {
            double larger = Math.Max(Math.Abs(a), Math.Abs(b));
            if (larger <= 0) return false;
            return Math.Abs(a - b) / larger <= SquareTolerance;
        }

        private static JsonValue Point3(Point3d p, double dx, double dy)
        {
            JsonValue arr = JsonValue.NewArray();
            arr.Add(JsonValue.New(Round(p.X - dx)));
            arr.Add(JsonValue.New(Round(p.Y - dy)));
            arr.Add(JsonValue.New(Round(p.Z)));
            return arr;
        }

        private static JsonValue Op(string name)
        {
            JsonValue op = JsonValue.NewObject();
            op["op"] = JsonValue.New(name);
            return op;
        }

        private static JsonValue Point(Point3d p, double dx, double dy)
        {
            JsonValue array = JsonValue.NewArray();
            array.Add(JsonValue.New(Round(p.X - dx)));
            array.Add(JsonValue.New(Round(p.Y - dy)));
            return array;
        }

        /// <summary>Two decimals: enough for drafting, and far smaller in tokens.</summary>
        private static double Round(double value)
        {
            return Math.Round(value, 2);
        }
    }
}
