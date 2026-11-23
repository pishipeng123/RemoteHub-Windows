using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteHub
{
    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log("UI", e.Exception);
                MessageBox.Show("发生未处理的错误：\r\n" + e.Exception.Message,
                    "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log("FATAL", e.ExceptionObject as Exception);
            };

            bool selfTest = false;
            bool smoke = false;
            string shotPath = null;
            bool shotDialog = false;
            bool shotOpen = false;
            bool shotTransfer = false;
            bool shotHistory = false;
            bool shotScan = false;
            bool shotLock = false;
            bool shotGrid = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/selftest", StringComparison.OrdinalIgnoreCase))
                        selfTest = true;
                    if (string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/smoke", StringComparison.OrdinalIgnoreCase))
                        smoke = true;
                    if (a.StartsWith("--shotscan=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(11);
                        shotScan = true;
                        shotDialog = false; shotOpen = false;
                        shotTransfer = false; shotHistory = false;
                    }
                    else if (a.StartsWith("--shotlock=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(11);
                        shotLock = true;
                        shotDialog = false; shotOpen = false;
                        shotTransfer = false; shotHistory = false;
                        Environment.SetEnvironmentVariable("RH_FORCE_LOCK", "1");
                    }
                    else if (a.StartsWith("--shotgrid=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(11);
                        shotGrid = true;
                        shotDialog = false; shotOpen = false;
                        shotTransfer = false; shotHistory = false;
                        Environment.SetEnvironmentVariable("RH_PREVIEW_TABS", "2");
                    }
                    else if (a.StartsWith("--shottransfer=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(15);
                        shotTransfer = true;
                        shotDialog = false;
                        shotOpen = false;
                    }
                    else if (a.StartsWith("--shothistory=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(14);
                        shotHistory = true;
                        shotDialog = false;
                        shotOpen = false;
                        shotTransfer = false;
                    }
                    else if (a.StartsWith("--shotdialog=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(13);
                        shotDialog = true;
                    }
                    else if (a.StartsWith("--shotopen=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(11);
                        shotDialog = false;
                        shotOpen = true;
                    }
                    else if (a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
                    {
                        shotPath = a.Substring(7);
                        shotDialog = false;
                        shotOpen = false;
                    }
                }
            }

            // Preview / test modes must never show the vault lock on startup.
            if (selfTest || smoke || shotPath != null)
                Environment.SetEnvironmentVariable("RH_PREVIEW", "1");

            if (selfTest)
            {
                string report;
                try { report = Diagnostics.Run(); }
                catch (Exception ex) { report = "自检过程异常：" + ex; }
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
                try { File.WriteAllText(path, report, System.Text.Encoding.UTF8); } catch { }
                return;
            }

            if (smoke)
            {
                try { SmokeTest.Run(); }
                catch (Exception ex) { Program.Log("SMOKE-FATAL", ex); }
                return;
            }

            if (shotPath != null)
            {
                try
                {
                    if (shotScan) RunScanShot(shotPath);
                    else if (shotLock || shotGrid) RunMainShot(shotPath, shotGrid);
                    else if (shotTransfer) RunTransferShot(shotPath);
                    else if (shotHistory) RunHistoryShot(shotPath);
                    else RunScreenshot(shotPath, shotDialog, shotOpen);
                }
                catch (Exception ex) { Log("SHOT", ex); }
                return;
            }

            Application.Run(new MainForm());
        }

        /// <summary>
        /// Hidden preview mode: open the real UI, optionally a dialog, capture the window
        /// to a PNG and exit. Used to verify the visual design on the target machine.
        /// </summary>
        private static void RunScreenshot(string path, bool withDialog, bool openFirst)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            if (withDialog)
            {
                // Show the add-connection dialog modally and capture the dialog itself.
                MainForm owner = new MainForm();
                owner.Shown += delegate
                {
                    ConnectionDialog d = new ConnectionDialog(new ConnectionInfo(), new List<string>(), true);
                    d.Shown += delegate
                    {
                        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 1200 };
                        t.Tick += delegate
                        {
                            t.Stop();
                            CaptureWindow(full, d);
                            d.Close();
                        };
                        t.Start();
                    };
                    d.ShowDialog(owner);
                    owner.Close();
                };
                Application.Run(owner);
            }
            else
            {
                MainForm f = new MainForm();
                f.Shown += delegate
                {
                    if (openFirst) f.PreviewOpenFirstHost();
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer
                    {
                        Interval = openFirst ? 3400 : 1100
                    };
                    t.Tick += delegate { t.Stop(); CaptureWindow(full, f); f.Close(); };
                    t.Start();
                };
                Application.Run(f);
            }
        }

        /// <summary>Main window preview, optionally with the grid wall or the lock overlay.</summary>
        private static void RunMainShot(string path, bool grid)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            MainForm f = new MainForm();
            f.Shown += delegate
            {
                if (grid) f.PreviewShowGrid();
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer
                {
                    Interval = grid ? 5000 : 1500
                };
                t.Tick += delegate { t.Stop(); CaptureWindow(full, f); f.Close(); };
                t.Start();
            };
            Application.Run(f);
        }

        /// <summary>Preview mode for the LAN scan dialog, filled with sample rows.</summary>
        private static void RunScanShot(string path)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            MainForm owner = new MainForm();
            owner.Shown += delegate
            {
                LanScanDialog d = new LanScanDialog();
                d.UseSamples();
                d.Shown += delegate
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 1200 };
                    t.Tick += delegate { t.Stop(); CaptureWindow(full, d); d.Close(); };
                    t.Start();
                };
                d.ShowDialog(owner);
                owner.Close();
            };
            Application.Run(owner);
        }

        /// <summary>Preview mode for the file-sharing dialog.</summary>
        private static void RunTransferShot(string path)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            MainForm owner = new MainForm();
            owner.Shown += delegate
            {
                FileTransferDialog d = new FileTransferDialog(null,
                    ConnectionStore.DefaultTransferFolder, "磁盘映射=开 / 剪贴板=开");
                d.Shown += delegate
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 1200 };
                    t.Tick += delegate { t.Stop(); CaptureWindow(full, d); d.Close(); };
                    t.Start();
                };
                d.ShowDialog(owner);
                owner.Close();
            };
            Application.Run(owner);
        }

        /// <summary>Preview mode for the session-history dialog, filled with sample rows.</summary>
        private static void RunHistoryShot(string path)
        {
            string full = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            MainForm owner = new MainForm();
            owner.Shown += delegate
            {
                SessionHistoryDialog d = new SessionHistoryDialog();
                d.UseSamples();
                d.Shown += delegate
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 1000 };
                    t.Tick += delegate { t.Stop(); CaptureWindow(full, d); d.Close(); };
                    t.Start();
                };
                d.ShowDialog(owner);
                owner.Close();
            };
            Application.Run(owner);
        }

        private static void CaptureWindow(string path, Form f)
        {
            Rectangle r = f.Bounds;
            using (Bitmap bmp = new Bitmap(r.Width, r.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        internal static void Log(string tag, Exception ex)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "RemoteHub-error.log");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " [" + tag + "] " +
                    (ex == null ? "(no exception)" : ex.ToString()) + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
