using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AiCad.Ai;

namespace AiCad.Execution
{
    /// <summary>
    /// Reads the facts about the open drawing that the model needs: which
    /// layers exist and, critically, which blocks it is allowed to insert.
    /// </summary>
    public static class DrawingInfo
    {
        private const int MaxLayers = 60;
        private const int MaxBlocks = 80;

        public static DrawingSnapshot Capture(Document doc)
        {
            DrawingSnapshot snapshot = new DrawingSnapshot();
            if (doc == null) return snapshot;

            try
            {
                snapshot.FileName = string.IsNullOrEmpty(doc.Name) ? "(unsaved)" : Path.GetFileName(doc.Name);
            }
            catch (Exception)
            {
                snapshot.FileName = "(unsaved)";
            }

            Database db = doc.Database;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    snapshot.SpaceName = db.TileMode ? "Model" : "Paper";
                    snapshot.UnitsDescription = DescribeUnits(db.Insunits);

                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    List<string> layers = new List<string>();
                    foreach (ObjectId id in lt)
                    {
                        if (layers.Count >= MaxLayers) break;
                        LayerTableRecord ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr != null) layers.Add(ltr.Name);
                    }
                    snapshot.LayerList = string.Join(", ", layers.ToArray());

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    List<string> blocks = new List<string>();
                    foreach (ObjectId id in bt)
                    {
                        if (blocks.Count >= MaxBlocks) break;
                        BlockTableRecord btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                        if (btr == null) continue;
                        // Layouts and anonymous *U / *D blocks are not insertable by name.
                        if (btr.IsLayout || btr.IsAnonymous) continue;
                        if (btr.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                        blocks.Add(btr.Name);
                    }
                    snapshot.BlockList = string.Join(", ", blocks.ToArray());

                    tr.Commit();
                }
                catch (Exception)
                {
                    tr.Abort();
                }
            }

            return snapshot;
        }

        /// <summary>INSUNITS as a millimetre scale hint for the prompt.</summary>
        private static string DescribeUnits(UnitsValue units)
        {
            switch (units)
            {
                case UnitsValue.Millimeters: return "1 unit = 1 mm";
                case UnitsValue.Centimeters: return "1 unit = 10 mm";
                case UnitsValue.Meters: return "1 unit = 1000 mm";
                case UnitsValue.Inches: return "1 unit = 25.4 mm";
                case UnitsValue.Feet: return "1 unit = 304.8 mm";
                case UnitsValue.Undefined: return "unset - treat 1 unit as 1 mm";
                default: return units.ToString();
            }
        }
    }
}
