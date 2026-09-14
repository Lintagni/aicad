using System;
using System.Collections.Generic;
using System.Globalization;
using AiCad.Json;
using AiCad.Model;

namespace AiCad.Tests
{
    /// <summary>
    /// Covers the parts that carry no AutoCAD dependency: the JSON layer that
    /// every model reply passes through, and the built-in sample plan.
    /// Run with tests\run-tests.ps1.
    /// </summary>
    public static class HeadlessTests
    {
        private static int _passed;
        private static int _failed;

        public static int Main()
        {
            SamplePlanParses();
            RoundTripPreservesValues();
            LenientParsingRecoversFromProse();
            LenientParsingHandlesCodeFence();
            EscapesSurviveRoundTrip();
            NumberFormsParse();
            MissingMembersFallBack();
            MalformedJsonIsRejected();
            DeepNestingCounts();
            PlanInboxRoundTrips();
            StoredKeySurvivesAnEmptySave();
            TruncatedPlanIsNotMistakenForAPlan();
            ExamplesAreChosenByRelevance();
            SolidGuideIsSpentOnlyOn3dRequests();
            TitleBlockFieldsStayInTheirCell();
            PlanStreamCountsOpsAsTheyArrive();

            Console.WriteLine();
            Console.WriteLine(_failed == 0
                ? "All " + _passed + " checks passed."
                : _passed + " passed, " + _failed + " FAILED.");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------- tests

        /// <summary>
        /// Counting operations while the plan is still being written. The text
        /// is not valid JSON until the last brace, so this cannot lean on the
        /// parser - and an off-by-one in the "ops" key lookback once made it
        /// count nothing at all, silently.
        /// </summary>
        private static void PlanStreamCountsOpsAsTheyArrive()
        {
            Console.WriteLine();
            Console.WriteLine("-- plan stream counts ops while they arrive");

            // Split exactly where Gemini splits it: mid-key and mid-object.
            AiCad.Ai.PlanStream s = new AiCad.Ai.PlanStream();
            Check("nothing counted yet", s.OpsComplete == 0);

            s.Append("{\"name\":\"staircase\",\"ops\":");
            Check("the key alone is not an op", s.OpsComplete == 0);

            s.Append("[{\"op\":\"line\",\"start\":[0,0],\"end\":[1,");
            Check("a half-written op does not count", s.OpsComplete == 0);

            s.Append("1]},");
            Check("the first op counts once closed", s.OpsComplete == 1);

            s.Append("{\"op\":\"line\",\"start\":[1,1],\"end\":[2,2]},");
            Check("the second op counts", s.OpsComplete == 2);

            s.Append("{\"op\":\"text\",\"value\":\"a}b{c\"}]}");
            Check("braces inside a string are not structure", s.OpsComplete == 3);
            Check("the whole text is kept", s.Text.StartsWith("{\"name\"") && s.Text.EndsWith("}"));

            // A nested list counts as the one operation the user asked for.
            AiCad.Ai.PlanStream nested = new AiCad.Ai.PlanStream();
            nested.Append("{\"ops\":[{\"op\":\"repeat\",\"ops\":[{\"op\":\"line\"},{\"op\":\"line\"}]}]}");
            Check("a nested group counts as one op", nested.OpsComplete == 1);

            // "ops" inside a string value must not start the count.
            AiCad.Ai.PlanStream decoy = new AiCad.Ai.PlanStream();
            decoy.Append("{\"notes\":\"mentions ops: [not real]\",\"ops\":[{\"op\":\"line\"}]}");
            Check("the real ops array is the one that counts", decoy.OpsComplete == 1);
        }

        /// <summary>
        /// A title block cell is a fixed width and the value is whatever the
        /// model wrote. Overflowing it put "DRAWN: AutoCAD Electrical" straight
        /// through the divider and on top of "DATE:".
        /// </summary>
        private static void TitleBlockFieldsStayInTheirCell()
        {
            Console.WriteLine();
            Console.WriteLine("-- title block fields stay in their cell");

            string text;
            double height;

            // The real case: a half-width cell of an A3 title block.
            const double Cell = 84;

            AiCad.Generators.TextFit.Fit("DRAWN: AutoCAD Electrical 2020", 4.0, Cell,
                                         out text, out height);
            Check("a long field is kept whole",
                  text == "DRAWN: AutoCAD Electrical 2020");
            Check("a long field is shrunk to fit",
                  height < 4.0 && AiCad.Generators.TextFit.Width(text, height) <= Cell + 0.001);

            AiCad.Generators.TextFit.Fit("SCALE: 1:1", 4.0, Cell, out text, out height);
            Check("a short field is left alone", text == "SCALE: 1:1" && height == 4.0);

            // Too long to shrink into: shrinking stops at the floor and the
            // value is cut instead of becoming unreadable.
            string huge = "DRAWN: " + new string('M', 120);
            AiCad.Generators.TextFit.Fit(huge, 4.0, Cell, out text, out height);
            Check("an impossible field stops shrinking at the floor",
                  height >= AiCad.Generators.TextFit.MinHeight);
            Check("an impossible field is truncated instead",
                  text.Length < huge.Length && text.EndsWith("…"));
            Check("a truncated field still fits",
                  AiCad.Generators.TextFit.Width(text, height) <= Cell + 0.001);

            AiCad.Generators.TextFit.Fit("anything", 4.0, 0, out text, out height);
            Check("no cell width means no constraint", text == "anything" && height == 4.0);

            AiCad.Generators.TextFit.Fit("", 4.0, Cell, out text, out height);
            Check("an empty field survives", text == "" && height == 4.0);
        }

        /// <summary>
        /// The solid-modelling guide is several hundred tokens. It has to reach
        /// 3D requests and stay out of schematics.
        /// </summary>
        private static void SolidGuideIsSpentOnlyOn3dRequests()
        {
            Console.WriteLine();
            Console.WriteLine("-- 3D guide is spent where it counts");

            AiCad.Config.AiCadConfig config = new AiCad.Config.AiCadConfig();
            const string marker = "SOLID MODELLING RULES";

            string spatial = AiCad.Ai.PromptBuilder.BuildSystemPrompt(
                config, null, "box: {...}", null, "3d robot arm, orbit around it");
            Check("a 3D request gets the guide", spatial.Contains(marker));
            Check("the guide states the profile convention",
                  spatial.Contains("[radius, height]"));

            string flat = AiCad.Ai.PromptBuilder.BuildSystemPrompt(
                config, null, "box: {...}", null, "single line diagram with an MCCB and 4 feeders");
            Check("a schematic does not pay for it", !flat.Contains(marker));

            string unknown = AiCad.Ai.PromptBuilder.BuildSystemPrompt(
                config, null, "box: {...}", null, null);
            Check("no request means no guide", !unknown.Contains(marker));

            Check("the older overload still compiles and runs",
                  AiCad.Ai.PromptBuilder.BuildSystemPrompt(config, null, "box: {...}")
                      .Contains("AVAILABLE OPERATIONS"));
        }

        private static void SamplePlanParses()
        {
            JsonValue root = JsonValue.ParseLenient(SamplePlan.Json);
            Check("sample plan parses", root != null);
            if (root == null) return;

            Check("sample plan has a name", root.GetString("name", "") .StartsWith("Sample enclosure"));
            Check("sample plan declares 6 layers", root.GetArray("layers").Count == 6);

            List<JsonValue> ops = root.GetArray("ops");
            Check("sample plan has 3 ops", ops.Count == 3);
            Check("first op is the enclosure generator", ops[0].GetString("op", "") == "enclosure");
            Check("enclosure width is 800", Math.Abs(ops[0].GetDouble("width", 0) - 800) < 1e-9);
            Check("enclosure asks for 3 rails", ops[0].GetInt("railCount", 0) == 3);
            Check("bolt pattern hole is 9 mm", Math.Abs(ops[1].GetDouble("holeDiameter", 0) - 9) < 1e-9);

            JsonValue origin = ops[0]["origin"];
            Check("origin is a 2-element array", origin != null && origin.Count == 2);
        }

        private static void RoundTripPreservesValues()
        {
            string source = "{\"a\":-12.5,\"b\":[1,2,3],\"c\":{\"d\":true,\"e\":null},\"f\":\"x\"}";
            JsonValue first = JsonValue.Parse(source);
            JsonValue second = JsonValue.Parse(first.ToString());

            Check("round-trip keeps a negative decimal", Math.Abs(second.GetDouble("a", 0) + 12.5) < 1e-12);
            Check("round-trip keeps array length", second.GetArray("b").Count == 3);
            Check("round-trip keeps nested bool", second["c"].GetBool("d", false));
            Check("round-trip keeps null", second["c"]["e"].Kind == JsonKind.Null);
            Check("round-trip keeps string", second.GetString("f", "") == "x");
        }

        private static void LenientParsingRecoversFromProse()
        {
            string reply = "Sure! Here is the plan you asked for:\n" +
                           "{\"name\":\"t\",\"ops\":[{\"op\":\"line\"}]}\n" +
                           "Let me know if you want changes.";
            JsonValue v = JsonValue.ParseLenient(reply);
            Check("prose-wrapped JSON is recovered", v != null && v.GetArray("ops").Count == 1);
        }

        private static void LenientParsingHandlesCodeFence()
        {
            string reply = "```json\n{\"name\":\"t\",\"ops\":[]}\n```";
            JsonValue v = JsonValue.ParseLenient(reply);
            Check("fenced JSON is recovered", v != null && v.GetString("name", "") == "t");
        }

        private static void EscapesSurviveRoundTrip()
        {
            string awkward = "line1\nline2 \"quoted\" back\\slash\ttab";
            JsonValue obj = JsonValue.NewObject();
            obj["v"] = JsonValue.New(awkward);
            JsonValue reparsed = JsonValue.Parse(obj.ToString());
            Check("escapes survive a round trip", reparsed.GetString("v", "") == awkward);

            JsonValue unicode = JsonValue.Parse("{\"v\":\"\\u00c5\\u03a9\"}");
            Check("unicode escapes decode", unicode.GetString("v", "") == "ÅΩ");
        }

        private static void NumberFormsParse()
        {
            JsonValue v = JsonValue.Parse("{\"a\":1e3,\"b\":-2.5E-2,\"c\":0,\"d\":1234567.89}");
            Check("exponent form parses", Math.Abs(v.GetDouble("a", 0) - 1000) < 1e-9);
            Check("negative exponent parses", Math.Abs(v.GetDouble("b", 0) + 0.025) < 1e-12);
            Check("zero parses", Math.Abs(v.GetDouble("c", 9)) < 1e-12);
            Check("large decimal parses", Math.Abs(v.GetDouble("d", 0) - 1234567.89) < 1e-6);
        }

        private static void MissingMembersFallBack()
        {
            JsonValue v = JsonValue.Parse("{\"a\":1}");
            Check("missing string falls back", v.GetString("nope", "fb") == "fb");
            Check("missing double falls back", Math.Abs(v.GetDouble("nope", 7) - 7) < 1e-12);
            Check("missing array is empty, not null", v.GetArray("nope").Count == 0);
            Check("missing member indexes to null", v["nope"] == null);
            Check("indexing a non-object is null", JsonValue.New(1.0)["x"] == null);
        }

        private static void MalformedJsonIsRejected()
        {
            bool threw = false;
            try { JsonValue.Parse("{\"a\":}"); }
            catch (Exception) { threw = true; }
            Check("malformed JSON throws in strict mode", threw);
            Check("malformed JSON is null in lenient mode", JsonValue.ParseLenient("{\"a\":}") == null);
            Check("empty input is null in lenient mode", JsonValue.ParseLenient("") == null);
        }

        private static void DeepNestingCounts()
        {
            string nested = "{\"ops\":[{\"op\":\"repeat\",\"ops\":[{\"op\":\"line\"},{\"op\":\"line\"}]}]}";
            JsonValue v = JsonValue.Parse(nested);
            List<JsonValue> ops = v.GetArray("ops");
            Check("nested ops are reachable", ops[0].GetArray("ops").Count == 2);
        }

        /// <summary>
        /// The app writes a request, the engine answers with a result. This is
        /// the whole contract between the two processes.
        /// </summary>
        private static void PlanInboxRoundTrips()
        {
            JsonValue plan = JsonValue.Parse("{\"name\":\"t\",\"ops\":[{\"op\":\"line\"}]}");
            string id = PlanInbox.WriteRequest(plan, true);
            Check("request gets an id", !string.IsNullOrEmpty(id));

            List<string> pending = PlanInbox.PendingRequests();
            bool found = false;
            string requestPath = null;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    requestPath = pending[i];
                }
            }
            Check("engine sees the queued request", found);

            if (requestPath != null)
            {
                JsonValue envelope = JsonValue.ParseLenient(
                    System.IO.File.ReadAllText(requestPath, System.Text.Encoding.UTF8));
                Check("envelope carries the id", envelope != null && envelope.GetString("id", "") == id);
                Check("envelope carries askForPoint", envelope.GetBool("askForPoint", false));
                Check("envelope carries the plan", envelope["plan"].GetArray("ops").Count == 1);
                System.IO.File.Delete(requestPath);
            }

            Check("no result before the engine answers", PlanInbox.TryTakeResult(id) == null);

            List<string> errors = new List<string>();
            errors.Add("bad op");
            PlanInbox.WriteResult(id, false, 0, errors, new List<string>());

            JsonValue result = PlanInbox.TryTakeResult(id);
            Check("app reads the result", result != null);
            if (result != null)
            {
                Check("result reports failure", !result.GetBool("success", true));
                Check("result carries the error text", result.GetArray("errors")[0].Text == "bad op");
            }
            Check("result is consumed once", PlanInbox.TryTakeResult(id) == null);
        }

        /// <summary>
        /// A stale dialog with an empty key box must not wipe the key on disk.
        /// This is the guarantee that stops the key needing to be re-entered.
        /// </summary>
        private static void StoredKeySurvivesAnEmptySave()
        {
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "aicad-keytest-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try
            {
                AiCad.Config.AiCadConfig saved = new AiCad.Config.AiCadConfig();
                saved.LoadedFrom = tmp;
                saved.Provider = "gemini";
                saved.GeminiApiKey = "SECRET-KEY-VALUE";
                saved.Save();

                JsonValue onDisk = JsonValue.Parse(
                    System.IO.File.ReadAllText(tmp, System.Text.Encoding.UTF8));
                Check("key is written", onDisk.GetString("geminiApiKey", "") == "SECRET-KEY-VALUE");

                // A dialog that never saw the key saves an empty box.
                AiCad.Config.AiCadConfig stale = new AiCad.Config.AiCadConfig();
                stale.LoadedFrom = tmp;
                stale.Provider = "gemini";
                stale.GeminiApiKey = "";
                stale.Save();

                onDisk = JsonValue.Parse(System.IO.File.ReadAllText(tmp, System.Text.Encoding.UTF8));
                Check("empty save does NOT wipe the stored key",
                      onDisk.GetString("geminiApiKey", "") == "SECRET-KEY-VALUE");

                // An explicit new value still replaces it.
                AiCad.Config.AiCadConfig changed = new AiCad.Config.AiCadConfig();
                changed.LoadedFrom = tmp;
                changed.Provider = "gemini";
                changed.GeminiApiKey = "NEW-KEY";
                changed.Save();

                onDisk = JsonValue.Parse(System.IO.File.ReadAllText(tmp, System.Text.Encoding.UTF8));
                Check("an explicit new key does replace it", onDisk.GetString("geminiApiKey", "") == "NEW-KEY");
            }
            finally
            {
                try { System.IO.File.Delete(tmp); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// A reply cut off mid-JSON leaves inner objects that still parse. Without
        /// demanding an "ops" key, one of those fragments was accepted as the plan
        /// and surfaced as a baffling "no geometry was produced".
        /// </summary>
        private static void TruncatedPlanIsNotMistakenForAPlan()
        {
            string truncated = "{\"name\":\"strip\",\"layers\":[{\"name\":\"AI-TEXT\",\"color\":2}]," +
                               "\"ops\":[{\"op\":\"line\",\"start\":[0,0],\"end\":[10,";

            JsonValue loose = JsonValue.ParseLenient(truncated);
            Check("a truncated reply still yields some fragment", loose != null);
            Check("that fragment is NOT the plan", loose == null || !loose.Has("ops"));

            Check("demanding 'ops' rejects the fragment",
                  JsonValue.ParseLenient(truncated, "ops") == null);

            string complete = "{\"name\":\"strip\",\"ops\":[{\"op\":\"line\"}]}";
            JsonValue good = JsonValue.ParseLenient(complete, "ops");
            Check("a complete plan is still accepted", good != null && good.GetArray("ops").Count == 1);

            string wrapped = "Here you go:" + Environment.NewLine + complete +
                             Environment.NewLine + "Hope that helps.";
            JsonValue fromProse = JsonValue.ParseLenient(wrapped, "ops");
            Check("prose-wrapped plan still recovered with a required key",
                  fromProse != null && fromProse.GetArray("ops").Count == 1);
        }

        /// <summary>
        /// Examples are expensive in tokens, so only the relevant ones are sent.
        /// An SLD request must not pull in an enclosure example.
        /// </summary>
        private static void ExamplesAreChosenByRelevance()
        {
            string folder = AiCad.Model.ExampleStore.UserFolder;
            System.Collections.Generic.List<string> written =
                new System.Collections.Generic.List<string>();

            try
            {
                JsonValue plan = JsonValue.Parse("{\"name\":\"x\",\"ops\":[{\"op\":\"line\"}]}");
                written.Add(AiCad.Model.ExampleStore.Save(
                    "TESTZZ single line diagram with busbar and mccb", null, plan));
                written.Add(AiCad.Model.ExampleStore.Save(
                    "TESTZZ enclosure with din rails and gland plate", null, plan));

                System.Collections.Generic.List<AiCad.Model.DrawingExample> picked =
                    AiCad.Model.ExampleStore.Select("single line diagram with a busbar", 1);

                Check("relevance picks exactly one when asked for one", picked.Count == 1);
                Check("relevance picks the matching example",
                      picked.Count == 1 && picked[0].Title.IndexOf("single line",
                          StringComparison.OrdinalIgnoreCase) >= 0);

                System.Collections.Generic.List<AiCad.Model.DrawingExample> other =
                    AiCad.Model.ExampleStore.Select("enclosure with din rails", 1);
                Check("a different request picks the other example",
                      other.Count == 1 && other[0].Title.IndexOf("enclosure",
                          StringComparison.OrdinalIgnoreCase) >= 0);

                string formatted = AiCad.Model.ExampleStore.Format(picked, 12000);
                Check("chosen examples render for the prompt",
                      formatted != null && formatted.IndexOf("Example 1") >= 0);

                Check("an oversized example is left out",
                      AiCad.Model.ExampleStore.Format(picked, 5) == null);
            }
            finally
            {
                for (int i = 0; i < written.Count; i++)
                {
                    try { AiCad.Model.ExampleStore.Delete(written[i]); }
                    catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------ runner

        private static void Check(string label, bool condition)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  ok    " + label);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + label);
            }
        }
    }
}
