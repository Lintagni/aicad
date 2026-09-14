using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AiCad.Json;

namespace AiCad.Execution
{
    /// <summary>
    /// Lets one part be positioned against another instead of by absolute
    /// coordinates.
    ///
    /// Writing a plan means emitting exact x, y and z for every part while
    /// holding the whole machine in mind, with nothing to measure against. That
    /// arithmetic is where the models go wrong: a spindle head that misses the
    /// column face by 40, a foot that ends up inside the base, teeth left in
    /// mid-air. None of those are failures of judgement - the intent was right
    /// and a subtraction was wrong.
    ///
    /// So an op may name itself with "id" and later ops may say where they sit:
    ///   {"op":"cylinder","id":"waist","on":{"part":"base","face":"top"}}
    /// The engine knows what the base actually came out as, so it can do the
    /// sum. The part cannot float, and it cannot sink in.
    /// </summary>
    public static class Mounting
    {
        public const string Help =
            "id?:\"name\" names this part; on?:{part:\"name\",face:\"top\"|\"bottom\"|\"front\"|" +
            "\"back\"|\"left\"|\"right\",offset?:[dx,dy,dz]} sits it against a named earlier part " +
            "instead of at absolute coordinates - the engine works out the position, so mounted " +
            "parts always touch";

        /// <summary>Faces a part can be mounted against.</summary>
        private static readonly string[] Faces =
            new string[] { "top", "bottom", "front", "back", "left", "right" };

        public static void Validate(OpIssues issues, JsonValue op)
        {
            JsonValue on = op["on"];
            if (on == null) return;

            if (on.Kind != JsonKind.Object)
            {
                issues.Error("'on' must be an object like {part:\"base\",face:\"top\"}");
                return;
            }
            if (string.IsNullOrEmpty(on.GetString("part", null)))
            {
                issues.Error("'on' needs the name of an earlier part in 'part'");
                return;
            }

            string face = on.GetString("face", "top");
            for (int i = 0; i < Faces.Length; i++)
                if (string.Equals(face, Faces[i], StringComparison.OrdinalIgnoreCase)) return;

            issues.Error("'on.face' must be one of top, bottom, front, back, left, right");
        }

        /// <summary>
        /// Moves whatever the op just drew so it rests against the named part.
        /// Called for every op, and does nothing unless "on" was given.
        /// </summary>
        public static void Apply(DrawContext ctx, JsonValue op, int firstEntity)
        {
            JsonValue on = op["on"];
            if (on == null || on.Kind != JsonKind.Object) return;

            string partName = on.GetString("part", null);
            if (string.IsNullOrEmpty(partName)) return;

            Extents3d target;
            if (!ctx.Parts.TryGetValue(partName, out target))
            {
                ctx.Warn("Cannot mount on '" + partName + "': no earlier part has that id. " +
                         "The part was left where its own coordinates put it.");
                return;
            }

            Extents3d moving;
            if (!TryExtents(ctx, firstEntity, out moving)) return;

            string face = on.GetString("face", "top");
            Point3d anchor = FaceAnchor(target, face);
            Point3d mating = MatingAnchor(moving, face);

            Vector3d shift = anchor - mating;
            JsonValue offset = on["offset"];
            if (offset != null && offset.Kind == JsonKind.Array && offset.Count >= 2)
            {
                shift = shift + new Vector3d(
                    offset.At(0).Number,
                    offset.At(1).Number,
                    offset.Count > 2 && offset.At(2).Kind == JsonKind.Number ? offset.At(2).Number : 0.0);
            }

            if (shift.Length < 1e-9) return;

            Matrix3d move = Matrix3d.Displacement(shift);
            for (int i = firstEntity; i < ctx.Created.Count; i++)
            {
                try
                {
                    Entity entity = (Entity)ctx.Tr.GetObject(ctx.Created[i], OpenMode.ForWrite);
                    entity.TransformBy(move);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>Remembers where the op's geometry ended up, under its id.</summary>
        public static void Record(DrawContext ctx, JsonValue op, int firstEntity)
        {
            string id = op.GetString("id", null);
            if (string.IsNullOrEmpty(id)) return;

            Extents3d box;
            if (!TryExtents(ctx, firstEntity, out box)) return;
            ctx.Parts[id] = box;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Combined extents of everything the op drew, or false if none.</summary>
        private static bool TryExtents(DrawContext ctx, int firstEntity, out Extents3d box)
        {
            box = new Extents3d();
            bool any = false;

            for (int i = firstEntity; i < ctx.Created.Count; i++)
            {
                try
                {
                    Entity entity = (Entity)ctx.Tr.GetObject(ctx.Created[i], OpenMode.ForRead);
                    Extents3d one = entity.GeometricExtents;
                    if (!any) { box = one; any = true; }
                    else box.AddExtents(one);
                }
                catch (Exception)
                {
                    // Entities that will not report extents simply do not count.
                }
            }
            return any;
        }

        /// <summary>The point on the reference part that the new part sits against.</summary>
        private static Point3d FaceAnchor(Extents3d box, string face)
        {
            Point3d min = box.MinPoint;
            Point3d max = box.MaxPoint;
            double cx = (min.X + max.X) / 2.0;
            double cy = (min.Y + max.Y) / 2.0;
            double cz = (min.Z + max.Z) / 2.0;

            switch (face.ToLowerInvariant())
            {
                case "bottom": return new Point3d(cx, cy, min.Z);
                case "front":  return new Point3d(cx, min.Y, cz);
                case "back":   return new Point3d(cx, max.Y, cz);
                case "left":   return new Point3d(min.X, cy, cz);
                case "right":  return new Point3d(max.X, cy, cz);
                default:       return new Point3d(cx, cy, max.Z);   // top
            }
        }

        /// <summary>
        /// The point on the new part that touches it - the opposite side, so
        /// mounting on a top face rests the new part's underside on it.
        /// </summary>
        private static Point3d MatingAnchor(Extents3d box, string face)
        {
            Point3d min = box.MinPoint;
            Point3d max = box.MaxPoint;
            double cx = (min.X + max.X) / 2.0;
            double cy = (min.Y + max.Y) / 2.0;
            double cz = (min.Z + max.Z) / 2.0;

            switch (face.ToLowerInvariant())
            {
                case "bottom": return new Point3d(cx, cy, max.Z);
                case "front":  return new Point3d(cx, max.Y, cz);
                case "back":   return new Point3d(cx, min.Y, cz);
                case "left":   return new Point3d(max.X, cy, cz);
                case "right":  return new Point3d(min.X, cy, cz);
                default:       return new Point3d(cx, cy, min.Z);   // top
            }
        }
    }
}
