using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using AiCad.Ai;

namespace AiCadApp
{
    /// <summary>
    /// Late-bound COM connection to a running AutoCAD. Late binding keeps the
    /// app free of AutoCAD interop assemblies, so one build works against any
    /// installed release.
    /// </summary>
    public class AcadBridge
    {
        /// <summary>
        /// Probed in order. AutoCAD 2020 registers 23.1 and 23 but NOT the
        /// unversioned "AutoCAD.Application" - there is no CurVer key - so the
        /// versioned ids must be tried explicitly.
        /// </summary>
        private static readonly string[] ProgIds =
        {
            "AutoCAD.Application.25",    // 2025
            "AutoCAD.Application.24.3",  // 2024
            "AutoCAD.Application.24.2",  // 2023
            "AutoCAD.Application.24.1",  // 2022
            "AutoCAD.Application.24",    // 2021
            "AutoCAD.Application.23.1",  // 2020
            "AutoCAD.Application.23",
            "AutoCAD.Application"
        };

        private object _app;
        private string _progId;

        public bool IsConnected { get { return _app != null; } }
        public string ConnectedProgId { get { return _progId; } }

        // ------------------------------------------------------- connecting

        /// <summary>Attaches to an already-running AutoCAD. False if none is running.</summary>
        public bool TryConnect(out string error)
        {
            error = null;
            Release();

            List<string> failures = new List<string>();
            for (int i = 0; i < ProgIds.Length; i++)
            {
                try
                {
                    object app = Marshal.GetActiveObject(ProgIds[i]);
                    if (app == null) continue;
                    _app = app;
                    _progId = ProgIds[i];
                    return true;
                }
                catch (COMException ex)
                {
                    failures.Add(ProgIds[i] + ": 0x" + ex.ErrorCode.ToString("X8", CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    failures.Add(ProgIds[i] + ": " + ex.Message);
                }
            }

            error = "No running AutoCAD found.";
            return false;
        }

        /// <summary>
        /// Starts AutoCAD through COM. It takes a while to come up, so the
        /// caller should keep polling TryConnect rather than blocking.
        /// </summary>
        public bool Launch(out string error)
        {
            error = null;
            for (int i = 0; i < ProgIds.Length; i++)
            {
                try
                {
                    Type type = Type.GetTypeFromProgID(ProgIds[i]);
                    if (type == null) continue;

                    object app = Activator.CreateInstance(type);
                    if (app == null) continue;

                    _app = app;
                    _progId = ProgIds[i];
                    // Newly created instances start hidden.
                    Retry(delegate { Set(_app, "Visible", true); return true; });
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            if (error == null) error = "AutoCAD is not installed, or its COM server is not registered.";
            return false;
        }

        public void Release()
        {
            if (_app == null) return;
            try { Marshal.ReleaseComObject(_app); }
            catch (Exception) { }
            _app = null;
            _progId = null;
        }

        // ---------------------------------------------------------- queries

        /// <summary>"AutoCAD Electrical 2020 - [2nd Floor.dwg]", or null if unreachable.</summary>
        public string Caption
        {
            get
            {
                if (_app == null) return null;
                try { return Retry(delegate { return Get(_app, "Caption") as string; }); }
                catch (Exception) { return null; }
            }
        }

        public string ActiveDrawingName
        {
            get
            {
                if (_app == null) return null;
                try
                {
                    return Retry(delegate
                    {
                        object doc = Get(_app, "ActiveDocument");
                        return doc == null ? null : Get(doc, "Name") as string;
                    });
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>Cheap liveness probe; false once AutoCAD has been closed.</summary>
        public bool IsAlive()
        {
            if (_app == null) return false;
            try
            {
                Retry(delegate { return Get(_app, "Caption"); });
                return true;
            }
            catch (Exception)
            {
                Release();
                return false;
            }
        }

        /// <summary>
        /// The active drawing, or null when there is none. AutoCAD's Start tab
        /// is not a document, so ActiveDocument throws rather than returning
        /// null when nothing is open - a distinction worth hiding here.
        /// </summary>
        private object TryGetActiveDocument()
        {
            try
            {
                return Get(_app, "ActiveDocument");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when a drawing is open, not just the Start tab.</summary>
        public bool HasDrawing
        {
            get
            {
                if (_app == null) return false;
                try { return Retry(delegate { return TryGetActiveDocument() != null; }); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Runs a command string in the active drawing, creating a blank drawing
        /// first if AutoCAD is sitting on the Start tab. The user asked for a
        /// drawing operation, so needing somewhere to put it is implied - making
        /// them go and press Ctrl+N is friction for nothing.
        /// </summary>
        public void SendCommand(string command)
        {
            if (_app == null) throw new InvalidOperationException("Not connected to AutoCAD.");
            WaitUntilIdle(20000);
            Retry(delegate
            {
                object doc = TryGetActiveDocument();

                // Sitting on the Start tab reports no ACTIVE document even when
                // drawings are open. Using one of those beats making a new one.
                if (doc == null) doc = FirstOpenDocument();

                if (doc == null)
                {
                    doc = CreateDocument();
                    if (doc == null)
                        throw new InvalidOperationException(
                            "AutoCAD has no drawing open and a new one could not be created. " +
                            "Open a drawing in AutoCAD, then try again.");
                }
                Invoke(doc, "SendCommand", new object[] { command });
                return true;
            });
        }

        /// <summary>
        /// Any open drawing, for when AutoCAD reports no ACTIVE one. That happens
        /// whenever the Start tab is in front, and it is not a reason to create
        /// another drawing on top of the ones already open.
        /// </summary>
        private object FirstOpenDocument()
        {
            object documents = Get(_app, "Documents");
            if (documents == null) return null;

            object count = Get(documents, "Count");
            if (!(count is int) || (int)count <= 0) return null;

            try { return Invoke(documents, "Item", new object[] { 0 }); }
            catch (COMException) { throw; }        // busy: let Retry handle it
            catch (Exception) { return null; }
        }

        /// <summary>The name of the drawing currently in front, or null.</summary>
        public string ActiveDocumentName
        {
            get
            {
                try
                {
                    return Retry<string>(delegate
                    {
                        object doc = TryGetActiveDocument();
                        if (doc == null) return null;
                        object name = Get(doc, "Name");
                        return name == null ? null : name.ToString();
                    });
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Starts a new empty drawing and returns its name.
        ///
        /// A chat about a control schematic and a chat about a 3D model have no
        /// business sharing a sheet, but everything drawn went into whatever
        /// document happened to be open. Giving each conversation its own drawing
        /// is what keeps them apart.
        /// </summary>
        public string NewDocument()
        {
            if (_app == null) throw new InvalidOperationException("Not connected to AutoCAD.");
            WaitUntilIdle(20000);
            return Retry<string>(delegate
            {
                object created = CreateDocument();
                if (created == null) return null;
                object name = Get(created, "Name");
                return name == null ? null : name.ToString();
            });
        }

        /// <summary>
        /// Brings a drawing to the front by name. False when it is no longer
        /// open - the caller then starts a fresh one rather than drawing into
        /// whatever else happens to be showing.
        /// </summary>
        public bool ActivateDocument(string name)
        {
            if (_app == null || string.IsNullOrEmpty(name)) return false;
            WaitUntilIdle(20000);
            try
            {
                return Retry<bool>(delegate
                {
                    object documents = Get(_app, "Documents");
                    if (documents == null) return false;

                    object countValue = Get(documents, "Count");
                    int count = countValue == null ? 0 : Convert.ToInt32(countValue);
                    for (int i = 0; i < count; i++)
                    {
                        object doc = Invoke(documents, "Item", new object[] { i });
                        if (doc == null) continue;

                        object docName = Get(doc, "Name");
                        if (docName == null) continue;
                        if (!string.Equals(docName.ToString(), name, StringComparison.OrdinalIgnoreCase))
                            continue;

                        Invoke(doc, "Activate", new object[] { });
                        return true;
                    }
                    return false;
                });
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Adds an empty drawing and returns it, or null if that fails.</summary>
        /// <summary>
        /// Adds a blank drawing and returns it.
        ///
        /// Deliberately does NOT swallow exceptions. It used to, and that quietly
        /// defeated the whole retry mechanism: every caller runs inside Retry,
        /// which exists to wait out "call rejected by callee" while AutoCAD is
        /// busy, but catching the COMException here turned a passing busy signal
        /// into a null and then into a hard "no drawing could be created". That
        /// is why the failure clustered right after heavy 3D work, when AutoCAD
        /// is regenerating and rejecting calls for a while. Letting it throw lets
        /// Retry do its job, and lets a genuine fault report what it actually was.
        /// </summary>
        private object CreateDocument()
        {
            object documents = Get(_app, "Documents");
            if (documents == null) return null;

            // A drawing cannot be added while AutoCAD is mid-command.
            WaitUntilIdle(20000);
            Invoke(documents, "Add", new object[] { });
            return TryGetActiveDocument();
        }

        /// <summary>
        /// Reads the layer and block names the model needs as context. Block
        /// names matter most: the engine refuses to insert a block that is not
        /// actually in the drawing.
        /// </summary>
        public DrawingSnapshot CaptureSnapshot()
        {
            DrawingSnapshot snapshot = new DrawingSnapshot();
            if (_app == null) return snapshot;

            try
            {
                Retry(delegate
                {
                    object doc = Get(_app, "ActiveDocument");
                    if (doc == null) return false;

                    string name = Get(doc, "Name") as string;
                    if (!string.IsNullOrEmpty(name)) snapshot.FileName = name;

                    try
                    {
                        object tilemode = Invoke(doc, "GetVariable", new object[] { "TILEMODE" });
                        snapshot.SpaceName = Convert.ToInt32(tilemode, CultureInfo.InvariantCulture) == 1
                            ? "Model" : "Paper";
                    }
                    catch (Exception) { }

                    try
                    {
                        object insunits = Invoke(doc, "GetVariable", new object[] { "INSUNITS" });
                        snapshot.UnitsDescription =
                            DescribeUnits(Convert.ToInt32(insunits, CultureInfo.InvariantCulture));
                    }
                    catch (Exception) { }

                    snapshot.LayerList = string.Join(", ", ReadNames(doc, "Layers", 60, false).ToArray());
                    snapshot.BlockList = string.Join(", ", ReadNames(doc, "Blocks", 80, true).ToArray());
                    return true;
                });
            }
            catch (Exception)
            {
                // A snapshot is a nicety; the request still works without it.
            }

            return snapshot;
        }

        /// <summary>
        /// Indexed access rather than foreach: late-bound COM collections do not
        /// reliably support IEnumerable through IDispatch.
        /// </summary>
        private List<string> ReadNames(object doc, string collectionName, int max, bool skipLayouts)
        {
            List<string> names = new List<string>();
            try
            {
                object collection = Get(doc, collectionName);
                if (collection == null) return names;

                int count = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
                for (int i = 0; i < count && names.Count < max; i++)
                {
                    try
                    {
                        object item = Invoke(collection, "Item", new object[] { i });
                        if (item == null) continue;

                        string name = Get(item, "Name") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.StartsWith("*", StringComparison.Ordinal)) continue;

                        if (skipLayouts)
                        {
                            try
                            {
                                object isLayout = Get(item, "IsLayout");
                                if (isLayout != null && Convert.ToBoolean(isLayout, CultureInfo.InvariantCulture))
                                    continue;
                            }
                            catch (Exception) { }
                        }

                        names.Add(name);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return names;
        }

        private static string DescribeUnits(int insunits)
        {
            switch (insunits)
            {
                case 0: return "unset - treat 1 unit as 1 mm";
                case 1: return "1 unit = 25.4 mm (inches)";
                case 2: return "1 unit = 304.8 mm (feet)";
                case 4: return "1 unit = 1 mm";
                case 5: return "1 unit = 10 mm (cm)";
                case 6: return "1 unit = 1000 mm (m)";
                default: return "INSUNITS " + insunits.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ---------------------------------------------------- COM plumbing

        private const int RpcCallRejected = unchecked((int)0x80010001);
        private const int RpcServerCallRetryLater = unchecked((int)0x8001010A);

        /// <summary>
        /// AutoCAD refuses COM calls while it is mid-command or showing a modal
        /// dialog. Those refusals are transient, so they are retried rather than
        /// surfaced as failures.
        /// </summary>
        private static T Retry<T>(Func<T> action)
        {
            // Twelve quarter-second tries covered a passing hiccup, but not real
            // work: regenerating a shaded model of a few dozen solids keeps
            // AutoCAD busy for much longer than three seconds, and the call was
            // then reported as a hard failure. Twenty seconds covers it.
            const int attempts = 40;
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    return action();
                }
                catch (COMException ex)
                {
                    bool busy = ex.ErrorCode == RpcCallRejected || ex.ErrorCode == RpcServerCallRetryLater;
                    if (!busy || i == attempts - 1) throw;
                    Thread.Sleep(500);
                }
            }
            throw new InvalidOperationException("AutoCAD stayed busy.");
        }

        /// <summary>
        /// Waits until AutoCAD is not mid-command. Retrying a rejected call works,
        /// but not starting one while it is plainly busy is cheaper and avoids
        /// interrupting whatever it is doing.
        /// </summary>
        private void WaitUntilIdle(int millisecondsToWait)
        {
            int waited = 0;
            while (waited < millisecondsToWait)
            {
                try
                {
                    object state = Invoke(_app, "GetAcadState", new object[0]);
                    if (state == null) return;
                    object quiescent = Get(state, "IsQuiescent");
                    if (quiescent is bool && (bool)quiescent) return;
                }
                catch (Exception)
                {
                    return;   // cannot ask, so just let the retry handle it
                }

                Thread.Sleep(250);
                waited += 250;
            }
        }

        /// <summary>
        /// Late binding wraps every failure in a TargetInvocationException, whose
        /// own message ("Exception has been thrown by the target of an
        /// invocation") says nothing. Worse, it hides the COMException that the
        /// busy-retry logic looks for, so transient refusals never got retried.
        /// Unwrapping restores both the real message and the retry behaviour.
        /// </summary>
        private static Exception Unwrap(System.Reflection.TargetInvocationException ex)
        {
            return ex.InnerException != null ? ex.InnerException : (Exception)ex;
        }

        private static object Get(object target, string property)
        {
            try
            {
                return target.GetType().InvokeMember(property,
                    System.Reflection.BindingFlags.GetProperty, null, target, null);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw Unwrap(ex);
            }
        }

        private static void Set(object target, string property, object value)
        {
            try
            {
                target.GetType().InvokeMember(property,
                    System.Reflection.BindingFlags.SetProperty, null, target, new object[] { value });
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw Unwrap(ex);
            }
        }

        private static object Invoke(object target, string method, object[] args)
        {
            try
            {
                return target.GetType().InvokeMember(method,
                    System.Reflection.BindingFlags.InvokeMethod, null, target, args);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw Unwrap(ex);
            }
        }
    }
}
