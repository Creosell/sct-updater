using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Updater
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.ThreadException += (sender, e) =>
            {
                Log.Error(e.Exception, "Unhandled UI exception.");
                MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nDetails are in logs\\sct_updater.log.", "Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                Log.Error(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "Unhandled exception.");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
