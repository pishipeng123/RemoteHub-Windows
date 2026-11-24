using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
            bool layoutTest = false;
            string shotPath = null;
            bool shotDialog = false;
            bool shotOpen = false;
            bool shotTransfer = false;
            bool shotHistory = false;
            bool shotScan = false;
            bool shotLock = false;
            bool shotGrid = false;
            int shotWidth = 0;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/selftest", StringComparison.OrdinalIgnoreCase))
                        selfTest = true;
                    if (string.Equals(a, "--layouttest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/layouttest", StringComparison.OrdinalIgnoreCase))
                        layoutTest = true;
                    if (string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/smoke", StringComparison.OrdinalIgnoreCase))
                        smoke = true;
                    if (a.StartsWith("--shotw=", StringComparison.OrdinalIgnoreCase))
                    {
                        int tmp;
                        if (int.TryParse(a.Substring(8), out tmp) && tmp > 400) shotWidth = tmp;
                    }
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
            if (selfTest || smoke || layoutTest || shotPath != null)
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

            if (layoutTest)
            {
                int rc = 1;
                try { rc = RunLayoutTest(); }
                catch (Exception ex) { Program.Log("LAYOUT-FATAL", ex); }
                Environment.Exit(rc);
            }

            if (shotPath != null)
            {
                try
                {
                    if (shotScan) RunScanShot(shotPath);
                    else if (shotLock || shotGrid) RunMainShot(shotPath, shotGrid);
                    else if (shotTransfer) RunTransferShot(shotPath);
                    else if (shotHistory) RunHistoryShot(shotPath);
                    else RunScreenshot(shotPath, shotDialog, shotOpen, shotWidth);
                }
                catch (Exception ex) { Log("SHOT", ex); }
                return;
            }

            Application.Run(new MainForm());
        }

        /// <summary>
        /// Layout regression check: the caption buttons (close / maximize / minimize) are
        /// placed by hand, so they have to re-anchor whenever the window width changes.
        /// This walks the real window through several widths - growing, shrinking and
        /// shrinking back - and checks that they stay glued to the right edge.
        /// Returns 0 when every width passes, 1 otherwise.
        /// </summary>
        private static int RunLayoutTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RemoteHub 标题栏布局自检  " + DateTime.Now.ToString("s"));
            sb.AppendLine();

            int[] widths = { 1040, 1280, 1600, 1920, 1100, 1040 };
            int failures = 0;
            int checks = 0;
            StringBuilder detail = new StringBuilder();

            MainForm form = new MainForm();
            form.Shown += delegate
            {
                object header = GetField(form, "_header");
                object close = GetField(form, "_btnClose");
                object max = GetField(form, "_btnMax");
                object min = GetField(form, "_btnMin");
                if (header == null || close == null || max == null || min == null)
                {
                    detail.AppendLine("  [FAIL] 无法读取标题栏控件");
                    failures++;
                }
                else
                {
                    // (label, header width, close-button right edge, minimize right edge)
                    Action<string, int> check = delegate(string label, int expect)
                    {
                        form.PerformLayout();
                        Application.DoEvents();
                        form.PerformLayout();

                        int hw = GetInt(header, "Width");
                        int cRight = GetInt(close, "Left") + GetInt(close, "Width");
                        int mRight = GetInt(max, "Left") + GetInt(max, "Width");
                        int nRight = GetInt(min, "Left") + GetInt(min, "Width");

                        bool flush = cRight == hw;
                        bool chained = mRight == GetInt(close, "Left") && nRight == GetInt(max, "Left");
                        bool onScreen = GetInt(close, "Left") >= 0 && cRight <= hw;
                        checks++;

                        string mark = (flush && chained && onScreen) ? "OK  " : "FAIL";
                        if (!(flush && chained && onScreen)) failures++;

                        detail.AppendLine(string.Format("  [{0}] {1,-22} 标题栏宽 {2,4}  关闭键右缘 {3,4}  差 {4,4}  {5}",
                            mark, label, hw, cRight, hw - cRight,
                            (flush && chained && onScreen) ? "贴住右缘" : "错位"));
                    };

                    foreach (int w in widths)
                    {
                        form.Size = new Size(w, 800);
                        check("宽度 " + w, w);
                    }

                    // Maximising switches the frame padding off, which changes the header
                    // width by a couple of pixels; restoring must not leave the buttons
                    // stranded at the old position either.
                    try
                    {
                        form.WindowState = FormWindowState.Maximized;
                        Application.DoEvents();
                        check("最大化", 0);
                        form.WindowState = FormWindowState.Normal;
                        Application.DoEvents();
                        check("还原", 0);
                    }
                    catch (Exception ex)
                    {
                        detail.AppendLine("  [SKIP] 最大化检查跳过: " + ex.Message);
                    }
                }
                form.Close();
            };

            Application.Run(form);

            sb.Append(detail);
            sb.AppendLine();
            sb.AppendLine("失败项 : " + failures + " / " + checks);
            sb.AppendLine(failures == 0 ? "结果   : 全部通过" : "结果   : 存在错位");

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouttest.log");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }
            Console.Write(sb.ToString());
            return failures == 0 ? 0 : 1;
        }

        private static object GetField(object target, string name)
        {
            FieldInfo fi = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return fi == null ? null : fi.GetValue(target);
        }

        private static int GetInt(object target, string property)
        {
            PropertyInfo pi = target.GetType().GetProperty(property,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (pi == null) return -1;
            object v = pi.GetValue(target, null);
            return v is int ? (int)v : -1;
        }

        /// <summary>
        /// Hidden preview mode: open the real UI, optionally a dialog, capture the window
        /// to a PNG and exit. Used to verify the visual design on the target machine.
        /// </summary>
        private static void RunScreenshot(string path, bool withDialog, bool openFirst, int width)
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
                    t.Tick += delegate
                    {
                        t.Stop();
                        CaptureWindow(full, f);
                        f.Close();
                    };
                    t.Start();
                    // Resize after the window is already up, so the caption buttons have
                    // to re-anchor the way they do while the user drags the window edge.
                    if (width > 0)
                    {
                        System.Windows.Forms.Timer rt = new System.Windows.Forms.Timer { Interval = 500 };
                        rt.Tick += delegate
                        {
                            rt.Stop();
                            f.Size = new Size(width, f.Size.Height);
                        };
                        rt.Start();
                    }
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
