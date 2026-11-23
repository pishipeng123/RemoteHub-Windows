using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// A 2x1 / 2x2 wall of live RDP views. The very same RdpHost controls owned by the
    /// tab pages are re-parented into the cells (never recreated), so the sessions keep
    /// running while the wall is visible.
    /// </summary>
    internal sealed class SessionGrid : Panel
    {
        internal sealed class Item
        {
            public string Title;
            public string Sub;
            public Control View;
            public Control Overlay;   // "connecting" veil, moved together with the view
        }

        private sealed class Cell
        {
            public Panel Panel;
            public Panel Body;
            public Label Title;
            public Label Offline;
            public int Index;
        }

        private readonly List<Cell> _cells = new List<Cell>();
        private List<Item> _items = new List<Item>();
        private int _columns = 2;

        public event Action<int> Activate;

        public SessionGrid()
        {
            BackColor = Ui.Surface;
            DoubleBuffered = true;
            Padding = new Padding(10);
        }

        public int Columns { get { return _columns; } }

        public void ShowItems(List<Item> items, int columns)
        {
            ReleaseViews();
            Controls.Clear();
            _cells.Clear();
            _items = items ?? new List<Item>();
            _columns = Math.Max(1, Math.Min(2, columns));

            for (int i = 0; i < _items.Count; i++)
            {
                Item it = _items[i];
                Panel cell = new Panel { BackColor = Ui.Card, Tag = "card" };
                cell.Paint += delegate(object s, PaintEventArgs e)
                {
                    Graphics g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(0, 0, cell.Width - 1, cell.Height - 1);
                    using (SolidBrush b = new SolidBrush(Ui.Card)) Ui.FillRound(g, b, r, Ui.Radius);
                    using (Pen p = new Pen(Ui.Border)) Ui.DrawRound(g, p, r, Ui.Radius);
                };

                Label title = new Label
                {
                    Text = (i + 1) + "  ·  " + (it.Title ?? "") +
                           (string.IsNullOrEmpty(it.Sub) ? "" : "   " + it.Sub) +
                           "     [双击返回标签]",
                    Dock = DockStyle.Top,
                    Height = 30,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(12, 0, 0, 0),
                    Font = Ui.NewF(9F, FontStyle.Bold),
                    ForeColor = Ui.Text,
                    BackColor = Ui.CardAlt,
                    Tag = "title"
                };
                Panel body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Surface, Padding = new Padding(3) };
                Label offline = new Label
                {
                    Text = "未连接\r\n双击标题返回标签视图后重连",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = Ui.NewF(9F),
                    ForeColor = Ui.TextMuted,
                    BackColor = Ui.Surface,
                    Visible = false,
                    Tag = "muted"
                };
                Cell c = new Cell { Panel = cell, Body = body, Title = title, Offline = offline, Index = i };
                int captured = i;
                title.DoubleClick += delegate { if (Activate != null) Activate(captured); };
                title.Cursor = Cursors.Hand;

                if (it.View != null)
                {
                    it.View.Dock = DockStyle.Fill;
                    body.Controls.Add(it.View);
                }
                if (it.Overlay != null)
                {
                    it.Overlay.Dock = DockStyle.Fill;
                    body.Controls.Add(it.Overlay);
                    it.Overlay.BringToFront();
                }
                body.Controls.Add(offline);
                cell.Controls.Add(body);
                cell.Controls.Add(title);
                Controls.Add(cell);
                _cells.Add(c);
            }
            LayoutCells();
        }

        /// <summary>Detaches (but does not dispose) the views so the tabs can take them back.</summary>
        public void ReleaseViews()
        {
            foreach (Item it in _items)
            {
                if (it.Overlay != null)
                {
                    try
                    {
                        it.Overlay.Dock = DockStyle.None;
                        if (it.Overlay.Parent != null) it.Overlay.Parent.Controls.Remove(it.Overlay);
                    }
                    catch { }
                }
                if (it.View == null) continue;
                try
                {
                    it.View.Dock = DockStyle.None;
                    if (it.View.Parent != null) it.View.Parent.Controls.Remove(it.View);
                }
                catch { }
            }
            _items = new List<Item>();
        }

        /// <summary>Marks a cell as connected / not connected (placeholder overlay).</summary>
        public void SetConnected(int index, bool connected)
        {
            if (index < 0 || index >= _cells.Count) return;
            Cell c = _cells[index];
            c.Offline.Visible = !connected;
            if (!connected) c.Offline.BringToFront();
        }

        public void ApplyTheme()
        {
            BackColor = Ui.Surface;
            foreach (Cell c in _cells)
            {
                c.Panel.BackColor = Ui.Card;
                c.Title.BackColor = Ui.CardAlt;
                c.Title.ForeColor = Ui.Text;
                c.Body.BackColor = Ui.Surface;
                c.Offline.BackColor = Ui.Surface;
                c.Offline.ForeColor = Ui.TextMuted;
                c.Panel.Invalidate();
            }
        }

        private void LayoutCells()
        {
            if (_cells.Count == 0) return;
            int n = _cells.Count;
            int rows = Math.Max(1, (n + _columns - 1) / _columns);
            int gap = 10;
            int w = Math.Max(80, (ClientSize.Width - gap * (_columns + 1)) / _columns);
            int h = Math.Max(80, (ClientSize.Height - gap * (rows + 1)) / rows);
            for (int i = 0; i < n; i++)
            {
                int col = i % _columns, row = i / _columns;
                _cells[i].Panel.SetBounds(gap + col * (w + gap), gap + row * (h + gap), w, h);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCells();
        }
    }
}
