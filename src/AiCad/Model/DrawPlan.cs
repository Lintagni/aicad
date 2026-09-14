using System;
using System.Collections.Generic;
using System.Globalization;
using AiCad.Json;

namespace AiCad.Model
{
    public class LayerSpec
    {
        public string Name;
        public int Color;
        public string Linetype;
        public double LineWeight;

        public LayerSpec()
        {
            Color = -1;
            LineWeight = 0;
        }
    }

    /// <summary>
    /// What the model is allowed to return: a named set of layers plus a flat
    /// list of ops. Anything else in the payload is ignored.
    /// </summary>
    public class DrawPlan
    {
        public string Name;
        public string Notes;
        public string Units;
        public List<LayerSpec> Layers;
        public List<JsonValue> Ops;

        public DrawPlan()
        {
            Name = "AI plan";
            Units = "mm";
            Layers = new List<LayerSpec>();
            Ops = new List<JsonValue>();
        }

        public static DrawPlan FromJson(JsonValue root)
        {
            DrawPlan plan = new DrawPlan();
            if (root == null || root.Kind != JsonKind.Object) return plan;

            // Tolerate the plan being nested one level down.
            JsonValue body = root["plan"];
            if (body != null && body.Kind == JsonKind.Object) root = body;

            plan.Name = root.GetString("name", "AI plan");
            plan.Notes = root.GetString("notes", null);
            plan.Units = root.GetString("units", "mm");

            List<JsonValue> layers = root.GetArray("layers");
            for (int i = 0; i < layers.Count; i++)
            {
                JsonValue l = layers[i];
                if (l == null || l.Kind != JsonKind.Object) continue;
                string name = l.GetString("name", null);
                if (string.IsNullOrEmpty(name)) continue;
                LayerSpec spec = new LayerSpec();
                spec.Name = name;
                spec.Color = l.GetInt("color", -1);
                spec.Linetype = l.GetString("linetype", null);
                spec.LineWeight = l.GetDouble("lineWeight", 0);
                plan.Layers.Add(spec);
            }

            plan.Ops = root.GetArray("ops");
            return plan;
        }

        /// <summary>Short human summary shown in the palette log.</summary>
        public string Describe()
        {
            return Name + " - " + CountOps(Ops).ToString(CultureInfo.InvariantCulture) + " operation(s)";
        }

        public static int CountOps(List<JsonValue> ops)
        {
            int total = 0;
            if (ops == null) return 0;
            for (int i = 0; i < ops.Count; i++)
            {
                JsonValue op = ops[i];
                if (op == null || op.Kind != JsonKind.Object) continue;
                total++;
                List<JsonValue> nested = op.GetArray("ops");
                if (nested.Count > 0) total += CountOps(nested);
            }
            return total;
        }
    }
}
