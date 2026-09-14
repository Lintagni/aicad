using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AiCad.Config;
using AiCad.Generators;
using AiCad.Ops;

namespace AiCad.Execution
{
    /// <summary>The single registry of everything the AI is allowed to draw.</summary>
    public static class Capabilities
    {
        private static OpRegistry _registry;
        private static readonly object Gate = new object();

        public static OpRegistry Registry
        {
            get
            {
                lock (Gate)
                {
                    if (_registry == null)
                    {
                        OpRegistry r = new OpRegistry();
                        Primitives.RegisterAll(r);
                        GeneratorPack.RegisterAll(r);
                        _registry = r;
                    }
                    return _registry;
                }
            }
        }

        /// <summary>
        /// The capability list injected into the system prompt. Because it is
        /// generated from the registry, a newly registered op is advertised to
        /// the model with no prompt editing.
        /// </summary>
        public static string UsageCatalogue()
        {
            List<string> lines = new List<string>();
            foreach (IOp op in Registry.All) lines.Add("  " + op.Usage);
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>
        /// Path the standalone app reads to learn what this engine can draw.
        /// Publishing it from the registry keeps the app's prompt in step with
        /// whatever engine version is actually installed.
        /// </summary>
        public static string CataloguePath
        {
            get { return Path.Combine(AiCadConfig.Folder, "capabilities.txt"); }
        }

        /// <summary>Called on plug-in load. Failure here is never fatal.</summary>
        public static void PublishCatalogue()
        {
            try
            {
                Directory.CreateDirectory(AiCadConfig.Folder);
                File.WriteAllText(CataloguePath, UsageCatalogue(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The app falls back to reporting that the engine is unavailable.
            }
        }
    }
}
