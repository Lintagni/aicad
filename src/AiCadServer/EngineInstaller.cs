using System;
using System.IO;

namespace AiCadServer
{
    /// <summary>
    /// Puts the drawing engine where AutoCAD looks for plug-ins, so a first-time
    /// user never has to run an installer or type NETLOAD.
    /// </summary>
    public static class EngineInstaller
    {
        private const string BundleName = "AiCad.bundle";

        public static string BundleFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk\\ApplicationPlugins\\" + BundleName);
            }
        }

        /// <summary>Copies the engine and its manifest; returns the bundle path.</summary>
        public static string Install()
        {
            string here = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";

            string dll = Path.Combine(here, "AiCad.dll");
            if (!File.Exists(dll))
                throw new FileNotFoundException("AiCad.dll is missing from " + here + ".");

            string contents = Path.Combine(BundleFolder, "Contents");
            Directory.CreateDirectory(contents);

            string target = Path.Combine(contents, "AiCad.dll");
            try
            {
                File.Copy(dll, target, true);
            }
            catch (IOException)
            {
                // AutoCAD holds the file for the life of its session.
                throw new IOException(
                    "AutoCAD currently has the engine loaded. Close AutoCAD and try again.");
            }

            File.WriteAllText(Path.Combine(BundleFolder, "PackageContents.xml"), Manifest());
            return BundleFolder;
        }

        /// <summary>
        /// True when AutoCAD is running an engine older than the installed file.
        ///
        /// Copying a new DLL into the bundle is not enough: AutoCAD keeps the
        /// version it loaded for the life of its session. The engine republishes
        /// its capability list every time it loads, so a capability list older
        /// than the DLL means the running engine is behind - and the model is
        /// being told about the wrong set of operations.
        /// </summary>
        public static bool NeedsAutoCadRestart()
        {
            try
            {
                string installed = Path.Combine(BundleFolder, "Contents\\AiCad.dll");
                string catalogue = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AiCad\\capabilities.txt");

                if (!File.Exists(installed) || !File.Exists(catalogue)) return false;
                return File.GetLastWriteTimeUtc(catalogue) < File.GetLastWriteTimeUtc(installed);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The engine version AutoCAD has loaded, for the footer and the settings
        /// panel. Falls back to the build date when the DLL carries no version.
        /// </summary>
        public static string InstalledVersion()
        {
            try
            {
                string installed = Path.Combine(BundleFolder, "Contents\\AiCad.dll");
                if (!File.Exists(installed)) return "not installed";

                // The engine carries no version resource, so an all-zero version
                // says nothing; the build date is what actually distinguishes
                // one engine from the next.
                System.Diagnostics.FileVersionInfo info =
                    System.Diagnostics.FileVersionInfo.GetVersionInfo(installed);
                string version = info.FileVersion;
                if (!string.IsNullOrEmpty(version) && version.Replace("0", "").Replace(".", "").Length > 0)
                    return "v" + version;

                return "built " + File.GetLastWriteTime(installed)
                    .ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>True when the installed engine matches the one shipped here.</summary>
        public static bool IsUpToDate()
        {
            try
            {
                string here = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string source = Path.Combine(here, "AiCad.dll");
                string installed = Path.Combine(BundleFolder, "Contents\\AiCad.dll");

                if (!File.Exists(source) || !File.Exists(installed)) return false;
                return File.GetLastWriteTimeUtc(installed) >= File.GetLastWriteTimeUtc(source);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Demand-loading manifest: AutoCAD starts normally and pulls the engine
        /// in the first time an AICAD command is used.
        /// </summary>
        private static string Manifest()
        {
            return
"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
"<ApplicationPackage SchemaVersion=\"1.0\"\r\n" +
"                    AutodeskProduct=\"AutoCAD\"\r\n" +
"                    ProductType=\"Application\"\r\n" +
"                    Name=\"AiCad\"\r\n" +
"                    AppVersion=\"1.0.0\"\r\n" +
"                    Description=\"AI drawing assistant\"\r\n" +
"                    ProductCode=\"{9F3C1A72-5D84-4E63-B0A1-2C7E4D6F8B95}\">\r\n" +
"  <CompanyDetails Name=\"AiCad\" />\r\n" +
"  <Components Description=\"AiCad assistant\">\r\n" +
"    <RuntimeRequirements OS=\"Win64\" Platform=\"AutoCAD*\" SeriesMin=\"R23.1\" SeriesMax=\"R26.0\" />\r\n" +
"    <ComponentEntry AppName=\"AiCad\"\r\n" +
"                    Version=\"1.0.0\"\r\n" +
"                    ModuleName=\"./Contents/AiCad.dll\"\r\n" +
"                    AppDescription=\"AI drawing assistant\"\r\n" +
"                    LoadOnAutoCADStartup=\"False\"\r\n" +
"                    LoadOnCommandInvocation=\"True\">\r\n" +
"      <Commands GroupName=\"AICAD_GROUP\">\r\n" +
"        <Command Global=\"AICAD\" Local=\"AICAD\" />\r\n" +
"        <Command Global=\"AICADCONFIG\" Local=\"AICADCONFIG\" />\r\n" +
"        <Command Global=\"AICADDRAW\" Local=\"AICADDRAW\" />\r\n" +
"        <Command Global=\"AICADSYNC\" Local=\"AICADSYNC\" />\r\n" +
"        <Command Global=\"AICADTEST\" Local=\"AICADTEST\" />\r\n" +
"        <Command Global=\"AICADOPS\" Local=\"AICADOPS\" />\r\n" +
"      </Commands>\r\n" +
"    </ComponentEntry>\r\n" +
"  </Components>\r\n" +
"</ApplicationPackage>\r\n";
        }
    }
}
