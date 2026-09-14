using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AiCad.Execution;
using AiCad.Json;

namespace AiCad.Ops
{
    /// <summary>Registers the 3D solid operations.</summary>
    public static class Solids
    {
        public static void RegisterAll(OpRegistry registry)
        {
            registry.Register(new BoxOp());
            registry.Register(new CylinderOp());
            registry.Register(new ConeOp());
            registry.Register(new SphereOp());
            registry.Register(new ExtrudeOp());
            registry.Register(new RevolveOp());
            registry.Register(new SweepOp());
            registry.Register(new LoftOp());
            registry.Register(new BooleanOp("subtract"));
            registry.Register(new BooleanOp("union"));
            registry.Register(new View3dOp());
        }

        /// <summary>
        /// Places a solid: AutoCAD builds primitives centred on the origin, so
        /// each one is rotated about its own centre and then moved into place.
        /// Rotating first is what makes jointed assemblies - a robot arm, a hinged
        /// door - straightforward to describe.
        /// </summary>
        internal static void Place(Solid3d solid, Point3d centre, JsonValue op)
        {
            double rx = DrawContext.Deg2Rad(op.GetDouble("rotateX", 0));
            double ry = DrawContext.Deg2Rad(op.GetDouble("rotateY", 0));
            double rz = DrawContext.Deg2Rad(op.GetDouble("rotateZ", 0));

            if (Math.Abs(rx) > 1e-12)
                solid.TransformBy(Matrix3d.Rotation(rx, Vector3d.XAxis, Point3d.Origin));
            if (Math.Abs(ry) > 1e-12)
                solid.TransformBy(Matrix3d.Rotation(ry, Vector3d.YAxis, Point3d.Origin));
            if (Math.Abs(rz) > 1e-12)
                solid.TransformBy(Matrix3d.Rotation(rz, Vector3d.ZAxis, Point3d.Origin));

            solid.TransformBy(Matrix3d.Displacement(centre.GetAsVector()));
        }

        /// <summary>Reads [x,y] or [x,y,z], applying the plan origin.</summary>
        internal static Point3d Point3(DrawContext ctx, JsonValue op, string field)
        {
            JsonValue value = op[field];
            if (value == null || value.Kind != JsonKind.Array || value.Count < 2)
                return new Point3d(ctx.Origin.X, ctx.Origin.Y, ctx.Origin.Z);

            double x = value.At(0).Number;
            double y = value.At(1).Number;
            double z = value.Count > 2 && value.At(2).Kind == JsonKind.Number ? value.At(2).Number : 0.0;
            return new Point3d(x + ctx.Origin.X, y + ctx.Origin.Y, z + ctx.Origin.Z);
        }

        /// <summary>Reads a bare [x,y] / [x,y,z] array, applying the plan origin.</summary>
        internal static Point3d Point3FromArray(DrawContext ctx, JsonValue value)
        {
            if (value == null || value.Kind != JsonKind.Array || value.Count < 2)
                return new Point3d(ctx.Origin.X, ctx.Origin.Y, ctx.Origin.Z);

            double x = value.At(0).Number;
            double y = value.At(1).Number;
            double z = value.Count > 2 && value.At(2).Kind == JsonKind.Number ? value.At(2).Number : 0.0;
            return new Point3d(x + ctx.Origin.X, y + ctx.Origin.Y, z + ctx.Origin.Z);
        }

        internal static string RotationHelp
        {
            get { return "rotateX?,rotateY?,rotateZ? (degrees, applied about the item's own centre)"; }
        }

        internal static string ProfileHelp
        {
            get
            {
                return "profile:[[x,y],...],bulges?:[number,...] (bulge = tan(includedAngle/4); " +
                       "use them for rounded, cast-looking shapes instead of faceted ones)";
            }
        }

        /// <summary>
        /// Builds a closed outline from a profile, honouring bulges so curved
        /// shapes are possible. The caller disposes the returned polyline; it is
        /// scaffolding for the region and never enters the drawing.
        /// </summary>
        internal static Polyline BuildOutline(DrawContext ctx, JsonValue op)
        {
            return BuildOutline(ctx, op, true);
        }

        /// <summary>
        /// applyOrigin is false for revolve, whose profile is a local
        /// (radius, height) section rather than a position in the drawing - the
        /// finished solid is moved to the axis point instead.
        /// </summary>
        internal static Polyline BuildOutline(DrawContext ctx, JsonValue op, bool applyOrigin)
        {
            JsonValue profile = op["profile"];
            List<JsonValue> bulges = op.GetArray("bulges");

            Polyline outline = new Polyline();
            for (int i = 0; i < profile.Count; i++)
            {
                JsonValue point = profile.At(i);
                double x = point.At(0).Number;
                double y = point.At(1).Number;
                if (applyOrigin) { x += ctx.Origin.X; y += ctx.Origin.Y; }

                double bulge = 0;
                if (i < bulges.Count && bulges[i] != null && bulges[i].Kind == JsonKind.Number)
                    bulge = bulges[i].Number;
                outline.AddVertexAt(i, new Point2d(x, y), bulge, 0, 0);
            }
            outline.Closed = true;
            return outline;
        }

        /// <summary>The region a solid is built from, or null when the outline is not closed.</summary>
        internal static Region BuildRegion(Polyline outline, DrawContext ctx, out DBObjectCollection owned)
        {
            owned = null;
            DBObjectCollection curves = new DBObjectCollection();
            curves.Add(outline);
            try
            {
                DBObjectCollection regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0) return null;
                owned = regions;
                return (Region)regions[0];
            }
            catch (Exception ex)
            {
                ctx.Warn("The profile did not form a closed area (" + ex.Message + ").");
                return null;
            }
        }

        internal static void DisposeRegions(DBObjectCollection regions)
        {
            if (regions == null) return;
            for (int i = 0; i < regions.Count; i++)
            {
                DBObject leftover = regions[i];
                if (leftover != null && !leftover.IsDisposed) leftover.Dispose();
            }
        }

        /// <summary>Shared profile validation for extrude, revolve and sweep.</summary>
        internal static void ValidateProfile(OpIssues issues, JsonValue op)
        {
            JsonValue profile = op["profile"];
            if (profile == null || profile.Kind != JsonKind.Array || profile.Count < 3)
            {
                issues.Error("a 'profile' of at least 3 points is required");
                return;
            }
            if (profile.Count > Limits.MaxPolylinePoints) issues.Error("the profile is too large");

            for (int i = 0; i < profile.Count; i++)
            {
                JsonValue p = profile.At(i);
                if (p == null || p.Kind != JsonKind.Array || p.Count < 2)
                {
                    issues.Error("profile point " + i + " is not [x,y]");
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------ primitives

    public class BoxOp : IOp
    {
        public string Name { get { return "box"; } }

        public string Usage
        {
            get
            {
                return "box: {op,center:[x,y,z],width,depth,height," + Solids.RotationHelp +
                       ",layer?,color?}  - 3D solid box, centre is the middle of the box";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "width");
            issues.RequirePositive(op, "depth");
            issues.RequirePositive(op, "height");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Solid3d solid = new Solid3d();
            solid.SetDatabaseDefaults();
            solid.CreateBox(op.GetDouble("width", 1), op.GetDouble("depth", 1), op.GetDouble("height", 1));
            Solids.Place(solid, Solids.Point3(ctx, op, "center"), op);
            ctx.Add(solid, op);
        }
    }

    public class CylinderOp : IOp
    {
        public string Name { get { return "cylinder"; } }

        public string Usage
        {
            get
            {
                return "cylinder: {op,center:[x,y,z],radius,height," + Solids.RotationHelp +
                       ",layer?,color?}  - 3D cylinder along Z, centre is its middle";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "radius");
            issues.RequirePositive(op, "height");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            double radius = op.GetDouble("radius", 1);
            Solid3d solid = new Solid3d();
            solid.SetDatabaseDefaults();
            solid.CreateFrustum(op.GetDouble("height", 1), radius, radius, radius);
            Solids.Place(solid, Solids.Point3(ctx, op, "center"), op);
            ctx.Add(solid, op);
        }
    }

    public class ConeOp : IOp
    {
        public string Name { get { return "cone"; } }

        public string Usage
        {
            get
            {
                return "cone: {op,center:[x,y,z],radius,topRadius?(0),height," + Solids.RotationHelp +
                       ",layer?}  - cone or truncated cone along Z";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "radius");
            issues.RequirePositive(op, "height");
            if (op.GetDouble("topRadius", 0) < 0) issues.Error("cone 'topRadius' cannot be negative");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            double radius = op.GetDouble("radius", 1);
            Solid3d solid = new Solid3d();
            solid.SetDatabaseDefaults();
            solid.CreateFrustum(op.GetDouble("height", 1), radius, radius, op.GetDouble("topRadius", 0));
            Solids.Place(solid, Solids.Point3(ctx, op, "center"), op);
            ctx.Add(solid, op);
        }
    }

    public class SphereOp : IOp
    {
        public string Name { get { return "sphere"; } }

        public string Usage
        {
            get { return "sphere: {op,center:[x,y,z],radius,layer?,color?}  - 3D sphere"; }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "center", true);
            issues.RequirePositive(op, "radius");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Solid3d solid = new Solid3d();
            solid.SetDatabaseDefaults();
            solid.CreateSphere(op.GetDouble("radius", 1));
            Solids.Place(solid, Solids.Point3(ctx, op, "center"), op);
            ctx.Add(solid, op);
        }
    }

    // --------------------------------------------------------------- extrude

    public class ExtrudeOp : IOp
    {
        public string Name { get { return "extrude"; } }

        public string Usage
        {
            get
            {
                return "extrude: {op," + Solids.ProfileHelp + ",height,base?:[x,y,z],taper?:deg," +
                       Solids.RotationHelp + ",layer?}" +
                       "  - turns a closed 2D outline into a solid; the profile is always closed for you";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            Solids.ValidateProfile(issues, op);
            issues.RequirePositive(op, "height");

            double taper = Math.Abs(op.GetDouble("taper", 0));
            if (taper >= 90) issues.Error("extrude 'taper' must be less than 90 degrees");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Polyline outline = Solids.BuildOutline(ctx, op);

            Solid3d solid = null;
            DBObjectCollection regions = null;
            try
            {
                Region region = Solids.BuildRegion(outline, ctx, out regions);
                if (region == null)
                {
                    ctx.Warn("The extrude profile did not form a closed area; skipped.");
                    return;
                }

                solid = new Solid3d();
                solid.SetDatabaseDefaults();
                solid.Extrude(region, op.GetDouble("height", 1),
                              DrawContext.Deg2Rad(op.GetDouble("taper", 0)));

                // Extrude builds upward from the profile plane; base moves the
                // whole thing without disturbing the shape.
                JsonValue baseAt = op["base"];
                if (baseAt != null)
                {
                    Point3d target = Solids.Point3(ctx, op, "base");
                    solid.TransformBy(Matrix3d.Displacement(target.GetAsVector() - ctx.Origin));
                }

                ctx.Add(solid, op);
                solid = null;
            }
            catch (Exception ex)
            {
                ctx.Warn("Extrude failed (" + ex.Message + "); skipped.");
            }
            finally
            {
                outline.Dispose();
                Solids.DisposeRegions(regions);
                if (solid != null) solid.Dispose();
            }
        }
    }

    /// <summary>
    /// Spins a profile around an axis. This is what gives rounded, cast-looking
    /// housings - domed caps, bell-shaped pedestals, turned shafts - which
    /// cylinders and boxes cannot express.
    /// </summary>
    public class RevolveOp : IOp
    {
        public string Name { get { return "revolve"; } }

        public string Usage
        {
            get
            {
                return "revolve: {op,profile:[[radius,height],...],bulges?:[number,...]," +
                       "axisPoint?:[x,y,z],axisDirection?:[dx,dy,dz](default [0,0,1])," +
                       "angle?:deg(360),layer?}" +
                       "  - spins a half-section around an axis, giving turned and cast shapes " +
                       "(domed caps, bell housings, shafts). IMPORTANT: profile points are " +
                       "[distance from the axis, position along the axis], NOT drawing x,y. " +
                       "Start and end on the axis (radius 0) for a solid; keep radius above 0 " +
                       "throughout for a tube. Use bulges for rounded, cast-looking outlines.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            Solids.ValidateProfile(issues, op);

            double angle = op.GetDouble("angle", 360);
            if (angle <= 0 || angle > 360) issues.Error("revolve 'angle' must be between 0 and 360");

            JsonValue profile = op["profile"];
            if (profile != null && profile.Kind == JsonKind.Array)
            {
                for (int i = 0; i < profile.Count; i++)
                {
                    JsonValue p = profile.At(i);
                    if (p != null && p.Kind == JsonKind.Array && p.Count >= 1 &&
                        p.At(0).Kind == JsonKind.Number && p.At(0).Number < 0)
                    {
                        issues.Error("revolve profile point " + i +
                                     " has a negative radius; the section must stay on one " +
                                     "side of the axis");
                        return;
                    }
                }
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            // The section is local: x is radius, y is height along the axis.
            Polyline outline = Solids.BuildOutline(ctx, op, false);

            Solid3d solid = null;
            DBObjectCollection regions = null;
            try
            {
                Region region = Solids.BuildRegion(outline, ctx, out regions);
                if (region == null)
                {
                    ctx.Warn("The revolve profile did not form a closed area; skipped.");
                    return;
                }

                // Stand the section up into the XZ plane. A revolve axis must lie
                // in the plane of the profile - spinning a shape about an axis
                // sticking out of its own face is meaningless, and was why this
                // failed with eInvalidInput.
                region.TransformBy(Matrix3d.Rotation(Math.PI / 2.0, Vector3d.XAxis, Point3d.Origin));

                double angle = DrawContext.Deg2Rad(op.GetDouble("angle", 360));
                solid = new Solid3d();
                solid.SetDatabaseDefaults();

                // CloseToAxis lets the section touch the axis, which is what makes
                // a solid of revolution rather than a tube.
                RevolveOptionsBuilder options = new RevolveOptionsBuilder();
                options.CloseToAxis = true;
                options.DraftAngle = 0;

                try
                {
                    solid.CreateRevolvedSolid(region, Point3d.Origin, Vector3d.ZAxis, angle, 0.0,
                                              options.ToRevolveOptions());
                }
                catch (Exception)
                {
                    solid.Dispose();
                    solid = new Solid3d();
                    solid.SetDatabaseDefaults();
                    solid.Revolve(region, Point3d.Origin, Vector3d.ZAxis, angle);
                }

                // Built about Z at the origin, then aimed and moved into place.
                Vector3d axis = ReadAxis(op);
                if (!axis.IsParallelTo(Vector3d.ZAxis))
                {
                    Vector3d turn = Vector3d.ZAxis.CrossProduct(axis);
                    solid.TransformBy(Matrix3d.Rotation(
                        Vector3d.ZAxis.GetAngleTo(axis), turn.GetNormal(), Point3d.Origin));
                }
                else if (axis.DotProduct(Vector3d.ZAxis) < 0)
                {
                    solid.TransformBy(Matrix3d.Rotation(Math.PI, Vector3d.XAxis, Point3d.Origin));
                }

                Point3d at = op["axisPoint"] != null
                    ? Solids.Point3(ctx, op, "axisPoint")
                    : new Point3d(ctx.Origin.X, ctx.Origin.Y, ctx.Origin.Z);
                solid.TransformBy(Matrix3d.Displacement(at.GetAsVector()));

                ctx.Add(solid, op);
                solid = null;
            }
            catch (Exception ex)
            {
                ctx.Warn("Revolve failed (" + ex.Message + "). The profile must be a closed " +
                         "[radius,height] section with no negative radius; skipped.");
            }
            finally
            {
                outline.Dispose();
                Solids.DisposeRegions(regions);
                if (solid != null) solid.Dispose();
            }
        }

        private static Vector3d ReadAxis(JsonValue op)
        {
            JsonValue direction = op["axisDirection"];
            if (direction == null || direction.Kind != JsonKind.Array || direction.Count < 2)
                return Vector3d.ZAxis;

            Vector3d axis = new Vector3d(
                direction.At(0).Number,
                direction.At(1).Number,
                direction.Count > 2 ? direction.At(2).Number : 0.0);
            return axis.Length < 1e-9 ? Vector3d.ZAxis : axis.GetNormal();
        }
    }

    /// <summary>
    /// Drags a profile along a path: cable conduit, hoses, handrails, curved arm
    /// sections - anything with constant section following a route.
    /// </summary>
    public class SweepOp : IOp
    {
        public string Name { get { return "sweep"; } }

        public string Usage
        {
            get
            {
                return "sweep: {op," + Solids.ProfileHelp + ",path:[[x,y,z],...],layer?}" +
                       "  - sweeps the profile along the path. Use a small circle-like profile " +
                       "for conduit and cable runs.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            Solids.ValidateProfile(issues, op);

            JsonValue path = op["path"];
            if (path == null || path.Kind != JsonKind.Array || path.Count < 2)
            {
                issues.Error("sweep needs a 'path' of at least 2 points");
                return;
            }
            if (path.Count > Limits.MaxPolylinePoints) issues.Error("the sweep path is too long");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            Polyline outline = Solids.BuildOutline(ctx, op);

            Solid3d solid = null;
            DBObjectCollection regions = null;
            Polyline3d path = null;
            try
            {
                Region region = Solids.BuildRegion(outline, ctx, out regions);
                if (region == null)
                {
                    ctx.Warn("The sweep profile did not form a closed area; skipped.");
                    return;
                }

                JsonValue points = op["path"];
                Point3dCollection route = new Point3dCollection();
                for (int i = 0; i < points.Count; i++)
                    route.Add(Solids.Point3FromArray(ctx, points.At(i)));

                // A 3D polyline must live in the database before it can be used
                // as a sweep path; it is removed again once the solid exists.
                path = new Polyline3d(Poly3dType.SimplePoly, route, false);
                ctx.Space.AppendEntity(path);
                ctx.Tr.AddNewlyCreatedDBObject(path, true);

                SweepOptionsBuilder options = new SweepOptionsBuilder();
                options.Align = SweepOptionsAlignOption.AlignSweepEntityToPath;
                options.BasePoint = path.StartPoint;
                options.Bank = true;

                solid = new Solid3d();
                solid.SetDatabaseDefaults();
                solid.CreateSweptSolid(region, path, options.ToSweepOptions());

                ctx.Add(solid, op);
                solid = null;

                path.Erase();
                path = null;
            }
            catch (Exception ex)
            {
                ctx.Warn("Sweep failed (" + ex.Message + "); skipped.");
                if (path != null)
                {
                    try { path.Erase(); }
                    catch (Exception) { }
                }
            }
            finally
            {
                outline.Dispose();
                Solids.DisposeRegions(regions);
                if (solid != null) solid.Dispose();
            }
        }
    }

    // -------------------------------------------------------------- booleans

    /// <summary>
    /// Combines solids. Cutting holes is the main use: draw the panel, draw the
    /// holes, subtract. Both sides are ordinary nested ops, so anything that
    /// makes a solid can take part.
    /// </summary>
    public class BooleanOp : IOp
    {
        private readonly string _name;

        public BooleanOp(string name)
        {
            _name = name;
        }

        public string Name { get { return _name; } }

        public string Usage
        {
            get
            {
                if (_name == "subtract")
                    return "subtract: {op,from:[...solid ops...],remove:[...solid ops...]}" +
                           "  - cuts the second set out of the first, e.g. gland holes in a panel";
                return "union: {op,solids:[...solid ops...]}  - merges solids into one";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            if (_name == "subtract")
            {
                if (op["from"] == null || op["from"].Kind != JsonKind.Array || op["from"].Count == 0)
                    issues.Error("subtract needs a non-empty 'from' array");
                if (op["remove"] == null || op["remove"].Kind != JsonKind.Array || op["remove"].Count == 0)
                    issues.Error("subtract needs a non-empty 'remove' array");
            }
            else
            {
                if (op["solids"] == null || op["solids"].Kind != JsonKind.Array || op["solids"].Count < 2)
                    issues.Error("union needs at least 2 entries in 'solids'");
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            List<ObjectId> first = RunAndCollect(ctx, _name == "subtract" ? op.GetArray("from")
                                                                          : op.GetArray("solids"));
            if (first.Count == 0)
            {
                ctx.Warn(_name + ": nothing solid was produced; skipped.");
                return;
            }

            List<ObjectId> second = _name == "subtract"
                ? RunAndCollect(ctx, op.GetArray("remove"))
                : new List<ObjectId>();

            // For a union the first entry absorbs the rest.
            List<ObjectId> others = new List<ObjectId>();
            if (_name == "subtract") others.AddRange(second);
            else for (int i = 1; i < first.Count; i++) others.Add(first[i]);

            Solid3d target = ctx.Tr.GetObject(first[0], OpenMode.ForWrite, false) as Solid3d;
            if (target == null)
            {
                ctx.Warn(_name + ": the first shape is not a 3D solid; skipped.");
                return;
            }

            BooleanOperationType operation = _name == "subtract"
                ? BooleanOperationType.BoolSubtract
                : BooleanOperationType.BoolUnite;

            for (int i = 0; i < others.Count; i++)
            {
                Solid3d other = ctx.Tr.GetObject(others[i], OpenMode.ForWrite, false) as Solid3d;
                if (other == null) continue;
                try
                {
                    target.BooleanOperation(operation, other);
                    // The consumed solid is emptied by the operation, not removed.
                    other.Erase();
                }
                catch (Exception ex)
                {
                    ctx.Warn(_name + " failed on one shape (" + ex.Message + ").");
                }
            }
        }

        /// <summary>Runs nested ops and reports which entities they created.</summary>
        private static List<ObjectId> RunAndCollect(DrawContext ctx, List<JsonValue> ops)
        {
            int before = ctx.Created.Count;
            ctx.RunOps(ops);

            List<ObjectId> made = new List<ObjectId>();
            for (int i = before; i < ctx.Created.Count; i++) made.Add(ctx.Created[i]);
            return made;
        }
    }

    // ------------------------------------------------------------------ view

    /// <summary>Switches to an isometric view, so 3D work is visible immediately.</summary>
    /// <summary>
    /// Blends a stack of outlines into one solid. This is how a part gets the
    /// tapered, flowing shape of a casting: a link that starts as a wide
    /// shoulder and finishes as a slim wrist is one loft, where boxes and
    /// cylinders would need a dozen pieces and still look like blocks.
    /// </summary>
    public class LoftOp : IOp
    {
        private const int MaxSections = 64;

        public string Name { get { return "loft"; } }

        public string Usage
        {
            get
            {
                return "loft: {op,sections:[{profile:[[x,y],...],bulges?:[number,...],z:number},...]," +
                       "at?:[x,y,z]," + Solids.RotationHelp + ",ruled?:bool,layer?}" +
                       "  - blends two or more closed outlines, each at its own z, into a single " +
                       "solid. Use it for tapered and cast-looking parts: an arm link narrowing " +
                       "from a wide shoulder to a slim wrist, a fairing, a moulded cover. " +
                       "Sections may differ in shape and size; list them in order along the part. " +
                       "Rotation is about the base of the stack, so a link can be built upright " +
                       "and then swung into place.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            JsonValue sections = op["sections"];
            if (sections == null || sections.Kind != JsonKind.Array || sections.Count < 2)
            {
                issues.Error("loft needs a 'sections' array of at least 2 outlines");
                return;
            }
            if (sections.Count > MaxSections)
            {
                issues.Error("loft has more than " + MaxSections + " sections");
                return;
            }

            for (int i = 0; i < sections.Count; i++)
            {
                JsonValue section = sections.At(i);
                if (section == null || section.Kind != JsonKind.Object)
                {
                    issues.Error("loft section " + i + " must be an object with a 'profile'");
                    return;
                }
                Solids.ValidateProfile(issues, section);
                if (!issues.Ok) return;
            }
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            JsonValue sections = op["sections"];
            List<Entity> outlines = new List<Entity>();
            Solid3d solid = null;
            try
            {
                for (int i = 0; i < sections.Count; i++)
                {
                    JsonValue section = sections.At(i);
                    // Local outlines: the finished solid is placed as a whole.
                    Polyline outline = Solids.BuildOutline(ctx, section, false);
                    outline.Elevation = section.GetDouble("z", 0);
                    outlines.Add(outline);
                }

                LoftOptionsBuilder options = new LoftOptionsBuilder();
                options.Ruled = op.GetBool("ruled", false);
                options.Closed = false;

                solid = new Solid3d();
                solid.SetDatabaseDefaults();
                solid.CreateLoftedSolid(outlines.ToArray(), new Entity[0], null,
                                        options.ToLoftOptions());

                Point3d at = op["at"] != null
                    ? Solids.Point3(ctx, op, "at")
                    : new Point3d(ctx.Origin.X, ctx.Origin.Y, ctx.Origin.Z);
                Solids.Place(solid, at, op);

                ctx.Add(solid, op);
                solid = null;
            }
            catch (Exception ex)
            {
                ctx.Warn("Loft failed (" + ex.Message + "). Every section must be a closed " +
                         "outline with its own z, listed in order along the part; skipped.");
            }
            finally
            {
                for (int i = 0; i < outlines.Count; i++) outlines[i].Dispose();
                if (solid != null) solid.Dispose();
            }
        }
    }

    public class View3dOp : IOp
    {
        public string Name { get { return "view3d"; } }

        public string Usage
        {
            get
            {
                return "view3d: {op,style?:\"shaded\"|\"conceptual\"|\"wireframe\"}" +
                       "  - switches the viewport to an isometric 3D view and shades it. " +
                       "Add this as the last op of any 3D drawing.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            if (ctx.Ed == null) return;
            try
            {
                using (ViewTableRecord view = ctx.Ed.GetCurrentView())
                {
                    // South-east isometric: the conventional way to read a 3D model.
                    view.ViewDirection = new Vector3d(1, -1, 1);

                    // Left as 2D wireframe, a solid model is drawn as every edge of
                    // every part seen through every other part, which is unreadable
                    // as soon as there is more than a handful of them. Shading is
                    // what makes it look like the object instead of a cage.
                    ObjectId style = FindVisualStyle(ctx, op.GetString("style", "shaded"));
                    if (!style.IsNull) view.VisualStyleId = style;

                    ctx.Ed.SetCurrentView(view);
                }
            }
            catch (Exception)
            {
                // A view change is cosmetic; the geometry matters more.
            }
        }

        /// <summary>
        /// Resolves one of our short style names to a visual style in the
        /// drawing. Falls back through the alternatives so an unusual template
        /// still gets something better than wireframe.
        /// </summary>
        private static ObjectId FindVisualStyle(DrawContext ctx, string requested)
        {
            string[] wanted;
            if (string.Equals(requested, "wireframe", StringComparison.OrdinalIgnoreCase))
                wanted = new string[] { "3D Wireframe", "Wireframe" };
            else if (string.Equals(requested, "conceptual", StringComparison.OrdinalIgnoreCase))
                wanted = new string[] { "Conceptual", "Shaded with edges", "Shaded" };
            else
                wanted = new string[] { "Shaded with edges", "Shaded", "Conceptual" };

            try
            {
                if (ctx.Db == null || ctx.Tr == null) return ObjectId.Null;
                DBDictionary styles =
                    (DBDictionary)ctx.Tr.GetObject(ctx.Db.VisualStyleDictionaryId, OpenMode.ForRead);
                for (int i = 0; i < wanted.Length; i++)
                    if (styles.Contains(wanted[i])) return styles.GetAt(wanted[i]);
            }
            catch (Exception)
            {
            }
            return ObjectId.Null;
        }
    }
}
