using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AiCad.Execution;
using AiCad.Json;

namespace AiCad.Ops
{
    /// <summary>
    /// Locates the AutoCAD Electrical symbol libraries and resolves symbol
    /// names to the .dwg files that define them.
    /// </summary>
    public static class SymbolLibrary
    {
        public const string DefaultLibrary = "jic125";

        private static string _root;
        private static bool _searched;

        /// <summary>
        /// The Libs folder AutoCAD Electrical installs under Public Documents.
        /// Probed once and cached; null when Electrical is not installed.
        /// </summary>
        public static string Root
        {
            get
            {
                if (_searched) return _root;
                _searched = true;

                string common;
                try
                {
                    common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
                }
                catch (Exception)
                {
                    return null;
                }

                // Newest first, so a machine with several releases uses the latest.
                for (int year = 2026; year >= 2016; year--)
                {
                    string candidate = Path.Combine(common,
                        "Autodesk\\Acade " + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\\Libs");
                    if (Directory.Exists(candidate)) { _root = candidate; return _root; }
                }
                return null;
            }
        }

        public static List<string> Libraries()
        {
            List<string> names = new List<string>();
            try
            {
                if (Root == null) return names;
                string[] dirs = Directory.GetDirectories(Root);
                for (int i = 0; i < dirs.Length; i++) names.Add(Path.GetFileName(dirs[i]));
                names.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
            }
            return names;
        }

        /// <summary>
        /// Only bare names are accepted. A symbol name is used to build a file
        /// path, so anything with a separator or a parent reference is refused
        /// rather than allowed to escape the library folder.
        /// </summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        public static string ResolvePath(string library, string symbol)
        {
            if (Root == null) return null;
            if (!IsSafeName(library) || !IsSafeName(symbol)) return null;

            string path = Path.Combine(Path.Combine(Root, library), symbol + ".dwg");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// A verified subset of the JIC library, with plain-English meanings.
        /// The full library runs to well over a thousand symbols, far too many
        /// for a prompt, so the model is given these and may also name any other
        /// symbol it knows - unresolvable names are skipped with a warning.
        /// </summary>
        public static readonly string[][] Common =
        {
            new string[] { "Hpb11",   "pushbutton, normally open (start)" },
            new string[] { "Hpb12",   "pushbutton, normally closed (stop)" },
            new string[] { "Hcr1",    "relay or contactor coil" },
            new string[] { "Hcr21",   "relay contact, normally open" },
            new string[] { "Hcr22",   "relay contact, normally closed" },
            new string[] { "Hms1",    "motor starter coil" },
            new string[] { "Hms21",   "starter contact, normally open" },
            new string[] { "Hms22",   "starter contact, normally closed" },
            new string[] { "Hms21ol", "overload contact, normally closed" },
            new string[] { "Hlt1a",   "pilot light / indicator lamp" },
            new string[] { "Hfu1",    "fuse" },
            new string[] { "Htd1n",   "on-delay timer coil" },
            new string[] { "Htd21i",  "timer contact, normally open" },
            new string[] { "Hls11",   "limit switch, normally open" },
            new string[] { "Hls12",   "limit switch, normally closed" },
            new string[] { "Hss112",  "selector switch, 2 position" },
            new string[] { "Hds11",   "disconnect switch" },
            new string[] { "Hmo1",    "motor" }
        };

        public static string CommonList()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Common.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Common[i][0]).Append('=').Append(Common[i][1]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Inserts a genuine AutoCAD Electrical library symbol, pulling the block
    /// definition out of the library .dwg the first time it is used.
    /// </summary>
    public class SymbolOp : IOp
    {
        public string Name { get { return "symbol"; } }

        public string Usage
        {
            get
            {
                string libs = string.Join("/", SymbolLibrary.Libraries().ToArray());
                if (libs.Length == 0) libs = "(none detected)";

                return "symbol: {op,name:\"Hpb11\",library?:\"" + SymbolLibrary.DefaultLibrary +
                       "\",position:[x,y],rotation?:deg,scale?,attributes?:{TAG1:\"S1\",DESC1:\"START\"},layer?}" +
                       "  - real AutoCAD Electrical symbol. Libraries: " + libs +
                       ". Common JIC symbols: " + SymbolLibrary.CommonList() +
                       ". IMPORTANT: these library blocks are TINY - roughly 1 to 2 units tall at " +
                       "scale 1 - so \"scale\" is not optional. Pick the scale FIRST so a symbol is " +
                       "about 1/15 of the finished drawing (typically scale 10-20), then lay the " +
                       "wires out around the scaled symbols. The symbol's terminals scale with it, " +
                       "so wire endpoints must be computed from the scaled size or nothing will " +
                       "join up. Space devices at least 3 symbol-widths apart.";
            }
        }

        public void Validate(OpIssues issues, JsonValue op)
        {
            issues.RequirePoint(op, "position", true);

            string name = op.GetString("name", null);
            if (string.IsNullOrEmpty(name))
            {
                issues.Error("symbol needs a 'name'");
                return;
            }
            if (!SymbolLibrary.IsSafeName(name))
                issues.Error("symbol name '" + name + "' contains characters that are not allowed");

            string library = op.GetString("library", SymbolLibrary.DefaultLibrary);
            if (!SymbolLibrary.IsSafeName(library))
                issues.Error("library name '" + library + "' contains characters that are not allowed");
        }

        public void Draw(DrawContext ctx, JsonValue op)
        {
            string name = op.GetString("name", "");
            string library = op.GetString("library", SymbolLibrary.DefaultLibrary);

            if (SymbolLibrary.Root == null)
            {
                ctx.Warn("No AutoCAD Electrical symbol library found; '" + name + "' skipped.");
                return;
            }

            ObjectId defId = EnsureBlockDefinition(ctx, library, name);
            if (defId.IsNull) return;

            BlockReference br = new BlockReference(ctx.Point(op, "position"), defId);
            double scale = op.GetDouble("scale", 1.0);
            if (scale > 0) br.ScaleFactors = new Scale3d(scale);
            br.Rotation = DrawContext.Deg2Rad(op.GetDouble("rotation", 0));
            ctx.Add(br, op);

            AppendAttributes(ctx, br, defId, op["attributes"]);
        }

        /// <summary>
        /// Imports the block from its library file on first use, then reuses the
        /// definition already in the drawing.
        /// </summary>
        private static ObjectId EnsureBlockDefinition(DrawContext ctx, string library, string symbol)
        {
            BlockTable bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(symbol)) return bt[symbol];

            string path = SymbolLibrary.ResolvePath(library, symbol);
            if (path == null)
            {
                ctx.Warn("Symbol '" + symbol + "' was not found in library '" + library + "'; skipped.");
                return ObjectId.Null;
            }

            try
            {
                using (Database source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                    ctx.Db.Insert(symbol, source, true);
                }

                bt = (BlockTable)ctx.Tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
                if (bt.Has(symbol)) return bt[symbol];

                ctx.Warn("Symbol '" + symbol + "' could not be imported; skipped.");
                return ObjectId.Null;
            }
            catch (Exception ex)
            {
                ctx.Warn("Symbol '" + symbol + "' failed to load (" + ex.Message + "); skipped.");
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Fills the symbol's attributes. AE symbols carry TAG1, DESC1 and
        /// similar, which is what makes the inserted block readable as a device.
        /// </summary>
        private static void AppendAttributes(DrawContext ctx, BlockReference br, ObjectId defId, JsonValue attrs)
        {
            BlockTableRecord def = (BlockTableRecord)ctx.Tr.GetObject(defId, OpenMode.ForRead);
            if (!def.HasAttributeDefinitions) return;

            foreach (ObjectId id in def)
            {
                AttributeDefinition ad = ctx.Tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant) continue;

                AttributeReference ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                if (attrs != null && attrs.Kind == JsonKind.Object && attrs[ad.Tag] != null)
                    ar.TextString = attrs.GetString(ad.Tag, ar.TextString);

                br.AttributeCollection.AppendAttribute(ar);
                ctx.Tr.AddNewlyCreatedDBObject(ar, true);
            }
        }
    }
}
