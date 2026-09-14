using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AiCad.Execution
{
    /// <summary>
    /// Automatic placement, so a request never has to be positioned by hand.
    /// New geometry is put clear of whatever is already drawn, and the view is
    /// moved to it, so results simply appear.
    /// </summary>
    public static class Placement
    {
        /// <summary>Above this, extents are taken from the database rather than walked.</summary>
        private const int MaxEntitiesToWalk = 20000;

        /// <summary>
        /// A point to the right of everything already in the current space,
        /// or the origin when the space is empty.
        /// </summary>
        public static Point3d NextFreeSpot(Document doc)
        {
            if (doc == null) return Point3d.Origin;

            Extents3d extents;
            if (!TryGetSpaceExtents(doc, out extents)) return Point3d.Origin;

            double width = extents.MaxPoint.X - extents.MinPoint.X;
            double gap = Math.Max(100.0, width * 0.10);

            // Sit on the same baseline so drawings line up in a row.
            return new Point3d(extents.MaxPoint.X + gap, extents.MinPoint.Y, 0);
        }

        /// <summary>Union of the geometric extents of everything in the current space.</summary>
        public static bool TryGetSpaceExtents(Document doc, out Extents3d extents)
        {
            extents = new Extents3d();
            bool found = false;

            Database db = doc.Database;
            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space =
                        (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                    int count = 0;
                    foreach (ObjectId id in space)
                    {
                        if (++count > MaxEntitiesToWalk)
                        {
                            // Too big to walk; fall back to the stored extents.
                            tr.Commit();
                            return TryGetDatabaseExtents(db, out extents);
                        }

                        Entity entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;

                        try
                        {
                            Extents3d one = entity.GeometricExtents;
                            if (!found) { extents = one; found = true; }
                            else extents.AddExtents(one);
                        }
                        catch (Exception)
                        {
                            // Empty text and a few other entities have no extents.
                        }
                    }

                    tr.Commit();
                }
            }
            catch (Exception)
            {
                return false;
            }

            return found && IsSane(extents);
        }

        private static bool TryGetDatabaseExtents(Database db, out Extents3d extents)
        {
            extents = new Extents3d();
            try
            {
                Point3d min = db.Extmin;
                Point3d max = db.Extmax;
                if (min.X > max.X || min.Y > max.Y) return false;
                extents = new Extents3d(min, max);
                return IsSane(extents);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// An empty drawing leaves Extmin/Extmax at +/-1e20, which would throw
        /// the placement far off into space.
        /// </summary>
        private static bool IsSane(Extents3d extents)
        {
            double limit = 1.0e12;
            return Math.Abs(extents.MinPoint.X) < limit && Math.Abs(extents.MinPoint.Y) < limit &&
                   Math.Abs(extents.MaxPoint.X) < limit && Math.Abs(extents.MaxPoint.Y) < limit;
        }

        /// <summary>Moves the view onto the given entities, so results are visible at once.</summary>
        public static void ZoomTo(Document doc, List<ObjectId> ids)
        {
            if (doc == null || ids == null || ids.Count == 0) return;

            try
            {
                Extents3d extents = new Extents3d();
                bool found = false;

                Database db = doc.Database;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        if (ids[i].IsNull || ids[i].IsErased) continue;
                        Entity entity = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        try
                        {
                            Extents3d one = entity.GeometricExtents;
                            if (!found) { extents = one; found = true; }
                            else extents.AddExtents(one);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    tr.Commit();
                }

                if (!found || !IsSane(extents)) return;
                ApplyView(doc.Editor, extents);
            }
            catch (Exception)
            {
                // Zooming is a convenience; never fail a successful draw over it.
            }
        }

        private static void ApplyView(Editor ed, Extents3d extents)
        {
            double width = extents.MaxPoint.X - extents.MinPoint.X;
            double height = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (width <= 0 || height <= 0) return;

            using (ViewTableRecord view = ed.GetCurrentView())
            {
                // Preserve the viewport's aspect ratio, with a little margin.
                double aspect = view.Height > 0 ? view.Width / view.Height : 1.0;
                double margin = 1.15;
                double newWidth = width * margin;
                double newHeight = height * margin;

                if (newWidth / newHeight > aspect) newHeight = newWidth / aspect;
                else newWidth = newHeight * aspect;

                view.CenterPoint = new Point2d(
                    (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0);
                view.Width = newWidth;
                view.Height = newHeight;
                ed.SetCurrentView(view);
            }
        }
    }
}
