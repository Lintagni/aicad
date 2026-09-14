using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AiCad.Execution
{
    /// <summary>
    /// Looks over a finished 3D model for the two faults that are invisible in
    /// the JSON but obvious on screen: a part left floating in space, and a part
    /// buried inside another where nobody will ever see it.
    ///
    /// The model writing the plan cannot see what it built, so these warnings are
    /// the only correction it gets. That makes a false alarm expensive - it sends
    /// the next attempt off fixing something that was never wrong. Solids that
    /// meet at a joint are SUPPOSED to overlap deeply: a pivot sits inside its
    /// housing, a boom is rooted in its shoulder. So overlap on its own is not
    /// reported; only a part swallowed almost whole, which is wasted geometry
    /// however it got there.
    /// </summary>
    public static class ModelReview
    {
        /// <summary>Swallowed this completely and the part may as well not exist.</summary>
        private const double BuriedShare = 0.9;

        /// <summary>A gap this fraction of the model's own size counts as detached.</summary>
        private const double DetachedFraction = 0.01;

        /// <summary>Pairwise cost grows as the square, so stop before it bites.</summary>
        private const int MaxSolids = 60;

        private const int MaxReports = 6;

        public static void Check(DrawContext ctx)
        {
            List<Solid3d> solids = new List<Solid3d>();
            List<Extents3d> boxes = new List<Extents3d>();
            List<double> volumes = new List<double>();

            try { Collect(ctx, solids, boxes, volumes); }
            catch (Exception) { return; }

            if (solids.Count < 2 || solids.Count > MaxSolids) return;

            int reports = 0;
            reports += ReportDetached(ctx, solids, boxes, MaxReports);
            ReportBuried(ctx, solids, boxes, volumes, MaxReports - reports);
        }

        // ------------------------------------------------------------ detached

        /// <summary>
        /// A part touching nothing is nearly always a mistake - teeth left beside
        /// the bucket, a bolt head hanging in space. Bounding boxes are enough to
        /// find it and cost nothing.
        /// </summary>
        private static int ReportDetached(DrawContext ctx, List<Solid3d> solids,
                                          List<Extents3d> boxes, int budget)
        {
            if (budget <= 0) return 0;

            double tolerance = ModelSize(boxes) * DetachedFraction;
            if (tolerance < 1e-6) return 0;

            int reported = 0;
            for (int i = 0; i < solids.Count && reported < budget; i++)
            {
                double nearest = double.MaxValue;
                for (int j = 0; j < solids.Count; j++)
                {
                    if (i == j) continue;
                    double gap = Gap(boxes[i], boxes[j]);
                    if (gap < nearest) nearest = gap;
                    if (nearest <= tolerance) break;
                }

                if (nearest <= tolerance || nearest == double.MaxValue) continue;

                ctx.Warn(Describe(solids[i], boxes[i]) + " is not joined to anything: the " +
                         "nearest part is " + Round(nearest) + " away. Every part must touch " +
                         "the one it is mounted on, or it floats in mid-air.");
                reported++;
            }
            return reported;
        }

        // -------------------------------------------------------------- buried

        private static void ReportBuried(DrawContext ctx, List<Solid3d> solids,
                                         List<Extents3d> boxes, List<double> volumes, int budget)
        {
            if (budget <= 0) return;

            int reported = 0;
            for (int i = 0; i < solids.Count && reported < budget; i++)
            {
                for (int j = i + 1; j < solids.Count && reported < budget; j++)
                {
                    if (!BoxesOverlap(boxes[i], boxes[j])) continue;

                    double shared = SharedVolume(solids[i], solids[j]);
                    if (shared <= 0) continue;

                    int small = volumes[i] <= volumes[j] ? i : j;
                    int large = small == i ? j : i;
                    if (volumes[small] <= 0) continue;
                    if (shared / volumes[small] < BuriedShare) continue;

                    ctx.Warn(Describe(solids[small], boxes[small]) + " is almost entirely inside " +
                             Describe(solids[large], boxes[large]) + ", so none of it can be " +
                             "seen. Either move it out to where it belongs or leave it out.");
                    reported++;
                }
            }
        }

        // ------------------------------------------------------------- helpers

        private static void Collect(DrawContext ctx, List<Solid3d> solids,
                                    List<Extents3d> boxes, List<double> volumes)
        {
            for (int i = 0; i < ctx.Created.Count; i++)
            {
                Solid3d solid = ctx.Tr.GetObject(ctx.Created[i], OpenMode.ForRead) as Solid3d;
                if (solid == null) continue;

                try
                {
                    Extents3d box = solid.GeometricExtents;
                    double volume = solid.MassProperties.Volume;
                    if (volume <= 0) continue;

                    solids.Add(solid);
                    boxes.Add(box);
                    volumes.Add(volume);
                }
                catch (Exception)
                {
                    // A solid that will not report its extents or volume is left out.
                }
            }
        }

        /// <summary>The diagonal of everything drawn, used to scale the tolerance.</summary>
        private static double ModelSize(List<Extents3d> boxes)
        {
            if (boxes.Count == 0) return 0;

            Point3d min = boxes[0].MinPoint;
            Point3d max = boxes[0].MaxPoint;
            for (int i = 1; i < boxes.Count; i++)
            {
                min = new Point3d(Math.Min(min.X, boxes[i].MinPoint.X),
                                  Math.Min(min.Y, boxes[i].MinPoint.Y),
                                  Math.Min(min.Z, boxes[i].MinPoint.Z));
                max = new Point3d(Math.Max(max.X, boxes[i].MaxPoint.X),
                                  Math.Max(max.Y, boxes[i].MaxPoint.Y),
                                  Math.Max(max.Z, boxes[i].MaxPoint.Z));
            }
            return min.DistanceTo(max);
        }

        /// <summary>Shortest distance between two boxes; 0 when they touch or overlap.</summary>
        private static double Gap(Extents3d a, Extents3d b)
        {
            double dx = AxisGap(a.MinPoint.X, a.MaxPoint.X, b.MinPoint.X, b.MaxPoint.X);
            double dy = AxisGap(a.MinPoint.Y, a.MaxPoint.Y, b.MinPoint.Y, b.MaxPoint.Y);
            double dz = AxisGap(a.MinPoint.Z, a.MaxPoint.Z, b.MinPoint.Z, b.MaxPoint.Z);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double AxisGap(double aMin, double aMax, double bMin, double bMax)
        {
            if (bMin > aMax) return bMin - aMax;
            if (aMin > bMax) return aMin - bMax;
            return 0;
        }

        private static bool BoxesOverlap(Extents3d a, Extents3d b)
        {
            return a.MinPoint.X < b.MaxPoint.X && b.MinPoint.X < a.MaxPoint.X
                && a.MinPoint.Y < b.MaxPoint.Y && b.MinPoint.Y < a.MaxPoint.Y
                && a.MinPoint.Z < b.MaxPoint.Z && b.MinPoint.Z < a.MaxPoint.Z;
        }

        /// <summary>
        /// The volume the two solids share. Both are copied first: the boolean
        /// that measures the overlap would otherwise consume the originals.
        /// </summary>
        private static double SharedVolume(Solid3d a, Solid3d b)
        {
            Solid3d left = null;
            Solid3d right = null;
            try
            {
                left = (Solid3d)a.Clone();
                right = (Solid3d)b.Clone();
                left.BooleanOperation(BooleanOperationType.BoolIntersect, right);
                right = null;   // consumed by the boolean
                return left.MassProperties.Volume;
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                if (left != null) left.Dispose();
                if (right != null) right.Dispose();
            }
        }

        /// <summary>
        /// Layer alone cannot tell three identical parts apart, so the size and
        /// position go in too - enough for the next plan to find the right one.
        /// </summary>
        private static string Describe(Solid3d solid, Extents3d box)
        {
            string layer;
            try { layer = string.IsNullOrEmpty(solid.Layer) ? "0" : solid.Layer; }
            catch (Exception) { layer = "0"; }

            Point3d min = box.MinPoint;
            Point3d max = box.MaxPoint;
            return "the " + Round(max.X - min.X) + "x" + Round(max.Y - min.Y) + "x" +
                   Round(max.Z - min.Z) + " part on layer " + layer + " at [" +
                   Round(min.X) + "," + Round(min.Y) + "," + Round(min.Z) + "]";
        }

        private static string Round(double value)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }
    }
}
