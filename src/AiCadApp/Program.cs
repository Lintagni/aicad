using System;
using System.Threading;
using System.Windows.Forms;

namespace AiCadApp
{
    public static class Program
    {
        /// <summary>
        /// STAThread is required: the app talks to AutoCAD over COM, and the
        /// WinForms UI needs a single-threaded apartment either way.
        /// </summary>
        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A COM hiccup should show a message, not vanish the window.
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                Report(ex);
                return 1;
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Report(e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Report(e.ExceptionObject as Exception);
        }

        private static void Report(Exception ex)
        {
            string message = ex == null ? "Unknown error." : ex.Message;
            try
            {
                MessageBox.Show(message, "AiCad", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }
    }
}
