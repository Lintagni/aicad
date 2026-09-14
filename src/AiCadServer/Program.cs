using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace AiCadServer
{
    /// <summary>
    /// Starts the local server, opens the browser at it, and sits in the tray.
    /// </summary>
    public static class Program
    {
        private const int DefaultPort = 8731;

        private static Session _session;
        private static WebServer _server;
        private static NotifyIcon _tray;

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool noBrowser = false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--no-browser", StringComparison.OrdinalIgnoreCase))
                    noBrowser = true;

            try
            {
                _session = new Session();
                _server = new WebServer(_session, DefaultPort);
            }
            catch (Exception ex)
            {
                MessageBox.Show("AiCad could not start: " + ex.Message, "AiCad",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Thread listener = new Thread(_server.Run);
            listener.IsBackground = true;
            listener.Start();

            // The token travels in the opening URL only; the page then keeps it
            // and sends it as a header on every call.
            string url = _server.Url + "?t=" + _server.Token;
            if (!noBrowser) OpenBrowser(url);

            BuildTray(url);

            Application.Run();

            _session.Save();
            _server.Stop();
            if (_tray != null) _tray.Visible = false;
            return 0;
        }

        private static void OpenBrowser(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        /// <summary>
        /// A tray icon rather than a window: the UI lives in the browser, but
        /// the process still needs somewhere to be closed from.
        /// </summary>
        private static void BuildTray(string url)
        {
            _tray = new NotifyIcon();
            _tray.Icon = System.Drawing.SystemIcons.Application;
            _tray.Text = "AiCad server";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open AiCad", null, delegate { OpenBrowser(url); });
            menu.Items.Add("Copy address", null, delegate
            {
                try { Clipboard.SetText(url); }
                catch (Exception) { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { Application.Exit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenBrowser(url); };
        }
    }
}
