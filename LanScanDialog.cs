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
        private readonly RhNumber _port = new RhNumber();
        private readonly RhNumber _timeout = new RhNumber();
        private readonly ListView _list = new ListView();
        private readonly ScanHeader _header = new ScanHeader();
        private readonly ScanBar _bar = new ScanBar();
        private readonly Label _stat = new Label();
        private readonly RhButton _start = new RhButton();
        private CancellationTokenSource _cts;
        private bool _running;
        private int _done, _total, _found;

        // Column geometry of the result grid. The header strip and the rows share these
        // offsets so the two always line up.
        private const int XCheck = 6;
        private const int XIp = 28;
        private const int XPort = 196;
        private const int XStatus = 276;
        private const int XElapsedRight = 512;

        /// <summary>
        /// Column titles for the result grid. The stock header strip paints a grey band
        /// with vertical separators that belongs to no theme in particular.
        /// </summary>
        private sealed class ScanHeader : Control
        {
            public ScanHeader()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Height = 22;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Ui.ResolveBackColor(this));
                Font f = Ui.F(8.5F, FontStyle.Bold);
                const TextFormatFlags V = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, "主机地址", f, new Rectangle(XIp, 0, 120, Height), Ui.TextMuted, TextFormatFlags.Left | V);
                TextRenderer.DrawText(g, "端口", f, new Rectangle(XPort, 0, 60, Height), Ui.TextMuted, TextFormatFlags.Left | V);
                TextRenderer.DrawText(g, "状态", f, new Rectangle(XStatus, 0, 140, Height), Ui.TextMuted, TextFormatFlags.Left | V);
                TextRenderer.DrawText(g, "耗时", f, new Rectangle(XElapsedRight - 60, 0, 60, Height), Ui.TextMuted, TextFormatFlags.Right | V);
                using (Pen p = new Pen(Ui.Border))
                    g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
        }

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

        /// <summary>
        /// One probe result. The result grid is owner-drawn, so the row carries its own
        /// check state instead of relying on the stock list-view check box.
        /// </summary>
        private sealed class ScanRow
        {
            public string Ip = "";
            public int Port;
            public int Milliseconds;
            public bool Checked;
        }

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
            _header.SetBounds(16, 34, 556, 22);
            res.Controls.Add(_header);
            _list.SetBounds(16, 58, 556, 152);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Ui.Field;
            _list.ForeColor = Ui.Text;
            _list.Font = Ui.NewF(9F);
            _list.HeaderStyle = ColumnHeaderStyle.None;
            _list.OwnerDraw = true;
            _list.Tag = "field";
            _list.Columns.Add("主机地址", 520);
            _list.DrawItem += List_DrawItem;
            _list.MouseDown += List_MouseDown;
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
                foreach (ListViewItem it in _list.Items)
                {
                    ScanRow r0 = it.Tag as ScanRow;
                    if (r0 != null && !r0.Checked) { turnOn = true; break; }
                }
                foreach (ListViewItem it in _list.Items)
                {
                    ScanRow r0 = it.Tag as ScanRow;
                    if (r0 == null) continue;
                    r0.Checked = turnOn;
                }
                _list.Invalidate();
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
                                    ScanRow row = new ScanRow();
                                    row.Ip = ip;
                                    row.Port = port;
                                    row.Milliseconds = (int)sw.ElapsedMilliseconds;
                                    row.Checked = true;
                                    ListViewItem it = new ListViewItem(ip);
                                    it.SubItems.Add(port.ToString());
                                    it.SubItems.Add("RDP 端口开放");
                                    it.SubItems.Add(row.Milliseconds + " ms");
                                    it.Tag = row;
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
                ScanRow row = new ScanRow();
                row.Ip = ips[i];
                row.Port = 3389;
                row.Milliseconds = ms[i];
                row.Checked = true;
                ListViewItem it = new ListViewItem(ips[i]);
                it.SubItems.Add("3389");
                it.SubItems.Add("RDP 端口开放");
                it.SubItems.Add(ms[i] + " ms");
                it.Tag = row;
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
                ScanRow row = it.Tag as ScanRow;
                if (row == null || !row.Checked) continue;
                ConnectionInfo c = new ConnectionInfo();
                c.Host = row.Ip;
                c.Name = row.Ip;
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

        /// <summary>Draws one result row: check box, address, port, state and elapsed time.</summary>
        private void List_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (e.Item == null) return;
            ScanRow row = e.Item.Tag as ScanRow;
            if (row == null) return;

            Graphics g = e.Graphics;
            bool sel = (e.State & ListViewItemStates.Selected) != 0;
            using (SolidBrush b = new SolidBrush(sel ? Ui.AccentSoft : Ui.Field))
                g.FillRectangle(b, e.Bounds);

            int left = e.Bounds.Left;
            int top = e.Bounds.Top;
            int h = e.Bounds.Height;

            Rectangle box = new Rectangle(left + XCheck, top + (h - 14) / 2, 14, 14);
            Ui.DrawCheckGlyph(g, box, row.Checked, true, false);

            const TextFormatFlags V = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, row.Ip, Ui.F(9F),
                new Rectangle(left + XIp, top, 160, h), sel ? Ui.Text : Ui.Text, TextFormatFlags.Left | V);
            TextRenderer.DrawText(g, row.Port.ToString(), Ui.F(9F),
                new Rectangle(left + XPort, top, 70, h), Ui.TextMuted, TextFormatFlags.Left | V);
            TextRenderer.DrawText(g, "RDP 端口开放", Ui.F(9F),
                new Rectangle(left + XStatus, top, 160, h), Ui.Accent, TextFormatFlags.Left | V);
            TextRenderer.DrawText(g, row.Milliseconds + " ms", Ui.F(9F),
                new Rectangle(left + XElapsedRight - 70, top, 70, h), Ui.TextMuted, TextFormatFlags.Right | V);

            using (Pen p = new Pen(Ui.BorderSoft))
                g.DrawLine(p, left, top + h - 1, e.Bounds.Right, top + h - 1);
        }

        /// <summary>Toggles the row check box when the glyph itself is clicked.</summary>
        private void List_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit == null || hit.Item == null) return;
            ScanRow row = hit.Item.Tag as ScanRow;
            if (row == null) return;
            Rectangle bounds = hit.Item.Bounds;
            Rectangle box = new Rectangle(bounds.Left + XCheck, bounds.Top + (bounds.Height - 14) / 2, 14, 14);
            if (!box.Contains(e.Location)) return;
            row.Checked = !row.Checked;
            _list.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running && _cts != null) { try { _cts.Cancel(); } catch { } }
            base.OnFormClosing(e);
        }
    }
}
