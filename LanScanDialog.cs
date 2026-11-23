using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// Sweeps an IP range for open RDP ports, then turns the checked results into
    /// address-book entries. All probing happens on the thread pool; only the list
    /// updates are marshalled back to the UI thread.
    /// </summary>
    internal sealed class LanScanDialog : Form
    {
        private readonly TextBox _range = new TextBox();
        private readonly NumericUpDown _port = new NumericUpDown();
        private readonly NumericUpDown _timeout = new NumericUpDown();
        private readonly ListView _list = new ListView();
        private readonly ScanBar _bar = new ScanBar();
        private readonly Label _stat = new Label();
        private readonly RhButton _start = new RhButton();
        private CancellationTokenSource _cts;
        private bool _running;
        private int _done, _total, _found;

        /// <summary>
        /// Thin self-drawn progress line. The stock WinForms bar renders as a chunky
        /// green block, which does not belong next to the flat UI.
        /// </summary>
        private sealed class ScanBar : Control
        {
            public int Percent;

            public ScanBar()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Height = 4;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush b = new SolidBrush(Ui.Border))
                    g.FillRectangle(b, new Rectangle(0, 0, Width, Height));
                int pct = Percent;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                int w = (int)Math.Round(Width * pct / 100.0);
                if (w > 0)
                    using (SolidBrush b = new SolidBrush(Ui.Accent))
                        g.FillRectangle(b, new Rectangle(0, 0, w, Height));
            }
        }

        public List<ConnectionInfo> Selected { get; private set; }

        public LanScanDialog()
        {
            Selected = new List<ConnectionInfo>();
            Dlg.Init(this, 620, 522, "局域网扫描");
            Controls.Add(Dlg.Header(this, 620, "发现远程主机",
                "扫描 IP 段内的 RDP 端口", UiIcon.Search));

            // ---- range card ----
            Panel top = Dlg.Card(16, 78, 588, 122, "扫描范围", UiIcon.Server);
            _range.Text = "192.168.1.1-254";
            Panel rw = Dlg.Wrap(_range, 28);
            Dlg.AddRow(top, 36, "IP 段", rw, 330);
            _port.Minimum = 1; _port.Maximum = 65535; _port.Value = 3389;
            Dlg.AddRow(top, 74, "端口", Dlg.Wrap(_port, 28), 90);
            _timeout.Minimum = 200; _timeout.Maximum = 3000; _timeout.Value = 600;
            _timeout.Increment = 100;
            Dlg.AddRow(top, 74, "超时 ms", Dlg.Wrap(_timeout, 28), 90);
            // Move the timeout row to the right half of the card.
            Control timeoutWrap = top.Controls[top.Controls.Count - 1];
            Label timeoutLabel = top.Controls[top.Controls.Count - 2] as Label;
            if (timeoutWrap != null) timeoutWrap.Left = 340;
            if (timeoutLabel != null) timeoutLabel.Left = 256;

            _start.Text = "开始扫描";
            _start.Icon = UiIcon.Play;
            _start.Kind = RhButtonKind.Primary;
            _start.Size = new Size(104, 30);
            _start.Location = new Point(460, 74);
            _start.Click += delegate { if (_running) StopScan(); else StartScan(); };
            top.Controls.Add(_start);
            Controls.Add(top);

            // ---- results card ----
            Panel res = Dlg.Card(16, 212, 588, 254, "扫描结果", UiIcon.Monitor);
            _list.SetBounds(16, 36, 556, 174);
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.BackColor = Ui.Field;
            _list.ForeColor = Ui.Text;
            _list.Font = Ui.NewF(9F);
            _list.BorderStyle = BorderStyle.None;
            _list.Columns.Add("主机地址", 190);
            _list.Columns.Add("端口", 80);
            _list.Columns.Add("状态", 160);
            _list.Columns.Add("耗时", 90);
            res.Controls.Add(_list);

            _bar.SetBounds(16, 220, 420, 4);
            res.Controls.Add(_bar);

            _stat.Text = "填写 IP 段后点「开始扫描」。";
            _stat.SetBounds(16, 232, 556, 18);
            _stat.Font = Ui.NewF(8.5F);
            _stat.ForeColor = Ui.TextMuted;
            _stat.BackColor = Color.Transparent;
            _stat.Tag = "muted";
            res.Controls.Add(_stat);
            Controls.Add(res);

            // ---- footer ----
            Panel footer = Dlg.Footer(620);
            RhButton close = Dlg.Button("关闭", UiIcon.Close, RhButtonKind.Ghost, 84);
            close.Click += delegate
            {
                if (_running) StopScan();
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Dlg.PlaceRight(footer, 620, close, 0);

            RhButton add = Dlg.Button("加入台账", UiIcon.Plus, RhButtonKind.Primary, 110);
            add.Click += delegate { AcceptSelection(); };
            Dlg.PlaceRight(footer, 620, add, 92);

            RhButton all = Dlg.Button("全选 / 全不选", UiIcon.Check, RhButtonKind.Ghost, 128);
            all.Location = new Point(14, 10);
            all.Click += delegate
            {
                bool turnOn = false;
                foreach (ListViewItem it in _list.Items) if (!it.Checked) { turnOn = true; break; }
                foreach (ListViewItem it in _list.Items) it.Checked = turnOn;
            };
            footer.Controls.Add(all);
            Controls.Add(footer);

            Ui.ApplyTheme(this);
            // Do not open with the whole range selected (looks like an error state).
            Shown += delegate { _range.Select(0, 0); };
        }

        private void StartScan()
        {
            string err;
            List<string> ips = NetProbe.ExpandRange(_range.Text, out err);
            if (err != null) { MessageBox.Show(err, "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (ips.Count == 0) return;
            int port = (int)_port.Value;
            int timeout = (int)_timeout.Value;

            _list.Items.Clear();
            _done = 0; _found = 0; _total = ips.Count;
            _bar.Percent = 0;
            _bar.Invalidate();
            _running = true;
            _cts = new CancellationTokenSource();
            _start.Text = "停止扫描";
            _start.Icon = UiIcon.Stop;
            _start.Kind = RhButtonKind.Outline;
            _start.Invalidate();
            _stat.Text = "正在扫描 0 / " + _total + " ...";
            CancellationToken token = _cts.Token;

            Task.Factory.StartNew(delegate
            {
                try
                {
                    ParallelOptions po = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 48,
                        CancellationToken = token
                    };
                    Parallel.ForEach(ips, po, delegate(string ip, ParallelLoopState state)
                    {
                        if (token.IsCancellationRequested) { state.Stop(); return; }
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        bool open = NetProbe.PortOpen(ip, port, timeout);
                        sw.Stop();
                        int d = Interlocked.Increment(ref _done);
                        if (open) Interlocked.Increment(ref _found);
                        try
                        {
                            BeginInvoke(new Action(delegate
                            {
                                if (IsDisposed) return;
                                if (open)
                                {
                                    ListViewItem it = new ListViewItem(ip);
                                    it.SubItems.Add(port.ToString());
                                    it.SubItems.Add("RDP 端口开放");
                                    it.SubItems.Add(sw.ElapsedMilliseconds + " ms");
                                    it.Checked = true;
                                    it.Tag = ip;
                                    _list.Items.Add(it);
                                }
                                _bar.Percent = Math.Min(100, d * 100 / Math.Max(1, _total));
                                _bar.Invalidate();
                                _stat.Text = "已扫描 " + d + " / " + _total + "   ·   发现 " + _found + " 台";
                            }));
                        }
                        catch { }
                    });
                }
                catch (OperationCanceledException) { }
                catch { }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed) return;
                        FinishScan();
                    }));
                }
                catch { }
            }, token);
        }

        private void StopScan()
        {
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            FinishScan();
        }

        private void FinishScan()
        {
            _running = false;
            _start.Text = "开始扫描";
            _start.Icon = UiIcon.Play;
            _start.Kind = RhButtonKind.Primary;
            _start.Invalidate();
            _stat.Text = "扫描结束：共 " + _total + " 个地址，发现 " + _found + " 台。";
        }

        /// <summary>Fills the list with sample rows for preview screenshots only.</summary>
        public void UseSamples()
        {
            string[] ips = { "192.168.1.21", "192.168.1.34", "192.168.1.58", "192.168.1.92" };
            int[] ms = { 12, 18, 9, 24 };
            for (int i = 0; i < ips.Length; i++)
            {
                ListViewItem it = new ListViewItem(ips[i]);
                it.SubItems.Add("3389");
                it.SubItems.Add("RDP 端口开放");
                it.SubItems.Add(ms[i] + " ms");
                it.Checked = true;
                it.Tag = ips[i];
                _list.Items.Add(it);
            }
            _bar.Percent = 100;
            _bar.Invalidate();
            _stat.Text = "扫描结束：共 254 个地址，发现 4 台。";
        }

        private void AcceptSelection()
        {
            Selected.Clear();
            int port = (int)_port.Value;
            foreach (ListViewItem it in _list.Items)
            {
                if (!it.Checked) continue;
                ConnectionInfo c = new ConnectionInfo();
                c.Host = it.Tag as string;
                c.Name = c.Host;
                c.Port = port;
                c.Group = "扫描发现";
                c.ShareDrives = true;
                c.ShareClipboard = true;
                Selected.Add(c);
            }
            if (Selected.Count == 0)
            {
                MessageBox.Show("请先勾选要加入的主机。", "RemoteHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_running) StopScan();
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running && _cts != null) { try { _cts.Cancel(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
