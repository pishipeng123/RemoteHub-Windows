using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteHub
{
    internal sealed class MainForm : Form
    {
        private sealed class Session
        {
            public ConnectionInfo Info;
            public RdpHost Host;
            public Panel Banner;
            public Panel Veil;
            public bool Notified;
            public bool FitApplied;
            public DateTime Started;
            public DateTime? ConnectedAt;      // first moment the session was actually online
            public bool HistoryWritten;
            public bool ManualDisconnect;
            public bool FullScreenAtTick;     // tracks mstscax's own fullscreen transitions
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        private ConnectionStore _store;
        private readonly Dictionary<TabPage, Session> _sessions = new Dictionary<TabPage, Session>();

        private Panel _header, _toolBar, _menuBar;
        private readonly List<string> _menuTitles = new List<string>();
        private readonly List<ContextMenuStrip> _menuPopups = new List<ContextMenuStrip>();
        private int _menuHover = -1;
        private ToolTip _tip;
        private RhButton _btnTheme, _btnHelp, _btnMore;
        private CapButton _btnMin, _btnMax, _btnClose;
        private ContextMenuStrip _moreMenu;
        private readonly List<RhButton> _toolButtons = new List<RhButton>();
        private readonly List<bool> _sepFlags = new List<bool>();
        private readonly List<int> _seps = new List<int>();
        private Panel _badge;
        private int _onlineCount;

        // ---- 1.3.0: quick connect, reachability probes, grid, screenshot, vault ----
        private Panel _quickWrap;
        private TextBox _quick;
        private RhButton _btnQuick;
        private readonly Dictionary<string, ProbeState> _probe =
            new Dictionary<string, ProbeState>(StringComparer.OrdinalIgnoreCase);
        private Timer _probeTimer, _idleTimer;
        private int _probeGeneration, _probeOnline;
        private SessionGrid _grid;
        private bool _gridMode;
        private readonly List<TabPage> _gridPages = new List<TabPage>();
        private VaultConfig _vault;
        private VaultLock _lock;

        private Panel _sideHead, _sideFoot, _searchWrap, _searchBox;
        private Label _sideCount;
        private RhButton _btnSideImport;
        private TreeView _tree;
        private Label _treeEmpty;
        private TextBox _search;
        private TreeNode _hoverNode;

        private RhTabControl _tabs;
        private Panel _welcome;
        private int _welcomeTop;
        private int _hoverTab = -1;     // pill whose "×" is under the cursor
        private int _hoverBody = -1;    // pill body under the cursor
        private bool _hoverAdd;         // the "+" button under the cursor

        private Panel _statusBar;
        private Label _lblMsg, _lblInfo;
        private Color _statusDot;

        private Timer _timer;
        private Timer _fitTimer;
        private ContextMenuStrip _treeMenu, _tabMenu;
        private ToolStripMenuItem _mFullScreen, _mFit;
        private bool _secureKeyHintShown;

        public MainForm()
        {
            this.Text = "RemoteHub - 内嵌远程桌面工作台";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(1280, 800);
            this.MinimumSize = new Size(1040, 640);
            this.Font = Ui.NewF(9F);

            // Frameless shell: the title bar is painted by us, so the window can be rounded
            // and the chrome can match the palette exactly.
            this.FormBorderStyle = FormBorderStyle.None;
            this.Padding = new Padding(1);
            this.BackColor = Ui.Border;
            this.DoubleBuffered = true;
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            ApplyWindowShape();

            // Full-window lock overlay (opaque, brought to front only while locked).
            _vault = VaultConfig.Load();
            _lock = new VaultLock(_vault);
            _lock.Visible = false;
            _lock.Unlocked += delegate { SetStatus("台账已解锁。"); };
            Controls.Add(_lock);
            _lock.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            _lock.BringToFront();

            _store = ConnectionStore.Load();
            BuildTree("");
            // Preselect the first host so the toolbar has a target on launch.
            if (_tree.Nodes.Count > 0)
            {
                TreeNode g0 = _tree.Nodes[0];
                _tree.SelectedNode = g0.Nodes.Count > 0 ? g0.Nodes[0] : g0;
            }
            if (!string.IsNullOrEmpty(_store.LastError)) SetStatus(_store.LastError);

            string picked = RdpHost.PickProgId();
            if (picked == null)
                MessageBox.Show("未检测到远程桌面 ActiveX 控件，无法建立连接。\r\n请使用「帮助 → 环境自检」查看详细信息。",
                    "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Ui.ThemeChanged += Ui_ThemeChanged;
            ApplyThemeNow();
            UpdateWelcome();
            _lblInfo.Text = "在线 0   ·   标签 0   ·   主机 " + _store.Items.Count +
                            "   ·   可达 0/" + _store.Items.Count;

            _timer = new Timer { Interval = 1500 };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Debounced resize: only re-lay out the remote desktop after the user
            // stops dragging the window / splitter.
            _fitTimer = new Timer { Interval = 260 };
            _fitTimer.Tick += delegate { _fitTimer.Stop(); ApplyFitToCurrent(); };

            SyncFitUi();
            SetStatus("就绪");

            // Reachability probes run off the UI thread; the tree just repaints on results.
            _probeTimer = new Timer { Interval = 30000 };
            _probeTimer.Tick += delegate { StartProbe(false); };
            _probeTimer.Start();
            StartProbe(false);

            _idleTimer = new Timer { Interval = 15000 };
            _idleTimer.Tick += IdleTimer_Tick;
            _idleTimer.Start();

            bool preview = Environment.GetEnvironmentVariable("RH_PREVIEW") == "1";
            bool forceLock = Environment.GetEnvironmentVariable("RH_FORCE_LOCK") == "1";
            if ((_vault.Enabled && !preview) || forceLock)
                this.Shown += delegate { _lock.ShowLock(); };
        }

        // ==================== UI construction ====================

        private void BuildUi()
        {
            BuildHeader();
            BuildSidebar();
            BuildWorkspace();
            BuildStatusBar();

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Ui.Border;
            split.SplitterWidth = 1;
            split.Panel1.BackColor = Ui.Sidebar;
            split.Panel1.Tag = "sidepanel";
            split.Panel2.BackColor = Ui.Surface;
            split.Panel2.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Ui.Border)) e.Graphics.DrawLine(p, 0, 0, 0, split.Panel2.Height);
            };

            // Dock order: Fill first (bottom of z-order), then edge-docked controls.
            split.Panel1.Controls.Add(_tree);                 // Fill
            split.Panel1.Controls.Add(_treeEmpty);            // Fill overlay
            split.Panel1.Controls.Add(_sideFoot);            // Bottom
            split.Panel1.Controls.Add(_searchWrap);          // Top
            split.Panel1.Controls.Add(_sideHead);            // Top (outermost)
            split.Panel2.Controls.Add(_tabs);                // Fill
            split.Panel2.Controls.Add(_welcome);             // Fill overlay
            split.Panel2.Controls.Add(_grid);                // Fill, hidden unless grid mode

            this.Controls.Add(split);        // Fill
            this.Controls.Add(_statusBar);   // Bottom
            this.Controls.Add(_toolBar);     // Top (command bar)
            this.Controls.Add(_menuBar);     // Top (menu bar)
            this.Controls.Add(_header);      // Top (title bar, outermost)

            this.Shown += delegate
            {
                // Navigation column is intentionally slim (was 252): 208px still fits
                // the search field, two-line host rows and the bottom action button.
                try { split.SplitterDistance = 208; } catch { }
                LayoutHeader();
                LayoutSidebar();
                LayoutWelcome();
            };
        }

        private void BuildHeader()
        {
            _header = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Ui.TopBar, Tag = "topbar" };
            _header.Paint += TitleBar_Paint;
            _header.MouseDown += TitleBar_MouseDown;
            _header.DoubleClick += delegate { ToggleMaximize(); };

            _btnTheme = new RhButton
            {
                Text = "",
                Icon = Ui.Dark ? UiIcon.Sun : UiIcon.Moon,
                Kind = RhButtonKind.Chip,
                Size = new Size(32, 30)
            };
            _btnTheme.Click += delegate { ToggleTheme(); };
            _btnHelp = new RhButton { Text = "", Icon = UiIcon.Help, Kind = RhButtonKind.Chip, Size = new Size(32, 30) };
            _btnHelp.Click += delegate { ShowShortcuts(); };

            _btnMin = new CapButton(CapGlyph.Minimize);
            _btnMax = new CapButton(CapGlyph.Maximize);
            _btnClose = new CapButton(CapGlyph.Close);
            _btnMin.Click += delegate { this.WindowState = FormWindowState.Minimized; };
            _btnMax.Click += delegate { ToggleMaximize(); };
            _btnClose.Click += delegate { this.Close(); };

            _header.Controls.AddRange(new Control[] { _btnTheme, _btnHelp, _btnMin, _btnMax, _btnClose });

            // The four-level top area (brand strip + menu bar + toolbar) is folded into a
            // painted title bar plus one command bar; every menu command lives in _moreMenu.

            _toolBar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Ui.Card, Tag = "card" };
            _toolBar.Paint += CommandBar_Paint;

            // Classic icon toolbar: no captions under the icons, the command name
            // shows in the tooltip. Every command is also reachable from the menu bar.
            _tip = new ToolTip { InitialDelay = 450, ReshowDelay = 120, AutoPopDelay = 6000 };
            AddTool("新建连接", UiIcon.Plus, RhButtonKind.Chip, 26, delegate { NewConnection(); }, false);
            AddTool("编辑连接", UiIcon.Edit, RhButtonKind.Chip, 26, delegate { EditSelected(); }, false);
            AddTool("删除连接", UiIcon.Trash, RhButtonKind.Chip, 26, delegate { DeleteSelected(); }, true);
            AddTool("连接", UiIcon.Play, RhButtonKind.Chip, 26, delegate { ConnectSelected(); }, false);
            AddTool("断开", UiIcon.Power, RhButtonKind.Chip, 26, delegate { DisconnectCurrent(); }, false);
            AddTool("重连", UiIcon.Refresh, RhButtonKind.Chip, 26, delegate { ReconnectCurrent(); }, true);
            AddTool("全屏 (F11)", UiIcon.Expand, RhButtonKind.Chip, 26, delegate { ToggleFullScreen(); }, false);
            AddTool("文件共享", UiIcon.Folder, RhButtonKind.Chip, 26, delegate { ShowFileTransfer(); }, false);
            AddTool("会话截图", UiIcon.Monitor, RhButtonKind.Chip, 26, delegate { CaptureSession(); }, false);
            AddTool("多画面宫格", UiIcon.Grid, RhButtonKind.Chip, 26, delegate { ToggleGrid(); }, false);

            _badge = new Panel { Size = new Size(74, 26), BackColor = Color.Transparent };
            _badge.Paint += Badge_Paint;
            _badge.Visible = false;          // live counters live in the status bar
            _toolBar.Controls.Add(_badge);

            _btnMore = new RhButton { Text = "", Icon = UiIcon.More, Kind = RhButtonKind.Chip, Size = new Size(26, 26) };
            _btnMore.Click += delegate { ShowRecentMenu(); };
            _toolBar.Controls.Add(_btnMore);

            // Quick connect: type an IP / host name and press Enter, no address book
            // entry required. Sits at the right end of the command bar.
            _quickWrap = new Panel { Size = new Size(210, 26), Tag = "fieldwrap", BackColor = Ui.Field };
            _quickWrap.Paint += QuickWrap_Paint;
            _quick = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Field,
                ForeColor = Ui.Text,
                Font = Ui.NewF(9F),
                Location = new Point(7, 5),
                Size = new Size(136, 18),
                Tag = "field"
            };
            _quick.HandleCreated += delegate
            {
                try { SendMessage(_quick.Handle, EM_SETCUEBANNER, (IntPtr)1, "快速连接：IP / 名称"); }
                catch { }
            };
            _quick.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    QuickConnect();
                }
            };
            _btnQuick = new RhButton
            {
                Text = "连接",
                Icon = UiIcon.Play,
                Kind = RhButtonKind.Chip,
                Size = new Size(52, 22),
                Location = new Point(148, 2)
            };
            _btnQuick.Click += delegate { QuickConnect(); };
            _quickWrap.Controls.Add(_quick);
            _quickWrap.Controls.Add(_btnQuick);
            _quickWrap.Resize += delegate
            {
                _quick.Width = Math.Max(40, _quickWrap.Width - 66);
                _btnQuick.Left = Math.Max(40, _quickWrap.Width - 56);
                Ui.RoundRegion(_quickWrap, Ui.Radius);
            };
            _toolBar.Controls.Add(_quickWrap);
            Ui.RoundRegion(_quickWrap, Ui.Radius);

            BuildMenuBar();
            _toolBar.Resize += delegate { LayoutToolbar(); };
        }

        // ==================== menu bar ====================

        private static ContextMenuStrip NewMenu()
        {
            return new ContextMenuStrip { Renderer = new UiToolStripRenderer(), Font = Ui.NewF(9F) };
        }

        private static void AddItem(ToolStripItemCollection items, string text, EventHandler h)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += h;
            items.Add(it);
        }

        private static void AddItem(ContextMenuStrip menu, string text, EventHandler h)
        {
            AddItem(menu.Items, text, h);
        }

        private static void AddSep(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        /// <summary>
        /// A real menu bar, the way a desktop tool exposes its commands. Everything the
        /// toolbar can do is reachable here too, plus the rarely used items.
        /// </summary>
        private void BuildMenuBar()
        {
            _menuBar = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Ui.Card, Tag = "card" };
            _menuBar.Paint += MenuBar_Paint;
            _menuBar.MouseMove += MenuBar_MouseMove;
            _menuBar.MouseLeave += delegate { _menuHover = -1; _menuBar.Invalidate(); };
            _menuBar.MouseDown += MenuBar_MouseDown;

            ContextMenuStrip file = NewMenu();
            AddItem(file, "新建连接", delegate { NewConnection(); });
            AddItem(file, "编辑连接", delegate { EditSelected(); });
            AddItem(file, "删除连接", delegate { DeleteSelected(); });
            AddSep(file);
            AddItem(file, "批量导入主机...", delegate { ImportHosts(); });
            AddItem(file, "导出台账 CSV...", delegate { ExportCsv(); });
            AddSep(file);
            AddItem(file, "退出", delegate { this.Close(); });

            ContextMenuStrip edit = NewMenu();
            AddItem(edit, "复制一份", delegate { DuplicateSelected(); });
            AddSep(edit);
            AddItem(edit, "搜索主机", delegate { _search.Focus(); _search.SelectAll(); });
            AddSep(edit);
            AddItem(edit, "锁定台账", delegate { LockNow(); });
            AddItem(edit, "主密码 / 自动锁定...", delegate { ShowVaultSettings(); });

            ContextMenuStrip view = NewMenu();
            _mFit = new ToolStripMenuItem("远程画面跟随窗口大小");
            _mFit.Click += delegate { ToggleFit(); };
            _mFit.Checked = Ui.FitToWindow;
            view.Items.Add(_mFit);
            AddItem(view, "深色 / 浅色主题", delegate { ToggleTheme(); });
            AddSep(view);
            AddItem(view, "多画面宫格", delegate { ToggleGrid(); });
            AddItem(view, "会话截图", delegate { CaptureSession(); });
            _mFullScreen = new ToolStripMenuItem("切换全屏");
            _mFullScreen.Click += delegate { ToggleFullScreen(); };
            view.Items.Add(_mFullScreen);
            AddSep(view);
            AddItem(view, "关闭当前标签", delegate { CloseCurrentTab(); });
            AddItem(view, "关闭所有标签", delegate { CloseAllTabs(); });

            ContextMenuStrip tools = NewMenu();
            AddItem(tools, "局域网扫描 / 发现主机...", delegate { ShowLanScan(); });
            AddItem(tools, "立即重新探测在线状态", delegate { StartProbe(true); });
            AddSep(tools);
            AddItem(tools, "文件共享 / 传输", delegate { ShowFileTransfer(); });
            AddItem(tools, "发送 Ctrl+Alt+Del（安全键）", delegate { SendSecureKey(); });
            AddSep(tools);
            AddItem(tools, "批量连接整个分组", delegate { BatchConnectGroup(); });
            AddItem(tools, "将当前会话保存到台账", delegate { SaveCurrentToBook(); });
            AddSep(tools);
            AddItem(tools, "刷新列表", delegate { BuildTree(CleanFilter()); });

            ContextMenuStrip help = NewMenu();
            AddItem(help, "连接历史", delegate { ShowHistory(); });
            AddItem(help, "清空连接历史", delegate { ClearHistory(); });
            AddSep(help);
            AddItem(help, "环境自检", delegate { ShowDiagnostics(); });
            AddItem(help, "快捷键说明", delegate { ShowShortcuts(); });
            AddItem(help, "关于 RemoteHub", delegate { ShowAbout(); });

            _menuTitles.Add("文件"); _menuPopups.Add(file);
            _menuTitles.Add("编辑"); _menuPopups.Add(edit);
            _menuTitles.Add("视图"); _menuPopups.Add(view);
            _menuTitles.Add("工具"); _menuPopups.Add(tools);
            _menuTitles.Add("帮助"); _menuPopups.Add(help);
        }

        private int MenuTitleWidth(int i)
        {
            return TextRenderer.MeasureText(_menuTitles[i], Ui.F(9F)).Width + 20;
        }

        private int MenuTitleStart(int i)
        {
            int x = 8;
            for (int k = 0; k < i; k++) x += MenuTitleWidth(k);
            return x;
        }

        private int MenuIndexAt(int x)
        {
            for (int i = 0; i < _menuTitles.Count; i++)
            {
                int s = MenuTitleStart(i);
                if (x >= s && x < s + MenuTitleWidth(i)) return i;
            }
            return -1;
        }

        private void MenuBar_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush bg = new SolidBrush(Ui.Card)) g.FillRectangle(bg, _menuBar.ClientRectangle);
            for (int i = 0; i < _menuTitles.Count; i++)
            {
                int s = MenuTitleStart(i);
                int w = MenuTitleWidth(i);
                if (i == _menuHover)
                    using (SolidBrush b = new SolidBrush(Ui.AccentSoft))
                        g.FillRectangle(b, s, 0, w, _menuBar.Height);
                using (SolidBrush t = new SolidBrush(i == _menuHover ? Ui.Accent : Ui.Text))
                    g.DrawString(_menuTitles[i], Ui.F(9F), t, new PointF(s + 10, 5));
            }
            using (Pen p = new Pen(Ui.Border))
                g.DrawLine(p, 0, _menuBar.Height - 1, _menuBar.Width, _menuBar.Height - 1);
        }

        private void MenuBar_MouseMove(object sender, MouseEventArgs e)
        {
            int idx = MenuIndexAt(e.Location.X);
            if (idx != _menuHover) { _menuHover = idx; _menuBar.Invalidate(); }
        }

        private void MenuBar_MouseDown(object sender, MouseEventArgs e)
        {
            int idx = MenuIndexAt(e.Location.X);
            if (idx < 0) return;
            _menuHover = idx;
            _menuBar.Invalidate();
            _menuPopups[idx].Show(_menuBar, new Point(MenuTitleStart(idx), _menuBar.Height));
        }

        /// <summary>Overflow button: recent sessions, one click to reconnect.</summary>
        private void ShowRecentMenu()
        {
            if (_moreMenu != null) { try { _moreMenu.Dispose(); } catch { } }
            _moreMenu = NewMenu();
            List<SessionRecord> rows = RecentRows(8);
            if (rows.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（暂无记录）");
                none.Enabled = false;
                _moreMenu.Items.Add(none);
            }
            else
            {
                foreach (SessionRecord r in rows)
                {
                    SessionRecord rec = r;
                    string label = (string.IsNullOrEmpty(r.Name) ? r.Address : r.Name) +
                                   "    " + r.Address;
                    ToolStripMenuItem it = new ToolStripMenuItem(label);
                    it.Click += delegate { ConnectRecent(rec); };
                    _moreMenu.Items.Add(it);
                }
                _moreMenu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem more = new ToolStripMenuItem("连接历史...");
                more.Click += delegate { ShowHistory(); };
                _moreMenu.Items.Add(more);
            }
            _moreMenu.Show(_btnMore, new Point(-190, _btnMore.Height + 2));
        }

        private void ConnectRecent(SessionRecord r)
        {
            string addr = r.Address ?? "";
            string host = addr;
            int port = 3389;
            int colon = addr.LastIndexOf(':');
            if (colon > 0)
            {
                int p;
                if (int.TryParse(addr.Substring(colon + 1), out p) && p > 0 && p <= 65535)
                {
                    host = addr.Substring(0, colon);
                    port = p;
                }
            }

            foreach (ConnectionInfo c in _store.Items)
                if (string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    c.Port == port)
                {
                    OpenConnection(c.Clone());
                    return;
                }

            ConnectionInfo temp = new ConnectionInfo();
            temp.Name = string.IsNullOrEmpty(r.Name) ? host : r.Name;
            temp.Host = host;
            temp.Port = port;
            temp.UserName = r.UserName ?? "";
            temp.Group = "临时连接";
            temp.ShareDrives = true;
            temp.ShareClipboard = true;
            OpenConnection(temp);
        }

        private void AddTool(string text, UiIcon icon, RhButtonKind kind, int width,
                             EventHandler handler, bool separatorAfter)
        {
            RhButton b = new RhButton
            {
                Text = "",                      // icon only, the name lives in the tooltip
                Icon = icon,
                Kind = kind,
                Size = new Size(width, 26),
                TabStop = false
            };
            b.Click += handler;
            if (_tip != null) _tip.SetToolTip(b, text);
            _toolButtons.Add(b);
            _sepFlags.Add(separatorAfter);
            _toolBar.Controls.Add(b);
        }

        // ==================== frameless window plumbing ====================

        private const int WM_NCHITTEST = 0x84;
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int RESIZE_GRIP = 6;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW: keep the edge readable without a frame
                return cp;
            }
        }

        /// <summary>Rounds the window corners while floating; maximised windows stay square.</summary>
        private void ApplyWindowShape()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                this.Padding = new Padding(0);
                Ui.RoundRegion(this, 0);
                return;
            }
            this.Padding = new Padding(1);
            Ui.RoundRegion(this, 0);
            _btnMax.Glyph = CapGlyph.Maximize;
            _btnMax.Invalidate();
        }

        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                _btnMax.Glyph = CapGlyph.Maximize;
            }
            else
            {
                // Without MaximizedBounds a borderless window covers the taskbar.
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                WindowState = FormWindowState.Maximized;
                _btnMax.Glyph = CapGlyph.Restore;
            }
            ApplyWindowShape();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_header == null) return;
            bool max = WindowState == FormWindowState.Maximized;
            Ui.RoundRegion(this, 0);
            int want = max ? 0 : 1;
            if (Padding.Left != want) Padding = new Padding(want);
            if (_lock != null) _lock.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        }

        private void TitleBar_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { ToggleMaximize(); return; }
            ReleaseCapture();
            SendMessage(this.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result != 1) return;   // not HTCLIENT: let children handle it
                int screenX = (short)(m.LParam.ToInt32() & 0xFFFF);
                int screenY = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                Point p = PointToClient(new Point(screenX, screenY));
                int g = RESIZE_GRIP;
                bool left = p.X <= g, right = p.X >= ClientSize.Width - g - 1;
                bool top = p.Y <= g, bottom = p.Y >= ClientSize.Height - g - 1;
                if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                return;
            }
            base.WndProc(ref m);
        }

        private enum CapGlyph { Minimize, Maximize, Restore, Close }

        /// <summary>Flat caption button: 36x42 hover surface, glyph drawn with pen strokes.</summary>
        private sealed class CapButton : Control
        {
            private bool _hover;
            private CapGlyph _glyph;

            public CapGlyph Glyph { get { return _glyph; } set { _glyph = value; Invalidate(); } }

            public CapButton(CapGlyph glyph)
            {
                _glyph = glyph;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                Size = new Size(36, 42);
                Cursor = Cursors.Default;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Ui.TopBar);

                bool close = _glyph == CapGlyph.Close;
                if (_hover)
                    using (SolidBrush b = new SolidBrush(close ? Ui.Danger : Ui.SidebarHover))
                        g.FillRectangle(b, ClientRectangle);

                Color ink = close && _hover ? Color.White : (Ui.Dark ? Color.FromArgb(200, 205, 215) : Color.FromArgb(90, 98, 112));
                using (Pen p = new Pen(ink, 1.1f))
                {
                    int cx = Width / 2, cy = Height / 2;
                    switch (_glyph)
                    {
                        case CapGlyph.Minimize:
                            g.DrawLine(p, cx - 5, cy + 1, cx + 5, cy + 1);
                            break;
                        case CapGlyph.Maximize:
                            g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
                            break;
                        case CapGlyph.Restore:
                            g.DrawRectangle(p, cx - 5, cy - 4, 8, 8);
                            g.DrawLine(p, cx - 2, cy - 6, cx + 5, cy - 6);
                            g.DrawLine(p, cx + 5, cy - 6, cx + 5, cy + 1);
                            break;
                        case CapGlyph.Close:
                            g.DrawLine(p, cx - 4, cy - 4, cx + 5, cy + 5);
                            g.DrawLine(p, cx + 5, cy - 4, cx - 4, cy + 5);
                            break;
                    }
                }
            }
        }

        private void BuildSidebar()
        {
            // Header is dropped: search sits flush at the top and the primary "new host"
            // action moves to the bottom of the navigation column.
            _sideHead = new Panel { Dock = DockStyle.Top, Height = 0, Visible = false, Tag = "sidehead", BackColor = Ui.Sidebar };

            _searchWrap = new Panel { Dock = DockStyle.Top, Height = 50, Tag = "sidepanel", BackColor = Ui.Sidebar };
            _searchBox = new Panel { Tag = "plain", BackColor = Ui.Sidebar };
            _searchBox.Paint += SearchBox_Paint;
            _searchBox.SetBounds(11, 10, 240, 32);
            _search = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.SidebarField,
                ForeColor = Ui.SidebarText,
                Font = Ui.NewF(9F),
                Location = new Point(33, 7),
                Size = new Size(196, 18),
                Tag = "sidefield"
            };
            _search.TextChanged += SearchTextChanged;
            _search.HandleCreated += delegate
            {
                try { SendMessage(_search.Handle, EM_SETCUEBANNER, (IntPtr)1, "搜索主机 / IP / 备注"); }
                catch { }
            };
            _searchBox.Controls.Add(_search);
            _searchWrap.Controls.Add(_searchBox);
            _searchWrap.Resize += delegate { LayoutSidebar(); };

            _sideFoot = new Panel { Dock = DockStyle.Bottom, Height = 74, Tag = "sidefoot", BackColor = Ui.Sidebar };
            _sideFoot.Paint += SideFoot_Paint;
            _btnSideImport = new RhButton
            {
                Text = "新建主机",
                Icon = UiIcon.Plus,
                Kind = RhButtonKind.Subtle,
                Size = new Size(206, 30),
                Location = new Point(11, 10)
            };
            _btnSideImport.Click += delegate { NewConnection(); };
            _sideCount = new Label
            {
                Text = "0 台主机",
                Location = new Point(13, 47),
                Size = new Size(150, 18),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.SidebarMuted,
                BackColor = Color.Transparent,
                Tag = "sidemuted"
            };
            _sideFoot.Controls.Add(_btnSideImport);
            _sideFoot.Controls.Add(_sideCount);
            _sideFoot.Resize += delegate { LayoutSidebar(); };

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Ui.Sidebar,
                ForeColor = Ui.SidebarText,
                BorderStyle = BorderStyle.None,
                DrawMode = TreeViewDrawMode.OwnerDrawAll,
                ShowLines = false,
                ShowPlusMinus = false,
                HideSelection = false,
                FullRowSelect = true,
                Indent = 16,
                ItemHeight = 44,
                Font = Ui.NewF(9F),
                ShowNodeToolTips = true,
                Tag = "tree"
            };
            _tree.DrawNode += Tree_DrawNode;
            _tree.DoubleClick += delegate { ConnectSelected(); };
            _tree.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { ConnectSelected(); e.Handled = true; }
                if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            };
            _tree.MouseMove += delegate(object s, MouseEventArgs e)
            {
                TreeNode n = _tree.GetNodeAt(e.Location);
                if (n != _hoverNode) { _hoverNode = n; _tree.Invalidate(); }
            };
            _tree.MouseLeave += delegate { _hoverNode = null; _tree.Invalidate(); };
            _tree.MouseDown += delegate(object s, MouseEventArgs e)
            {
                TreeNode n = _tree.GetNodeAt(e.Location);
                if (n == null || n.Nodes.Count == 0 || e.Button != MouseButtons.Left) return;
                int x0 = 10 + n.Level * 18;
                if (e.X >= x0 - 2 && e.X <= x0 + 14) { n.Toggle(); _tree.Invalidate(); }
            };

            _treeMenu = new ContextMenuStrip { Renderer = new UiToolStripRenderer(), Font = Ui.NewF(9F) };            _treeMenu.Items.Add("连接", null, delegate { ConnectSelected(); });
            _treeMenu.Items.Add("批量连接本组", null, delegate { BatchConnectGroup(); });
            _treeMenu.Items.Add(new ToolStripSeparator());
            _treeMenu.Items.Add("编辑", null, delegate { EditSelected(); });
            _treeMenu.Items.Add("复制一份", null, delegate { DuplicateSelected(); });
            _treeMenu.Items.Add("删除", null, delegate { DeleteSelected(); });
            _treeMenu.Items.Add(new ToolStripSeparator());
            _treeMenu.Items.Add("在此分组新建", null, delegate { NewConnection(); });
            _tree.ContextMenuStrip = _treeMenu;

            _treeEmpty = new Label
            {
                Dock = DockStyle.Fill,
                Text = "没有主机\r\n\r\n点下方「新建主机」添加",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Ui.SidebarMuted,
                BackColor = Ui.Sidebar,
                Font = Ui.NewF(9F),
                Tag = "sidebar"
            };
        }

        private void BuildWorkspace()
        {
            _tabs = new RhTabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.Normal,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(186, 40),
                Padding = new Point(8, 4),
                Font = Ui.NewF(9F),
                BackColor = Ui.Surface,
                Tag = "tabs"
            };
            _tabs.PaintStrip = Tabs_PaintStrip;
            _tabs.SelectedIndexChanged += delegate { SyncUi(); ScheduleFit(); InvalidateStrip(); };
            _tabs.Resize += delegate { ScheduleFit(); InvalidateStrip(); };
            _tabs.MouseDown += Tabs_MouseDown;
            _tabs.MouseMove += delegate(object s, MouseEventArgs e)
            {
                int h = TabCloseHit(e.Location);
                int b = TabAt(e.Location);
                bool add = TabAddRect().Contains(e.Location);
                if (h != _hoverTab || b != _hoverBody || add != _hoverAdd)
                {
                    _hoverTab = h;
                    _hoverBody = b;
                    _hoverAdd = add;
                    InvalidateStrip();
                }
            };
            _tabs.MouseLeave += delegate
            {
                _hoverTab = -1; _hoverBody = -1; _hoverAdd = false;
                InvalidateStrip();
            };

            _tabMenu = new ContextMenuStrip { Renderer = new UiToolStripRenderer(), Font = Ui.NewF(9F) };
            _tabMenu.Items.Add("重连", null, delegate { ReconnectCurrent(); });
            _tabMenu.Items.Add("断开", null, delegate { DisconnectCurrent(); });
            _tabMenu.Items.Add("全屏", null, delegate { ToggleFullScreen(); });
            _tabMenu.Items.Add("发送 Ctrl+Alt+Del（安全键）", null, delegate { SendSecureKey(); });
            _tabMenu.Items.Add("文件共享 / 传输", null, delegate { ShowFileTransfer(); });
            _tabMenu.Items.Add("会话截图", null, delegate { CaptureSession(); });
            _tabMenu.Items.Add("多画面宫格", null, delegate { ToggleGrid(); });
            _tabMenu.Items.Add(new ToolStripSeparator());
            _tabMenu.Items.Add("关闭当前标签", null, delegate { CloseCurrentTab(); });
            _tabMenu.Items.Add("关闭其他标签", null, delegate { CloseOtherTabs(); });
            _tabMenu.Items.Add("关闭所有标签", null, delegate { CloseAllTabs(); });

            _grid = new SessionGrid { Dock = DockStyle.Fill, Visible = false };
            _grid.Activate += Grid_Activate;

            _welcome = new Panel { Dock = DockStyle.Fill, Tag = "welcome", BackColor = Ui.Surface };
            _welcome.Paint += Welcome_Paint;
            _welcome.Resize += delegate { LayoutWelcome(); };
        }

        private void BuildStatusBar()
        {
            _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Tag = "status", BackColor = Ui.StatusBar };
            _statusBar.Paint += StatusBar_Paint;
            _lblMsg = new Label
            {
                Text = "就绪",
                Location = new Point(28, 4),
                Size = new Size(600, 22),
                Font = Ui.NewF(9F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Tag = "title"
            };
            _lblInfo = new Label
            {
                Text = "",
                Location = new Point(600, 4),
                Size = new Size(400, 22),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Tag = "muted"
            };
            _statusBar.Controls.Add(_lblMsg);
            _statusBar.Controls.Add(_lblInfo);
            _statusBar.Resize += delegate
            {
                _lblInfo.SetBounds(Math.Max(300, _statusBar.Width - 560), 4, 546, 22);
                _lblMsg.Width = Math.Max(150, _statusBar.Width - 590);
            };
            _statusDot = Ui.TextMuted;
        }

        // ==================== layout ====================

        private void LayoutHeader()
        {
            if (_header == null) return;
            int w = _header.Width;
            _btnClose.Location = new Point(w - _btnClose.Width, 0);
            _btnMax.Location = new Point(_btnClose.Left - _btnMax.Width, 0);
            _btnMin.Location = new Point(_btnMax.Left - _btnMin.Width, 0);
            _btnHelp.Location = new Point(_btnMin.Left - 42, 6);
            _btnTheme.Location = new Point(_btnHelp.Left - 33, 6);
            _header.Invalidate();
            LayoutToolbar();
        }

        private void LayoutToolbar()
        {
            if (_toolBar == null) return;
            int x = 8;
            _seps.Clear();
            for (int i = 0; i < _toolButtons.Count; i++)
            {
                RhButton b = _toolButtons[i];
                b.Location = new Point(x, 3);
                x += b.Width + 2;
                if (_sepFlags[i]) { _seps.Add(x + 5); x += 14; }
            }
            // Right cluster: overflow menu, then the quick-connect field.
            int right = _toolBar.Width - 8;
            _btnMore.Location = new Point(right - _btnMore.Width, 3);
            _btnMore.Visible = _btnMore.Left > x + 4;
            if (_quickWrap != null)
            {
                int room = _btnMore.Left - x - 14;
                int qw = Math.Max(140, Math.Min(220, room));
                _quickWrap.SetBounds(_btnMore.Left - qw - 8, 3, qw, 26);
                _quickWrap.Visible = _btnMore.Visible && _quickWrap.Left > x + 6;
            }
            _toolBar.Invalidate();
        }

        private void LayoutSidebar()
        {
            if (_searchWrap != null && _searchBox != null)
            {
                _searchBox.SetBounds(11, 10, Math.Max(80, _searchWrap.Width - 22), 32);
                _search.SetBounds(33, 7, Math.Max(40, _searchBox.Width - 44), 18);
                Ui.RoundRegion(_searchBox, Ui.Radius);
            }
            if (_sideFoot != null && _btnSideImport != null)
                _btnSideImport.SetBounds(11, 10, Math.Max(60, _sideFoot.Width - 22), 30);
        }

        private void LayoutWelcome()
        {
            if (_welcome == null) return;
            _welcomeTop = Math.Max(24, (_welcome.Height - 40) / 2);
            _welcome.Invalidate();
        }

        // ==================== painting ====================

        private void TitleBar_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush bg = new SolidBrush(Ui.TopBar)) g.FillRectangle(bg, _header.ClientRectangle);

            Rectangle mark = new Rectangle(13, 10, 22, 22);
            using (SolidBrush b = new SolidBrush(Ui.Accent)) Ui.FillRound(g, b, mark, 3);
            Ui.DrawIcon(g, UiIcon.Logo, new Rectangle(mark.X + 4, mark.Y + 4, 14, 14), Color.White);

            using (SolidBrush tb = new SolidBrush(Ui.Text))
                g.DrawString("RemoteHub", Ui.F(10F, FontStyle.Bold), tb, new PointF(44, 11));

            using (Pen p = new Pen(Ui.Border)) g.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        }

        private void CommandBar_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(Ui.CardAlt)) g.FillRectangle(bg, _toolBar.ClientRectangle);
            using (Pen p = new Pen(Ui.Border))
            {
                foreach (int sx in _seps) g.DrawLine(p, sx, 7, sx, _toolBar.Height - 7);
                g.DrawLine(p, 0, _toolBar.Height - 1, _toolBar.Width, _toolBar.Height - 1);
            }
        }

        private void Badge_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _badge.Width - 1, _badge.Height - 1);
            using (SolidBrush b = new SolidBrush(Ui.AccentSoft)) Ui.FillRound(g, b, r, 3);
            using (SolidBrush d = new SolidBrush(_onlineCount > 0 ? Ui.Success : Ui.TextMuted))
                g.FillEllipse(d, 11, r.Height / 2 - 2.5f, 5, 5);
            using (SolidBrush tb = new SolidBrush(_onlineCount > 0 ? Ui.Accent : Ui.TextMuted))
                g.DrawString("在线 " + _onlineCount, Ui.F(8.5F, FontStyle.Bold), tb, new PointF(21, 6));
        }

        private void SearchBox_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _searchBox.Width - 1, _searchBox.Height - 1);
            using (SolidBrush b = new SolidBrush(Ui.SidebarField)) Ui.FillRound(g, b, r, Ui.Radius);
            using (Pen p = new Pen(Ui.SidebarBorder)) Ui.DrawRound(g, p, r, Ui.Radius);
            Ui.DrawIcon(g, UiIcon.Search, new Rectangle(9, 9, 14, 14), Ui.SidebarMuted);
        }

        private void QuickWrap_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _quickWrap.Width - 1, _quickWrap.Height - 1);
            using (SolidBrush b = new SolidBrush(Ui.Field)) Ui.FillRound(g, b, r, Ui.Radius);
            using (Pen p = new Pen(Ui.Border)) Ui.DrawRound(g, p, r, Ui.Radius);
        }

        private void SideFoot_Paint(object sender, PaintEventArgs e)
        {
            using (Pen p = new Pen(Ui.SidebarHover)) e.Graphics.DrawLine(p, 0, 0, _sideFoot.Width, 0);
        }

        private void StatusBar_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Ui.Border)) g.DrawLine(p, 0, 0, _statusBar.Width, 0);
            using (SolidBrush d = new SolidBrush(_statusDot)) g.FillEllipse(d, 13, 11, 8, 8);
        }

        private void Tree_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null) return;
            e.DrawDefault = false;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int rowH = e.Bounds.Height;
            Rectangle row = new Rectangle(0, e.Bounds.Top, _tree.ClientSize.Width, rowH);
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            bool hovered = (_hoverNode == e.Node) && !selected;
            ConnectionInfo c = e.Node.Tag as ConnectionInfo;
            bool isGroup = c == null;

            Rectangle pill = new Rectangle(7, row.Top + 2, Math.Max(24, row.Width - 14), rowH - 4);
            if (selected || hovered)
            {
                using (SolidBrush b = new SolidBrush(selected ? Ui.SidebarSelected : Ui.SidebarHover))
                    Ui.FillRound(g, b, pill, Ui.Radius);
                // Selected marker: short rounded bar, not a full-height stripe.
                if (selected)
                    using (SolidBrush a = new SolidBrush(Ui.SidebarActiveText))
                        Ui.FillRound(g, a, new Rectangle(pill.Left + 4, pill.Top + (pill.Height - 22) / 2, 3, 22), 2);
            }

            int indent = 13 + e.Node.Level * 16;
            int midY = row.Top + rowH / 2;

            if (isGroup)
            {
                bool expanded = e.Node.IsExpanded;
                PointF[] tri = expanded
                    ? new PointF[] { new PointF(indent, midY - 4), new PointF(indent + 8, midY - 4), new PointF(indent + 4, midY + 2) }
                    : new PointF[] { new PointF(indent + 1, midY - 5), new PointF(indent + 1, midY + 3), new PointF(indent + 7, midY - 1) };
                using (SolidBrush tb = new SolidBrush(Ui.SidebarMuted)) g.FillPolygon(tb, tri);

                string name = e.Node.Text;
                int cut = name.LastIndexOf("   (");
                if (cut > 0) name = name.Substring(0, cut);

                using (SolidBrush nb = new SolidBrush(selected ? Ui.SidebarActiveText : Ui.SidebarText))
                    g.DrawString(name, Ui.F(9F, FontStyle.Bold), nb, new PointF(indent + 14, midY - 9));

                string cnt = e.Node.Nodes.Count.ToString();
                float nameW = g.MeasureString(name, Ui.F(9F, FontStyle.Bold)).Width;
                float cntW = g.MeasureString(cnt, Ui.F(8F)).Width;
                Rectangle cr = new Rectangle((int)(indent + 16 + nameW), midY - 8, (int)cntW + 11, 16);
                using (SolidBrush cb = new SolidBrush(Ui.SidebarHover)) Ui.FillRound(g, cb, cr, Ui.Radius);
                using (SolidBrush ct = new SolidBrush(Ui.SidebarMuted))
                    g.DrawString(cnt, Ui.F(8F), ct, new PointF(cr.Left + 5, cr.Top + 1));
                return;
            }

            // Hosts sit one level in and hang from a hairline rail that starts
            // under the group header, so a host reads as part of that group.
            int dotX = e.Node.Level > 0 ? 25 : pill.Left + (selected ? 15 : 11);
            ProbeState live = ProbeStateOf(c);
            Color dotColor = live == ProbeState.Online ? Ui.Success
                : live == ProbeState.Reachable ? Ui.Warning
                : live == ProbeState.Checking ? Ui.TextMuted
                : Ui.SidebarOffline;

            if (e.Node.Level > 0 && e.Node.Parent != null)
            {
                int railX = dotX + 3;
                int railBottom = e.Node.Index == e.Node.Parent.Nodes.Count - 1 ? midY : row.Bottom;
                using (SolidBrush rb = new SolidBrush(Ui.Border))
                    g.FillRectangle(rb, railX, row.Top, 1, Math.Max(1, railBottom - row.Top));
            }

            using (SolidBrush db = new SolidBrush(dotColor)) g.FillEllipse(db, dotX, midY - 3, 6, 6);

            int textX = dotX + 14;
            int textW = Math.Max(20, row.Right - textX - 14);
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush nb = new SolidBrush(selected ? Ui.SidebarText : Ui.SidebarText))
                    g.DrawString(c.Name, Ui.F(9F), nb,
                        new RectangleF(textX, row.Top + 5, textW, 18), sf);
                string sub = c.Address + (string.IsNullOrEmpty(c.UserName) ? "" : " · " + c.UserName);
                using (SolidBrush ab = new SolidBrush(Ui.SidebarMuted))
                    g.DrawString(sub, Ui.F(8F), ab, new RectangleF(textX, row.Top + 23, textW, 16), sf);
            }
        }

        /// <summary>
        /// A live session always wins; otherwise use the last background probe result.
        /// Green = RDP port open, amber = ping answers but the port does not,
        /// grey = offline / not checked yet.
        /// </summary>
        private ProbeState ProbeStateOf(ConnectionInfo c)
        {
            if (c == null) return ProbeState.Unknown;
            foreach (Session s in _sessions.Values)
                if (s.Host != null && !s.Host.IsDisposed && s.Host.IsOnline &&
                    s.Info != null && s.Info.Address == c.Address)
                    return ProbeState.Online;
            ProbeState st;
            lock (_probe)
                if (!_probe.TryGetValue(c.Address, out st)) st = ProbeState.Unknown;
            return st;
        }

        private bool IsHostOnline(ConnectionInfo c)
        {
            return ProbeStateOf(c) == ProbeState.Online;
        }

        /// <summary>All pills share one width, sized to hug the longest title.</summary>
        private void SyncTabWidth()
        {
            if (_tabs == null) return;
            int maxW = 0;
            using (Graphics g = _tabs.CreateGraphics())
                foreach (TabPage p in _tabs.TabPages)
                    maxW = Math.Max(maxW, (int)Math.Ceiling(g.MeasureString(p.Text, Ui.F(9F, FontStyle.Bold)).Width));
            int w = Math.Max(126, Math.Min(236, maxW + 88));
            if (_tabs.ItemSize.Width != w || _tabs.ItemSize.Height != 40)
            {
                _tabs.ItemSize = new Size(w, 40);
                InvalidateStrip();
            }
        }

        /// <summary>Pill geometry inside one fixed-size tab slot.</summary>
        private Rectangle TabChipRect(int index)
        {
            Rectangle r = _tabs.GetTabRect(index);
            return new Rectangle(r.Left + 3, r.Top + 5, r.Width - 6, Math.Max(18, r.Height - 11));
        }

        /// <summary>The "+" affordance sitting right after the last tab.</summary>
        private Rectangle TabAddRect()
        {
            int n = _tabs.TabPages.Count;
            if (n == 0) return Rectangle.Empty;
            Rectangle last = _tabs.GetTabRect(n - 1);
            return new Rectangle(last.Right + 4, last.Top + 6, 26, Math.Max(18, last.Height - 12));
        }

        /// <summary>Bottom edge of the tab strip in client coordinates.</summary>
        private int TabStripBottom()
        {
            if (_tabs.TabPages.Count == 0) return 0;
            Rectangle r = _tabs.GetTabRect(_tabs.TabPages.Count - 1);
            return r.Bottom + 2;
        }

        private void InvalidateStrip()
        {
            int bottom = TabStripBottom();
            if (bottom <= 0) { _tabs.Invalidate(); return; }
            _tabs.Invalidate(new Rectangle(0, 0, _tabs.ClientSize.Width, bottom));
        }

        /// <summary>Paints the whole session strip: backdrop, pills, hairline and "＋".</summary>
        private void Tabs_PaintStrip(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int right = _tabs.ClientSize.Width;
            int bottom = TabStripBottom();
            if (bottom <= 0) bottom = 44;

            using (SolidBrush bg = new SolidBrush(Ui.Surface))
                g.FillRectangle(bg, 0, 0, right, bottom);

            for (int i = 0; i < _tabs.TabPages.Count; i++) DrawTabPill(g, i);

            using (Pen sep = new Pen(Ui.Border))
                g.DrawLine(sep, 0, bottom - 1, right, bottom - 1);

            Rectangle add = TabAddRect();
            if (add.Width > 0 && add.Right < right - 4)
            {
                if (_hoverAdd)
                {
                    using (SolidBrush b = new SolidBrush(Ui.CardAlt)) Ui.FillRound(g, b, add, 3);
                    using (SolidBrush ab = new SolidBrush(Color.FromArgb(45, Ui.Accent)))
                    using (Pen p = new Pen(ab)) Ui.DrawRound(g, p, add, 3);
                }
                Ui.DrawIcon(g, UiIcon.Plus,
                    new Rectangle(add.Left + 6, add.Top + add.Height / 2 - 7, 14, 14),
                    _hoverAdd ? Ui.Accent : Ui.TextMuted, 1.9f);
            }
        }

        private void DrawTabPill(Graphics g, int index)
        {
            if (index < 0 || index >= _tabs.TabPages.Count) return;
            TabPage page = _tabs.TabPages[index];
            Rectangle chip = TabChipRect(index);
            bool active = index == _tabs.SelectedIndex;
            bool bodyHot = (_hoverBody == index) && !active;
            bool closeHot = (_hoverTab == index);

            if (active)
            {
                using (SolidBrush b = new SolidBrush(Ui.AccentSoft)) Ui.FillRound(g, b, chip, 3);
                using (SolidBrush ab = new SolidBrush(Color.FromArgb(40, Ui.Accent)))
                using (Pen p = new Pen(ab)) Ui.DrawRound(g, p, chip, 3);
            }
            else if (bodyHot)
            {
                using (SolidBrush b = new SolidBrush(Ui.CardAlt)) Ui.FillRound(g, b, chip, 3);
            }

            Session s;
            bool online = _sessions.TryGetValue(page, out s) && s.Host != null &&
                          !s.Host.IsDisposed && s.Host.IsOnline;
            bool showClose = active || bodyHot || closeHot;

            // Dot + title form one group, centred in the pill. Short host names then look
            // balanced instead of hugging the left edge with a hole before the "×".
            string title = page.Text;
            Font titleFont = Ui.F(9F, active ? FontStyle.Bold : FontStyle.Regular);
            float titleW = g.MeasureString(title, titleFont).Width;
            int groupW = (int)Math.Ceiling(titleW) + 12;
            int room = chip.Width - 12 - 24;
            int gx = chip.Left + 12 + Math.Max(0, (room - groupW) / 2);

            using (SolidBrush db = new SolidBrush(online ? Ui.Success : (active ? Ui.Accent : Ui.TextMuted)))
                g.FillEllipse(db, gx, chip.Top + chip.Height / 2 - 3, 6, 6);

            int textLeft = gx + 12;
            using (SolidBrush tb = new SolidBrush(active ? Ui.Accent : (bodyHot ? Ui.Text : Ui.TextMuted)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(title, titleFont, tb,
                    new RectangleF(textLeft, chip.Top, Math.Max(10, chip.Right - 24 - textLeft), chip.Height), sf);
            }

            if (showClose)
                Ui.DrawIcon(g, UiIcon.Close,
                    new Rectangle(chip.Right - 19, chip.Top + chip.Height / 2 - 7, 13, 13),
                    closeHot ? Ui.Danger : Ui.TextMuted, 1.7f);
        }

        private int TabCloseHit(Point p)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                // Only an active or hovered pill shows its "×", so only those can be clicked.
                if (i != _tabs.SelectedIndex && _hoverBody != i && _hoverTab != i) continue;
                Rectangle chip = TabChipRect(i);
                if (new Rectangle(chip.Right - 24, chip.Top, 26, chip.Height).Contains(p)) return i;
            }
            return -1;
        }

        private int TabAt(Point p)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
                if (_tabs.GetTabRect(i).Contains(p)) return i;
            return -1;
        }

        private void Tabs_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (TabAddRect().Contains(e.Location)) { NewConnection(); return; }
                int hit = TabCloseHit(e.Location);
                if (hit >= 0) { CloseTab(_tabs.TabPages[hit]); return; }
                int pick = TabAt(e.Location);
                if (pick >= 0) { _tabs.SelectedIndex = pick; InvalidateStrip(); }
            }
            if (e.Button != MouseButtons.Right) return;
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                if (_tabs.GetTabRect(i).Contains(e.Location))
                {
                    _tabs.SelectedIndex = i;
                    _tabMenu.Show(_tabs, e.Location);
                    break;
                }
            }
        }

        private void Welcome_Paint(object sender, PaintEventArgs e)
        {
            // No landing page. No logo tile, no slogan, no card of "recent connections" -
            // that combination is what makes a desktop tool look like a generated product
            // page. An empty workspace with one line of guidance is what real software
            // shows when there is nothing to display.
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int cx = _welcome.Width / 2;
            int top = _welcomeTop;
            Ui.DrawCentered(g, "双击左侧主机即可连接", Ui.F(9F), Ui.TextMuted,
                new Rectangle(cx - 300, top, 600, 22));
        }

        private List<SessionRecord> RecentRows(int count)
        {
            List<SessionRecord> list = new List<SessionRecord>();
            try
            {
                HistoryBook book = SessionHistory.Load();
                if (book == null || book.Items == null) return list;
                List<SessionRecord> all = new List<SessionRecord>(book.Items);
                all.Sort(delegate(SessionRecord a, SessionRecord b) { return b.Ended.CompareTo(a.Ended); });
                for (int i = 0; i < all.Count && list.Count < count; i++) list.Add(all[i]);
            }
            catch { }
            return list;
        }

        private static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "刚刚";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " 分钟前";
            if (d.TotalHours < 24) return (int)d.TotalHours + " 小时前";
            if (d.TotalDays < 30) return (int)d.TotalDays + " 天前";
            return t.ToString("MM-dd");
        }

        // ==================== theme ====================

        private void ToggleTheme()
        {
            Ui.ToggleTheme();
            SetStatus("已切换到" + Ui.ThemeName + "主题。");
        }

        /// <summary>Live colour changes (including preview) repaint the whole window.</summary>
        private void Ui_ThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            ApplyThemeNow();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Ui.ThemeChanged -= Ui_ThemeChanged;
            base.OnFormClosed(e);
        }

        private void IdleTimer_Tick(object sender, EventArgs e)
        {
            if (_vault == null || !_vault.Enabled || _vault.IdleMinutes <= 0) return;
            if (_lock == null || _lock.Visible) return;
            if (Environment.GetEnvironmentVariable("RH_PREVIEW") == "1") return;
            if (IdleWatcher.Idle.TotalMinutes >= _vault.IdleMinutes) LockNow();
        }

        private void ApplyThemeNow()
        {
            Ui.ApplyTheme(this);
            this.BackColor = Ui.Border;
            _btnTheme.Icon = Ui.Dark ? UiIcon.Sun : UiIcon.Moon;
            _btnTheme.Invalidate();
            _statusDot = _onlineCount > 0 ? Ui.Success : Ui.TextMuted;
            foreach (TabPage page in _tabs.TabPages) page.BackColor = Ui.Surface;
            foreach (Control c in _header.Controls) c.Invalidate();
            foreach (Control c in _toolBar.Controls) c.Invalidate();
            _header.Invalidate();
            _toolBar.Invalidate();
            _statusBar.Invalidate();
            _welcome.Invalidate();
            _badge.Invalidate();
            _searchBox.Invalidate();
            _quickWrap.BackColor = Ui.Field;
            _quickWrap.Invalidate();
            _quick.BackColor = Ui.Field;
            _quick.ForeColor = Ui.Text;
            if (_grid != null) _grid.ApplyTheme();
            if (_lock != null) _lock.ApplyTheme();
            _tree.Invalidate();
            _tabs.Invalidate();
            LayoutHeader();
            LayoutSidebar();
            SyncFitUi();
            ApplyWindowShape();
        }

        private void UpdateWelcome()
        {
            bool show = _tabs.TabPages.Count == 0;
            _welcome.Visible = show;
            if (show) _welcome.BringToFront();
            LayoutWelcome();
        }

        // ==================== address book ====================

        private void SearchTextChanged(object sender, EventArgs e)
        {
            if (_tree == null || _store == null) return;
            BuildTree(_search.Text.Trim());
        }

        private string CleanFilter()
        {
            return _search == null ? "" : _search.Text.Trim();
        }

        private void SetSideCount()
        {
            if (_sideCount == null || _store == null) return;
            SortedDictionary<string, bool> groups = new SortedDictionary<string, bool>();
            foreach (ConnectionInfo c in _store.Items)
                if (!groups.ContainsKey(c.DisplayGroup)) groups[c.DisplayGroup] = true;
            _sideCount.Text = _store.Items.Count + " 台主机  ·  " + groups.Count + " 个分组";
        }

        private void BuildTree(string filter)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            SortedDictionary<string, List<ConnectionInfo>> groups =
                new SortedDictionary<string, List<ConnectionInfo>>(StringComparer.CurrentCultureIgnoreCase);

            foreach (ConnectionInfo c in _store.Items)
            {
                if (!string.IsNullOrEmpty(filter))
                {
                    string hay = (c.Name + " " + c.Host + " " + c.UserName + " " + c.Group + " " + c.Notes);
                    if (hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                string g = c.DisplayGroup;
                if (!groups.ContainsKey(g)) groups[g] = new List<ConnectionInfo>();
                groups[g].Add(c);
            }

            foreach (KeyValuePair<string, List<ConnectionInfo>> kv in groups)
            {
                TreeNode groupNode = new TreeNode(kv.Key + "   (" + kv.Value.Count + ")");
                groupNode.Tag = kv.Key;
                foreach (ConnectionInfo c in kv.Value)
                {
                    TreeNode n = new TreeNode(c.Name);
                    n.Tag = c;
                    n.ToolTipText = "主机: " + c.Address + "\r\n用户: " + c.UserName +
                        "\r\n分组: " + c.DisplayGroup +
                        (string.IsNullOrEmpty(c.Notes) ? "" : "\r\n备注: " + c.Notes);
                    groupNode.Nodes.Add(n);
                }
                _tree.Nodes.Add(groupNode);
            }
            _tree.ExpandAll();
            _tree.EndUpdate();
            _tree.Invalidate();
            SetSideCount();

            // Only one of the two Fill controls is visible at a time; overlapping them
            // would let the TreeView's native window sit on top of the hint label.
            bool empty = _tree.Nodes.Count == 0;
            if (_treeEmpty != null)
            {
                if (empty)
                {
                    _treeEmpty.Text = string.IsNullOrEmpty(filter)
                        ? "没有主机\r\n\r\n点下方「新建主机」添加"
                        : "没有匹配的主机\r\n\r\n换个关键词试试";
                }
                _treeEmpty.Visible = empty;
                _tree.Visible = !empty;
            }
        }

        private ConnectionInfo SelectedConnection()
        {
            if (_tree.SelectedNode == null) return null;
            return _tree.SelectedNode.Tag as ConnectionInfo;
        }

        private string SelectedGroup()
        {
            if (_tree.SelectedNode == null) return null;
            TreeNode n = _tree.SelectedNode;
            if (n.Tag is ConnectionInfo) n = n.Parent;
            return n == null ? null : n.Tag as string;
        }

        private void NewConnection()
        {
            ConnectionInfo c = new ConnectionInfo();
            string g = SelectedGroup();
            if (!string.IsNullOrEmpty(g) && g != "未分组") c.Group = g;
            using (ConnectionDialog d = new ConnectionDialog(c, _store.Groups(), true))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _store.Items.Add(d.Result);
                    _store.Save();
                    BuildTree(CleanFilter());
                    SetStatus("已添加主机 " + d.Result.Address);
                }
            }
        }

        private void EditSelected()
        {
            ConnectionInfo c = SelectedConnection();
            if (c == null) { SetStatus("请先在左侧选中一台主机。"); return; }
            using (ConnectionDialog d = new ConnectionDialog(c, _store.Groups(), false))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    int idx = _store.Items.IndexOf(c);
                    if (idx >= 0) _store.Items[idx] = d.Result;
                    _store.Save();
                    BuildTree(CleanFilter());
                    SetStatus("已保存 " + d.Result.Name + "（已打开的会话需重连）");
                }
            }
        }

        private void DuplicateSelected()
        {
            ConnectionInfo c = SelectedConnection();
            if (c == null) return;
            ConnectionInfo copy = c.Clone();
            copy.Name = c.Name + " - 副本";
            _store.Items.Add(copy);
            _store.Save();
            BuildTree(CleanFilter());
            SetStatus("已复制 " + c.Name);
        }

        private void DeleteSelected()
        {
            ConnectionInfo c = SelectedConnection();
            if (c == null) { SetStatus("请先在左侧选中一台主机。"); return; }
            if (MessageBox.Show("确定删除主机「" + c.Name + "」(" + c.Address + ") 吗？",
                "RemoteHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _store.Items.Remove(c);
            _store.Save();
            BuildTree(CleanFilter());
            SetStatus("已删除 " + c.Name);
        }

        private void ConnectSelected()
        {
            ConnectionInfo c = SelectedConnection();
            if (c == null) { SetStatus("请先在左侧选中一台主机。"); return; }
            OpenConnection(c.Clone());
        }

        private void BatchConnectGroup()
        {
            string g = SelectedGroup();
            if (string.IsNullOrEmpty(g)) { SetStatus("请选中一个分组或分组下的主机。"); return; }
            List<ConnectionInfo> list = new List<ConnectionInfo>();
            foreach (ConnectionInfo c in _store.Items)
                if (c.DisplayGroup == g) list.Add(c);
            if (list.Count == 0) return;
            if (list.Count > 8 &&
                MessageBox.Show("该分组共 " + list.Count + " 台主机，将同时建立 " + list.Count +
                    " 个远程会话，可能占用较多资源。继续？", "RemoteHub",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (ConnectionInfo c in list) OpenConnection(c.Clone());
            SetStatus("已批量发起 " + list.Count + " 个连接。");
        }

        private void ImportHosts()
        {
            using (ImportDialog d = new ImportDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                List<string> problems;
                int added = _store.ImportText(d.ImportText, out problems);
                if (added > 0) _store.Save();
                BuildTree(CleanFilter());
                string msg = "导入完成：新增 " + added + " 台。";
                if (problems.Count > 0)
                {
                    msg += " 跳过/提示 " + problems.Count + " 条：\r\n" +
                        string.Join("\r\n", problems.ToArray(), 0, Math.Min(10, problems.Count));
                    if (problems.Count > 10) msg += "\r\n...";
                    MessageBox.Show(msg, "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                SetStatus("批量导入：新增 " + added + " 台主机。");
            }
        }

        private void ExportCsv()
        {
            DialogResult incl = MessageBox.Show(
                "导出的 CSV 是否包含明文密码？\r\n「否」为推荐选项，导出的文件不含密码。",
                "RemoteHub", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (incl == DialogResult.Cancel) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV 文件|*.csv";
                d.FileName = "RemoteHub-主机台账-" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, _store.ToCsv(incl == DialogResult.Yes), new UTF8Encoding(true));
                    SetStatus("已导出 " + d.FileName + (incl == DialogResult.Yes ? "（含明文密码，请妥善保管）" : ""));
                }
                catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
            }
        }

        // ==================== sessions ====================

        private void OpenConnection(ConnectionInfo c)
        {
            if (_gridMode) ExitGrid(-1);   // structural change: leave the wall first
            TabPage page = new TabPage(c.Name);
            page.BackColor = Ui.Surface;
            Session sess = new Session { Info = c, Started = DateTime.Now };
            RdpHost host = new RdpHost();
            page.Controls.Add(host);
            sess.Veil = MakeVeil(c);
            page.Controls.Add(sess.Veil);
            _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;
            SyncTabWidth();
            _sessions[page] = sess;
            sess.Host = host;
            UpdateWelcome();

            Application.DoEvents();
            PrepareAndConnect(page, sess);
            SyncUi();
        }

        private void PrepareAndConnect(TabPage page, Session sess)
        {
            RemoveBanner(page);
            if (sess.Veil != null)
            {
                sess.Veil.Visible = true;
                sess.Veil.BringToFront();   // the ActiveX control tries to stay on top
            }
            RdpHost host = sess.Host;
            ConnectionInfo c = sess.Info;
            int w = c.DesktopWidth, h = c.DesktopHeight;

            // Follow-the-window mode overrides a per-host fixed resolution: the session is
            // created at exactly the size of the area it will occupy, so nothing is letterboxed.
            if (Ui.FitToWindow || w <= 0 || h <= 0)
            {
                Size size = host.ClientSize;
                if (size.Width < 320 || size.Height < 240)
                {
                    // Layout not done yet: use the page's usable area (minus its padding).
                    size = page.ClientSize;
                    size.Width -= page.Padding.Horizontal;
                    size.Height -= page.Padding.Vertical;
                }
                w = size.Width;
                h = size.Height;
            }
            w = Math.Max(640, (w / 2) * 2);
            h = Math.Max(480, (h / 2) * 2);
            sess.FitApplied = false;
            try
            {
                bool ok = host.ApplySettings(c, w, h);
                if (!ok) throw new InvalidOperationException("无法设置 Server 属性，控件版本可能不兼容。");
                host.Connect();
                if (sess.Veil != null) sess.Veil.BringToFront();
                SetStatus("正在连接 " + c.Address + " ..." +
                    (string.IsNullOrEmpty(host.LastError) ? "" : " " + host.LastError));
            }
            catch (Exception ex)
            {
                ShowBanner(page, "连接失败：" + ex.Message);
                SetStatus("连接失败：" + ex.Message);
                Program.Log("CONNECT", ex);
                RecordEnd(sess, "连接失败", ex.Message);
            }
        }

        // ---------- follow-the-window sizing ----------

        private void ScheduleFit()
        {
            if (!Ui.FitToWindow || _fitTimer == null) return;
            _fitTimer.Stop();
            _fitTimer.Start();
        }

        private void ApplyFitToCurrent()
        {
            Session s = CurrentSession();
            if (s != null) ApplyFit(s);
        }

        /// <summary>
        /// Resizes the remote desktop to the exact area the control occupies. While connected
        /// this is a live resolution change, so no reconnect and no blank border.
        /// </summary>
        private void ApplyFit(Session s)
        {
            if (!Ui.FitToWindow || s == null || s.Host == null || s.Host.IsDisposed) return;
            Size area = s.Host.ClientSize;
            if (area.Width < 200 || area.Height < 150) return;
            try { s.Host.ResizeDesktop(area.Width, area.Height); }
            catch (Exception ex) { Program.Log("FIT", ex); }
        }

        private void ToggleFit()
        {
            Ui.FitToWindow = !Ui.FitToWindow;
            SyncFitUi();
            if (Ui.FitToWindow)
            {
                ApplyFitToCurrent();
                SetStatus("跟随窗口：开（分辨率随窗口调整）");
            }
            else
            {
                SetStatus("跟随窗口：关（使用主机配置的分辨率）");
            }
        }

        private void SyncFitUi()
        {
            if (_mFit != null) _mFit.Checked = Ui.FitToWindow;
            if (_statusBar != null) _statusBar.Invalidate();
        }

        private void ShowBanner(TabPage page, string message)
        {
            Session s;
            if (!_sessions.TryGetValue(page, out s)) return;
            RemoveBanner(page);
            if (s.Veil != null) s.Veil.Visible = false;
            Panel p = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Ui.CardAlt, Tag = "cardalt" };
            p.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen line = new Pen(Ui.Border))
                    e.Graphics.DrawLine(line, 0, p.Height - 1, p.Width, p.Height - 1);
            };
            Label l = new Label
            {
                Dock = DockStyle.Fill,
                Text = message,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 8, 0),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Font = Ui.NewF(9F),
                Tag = "title"
            };
            Button reconnect = new Button { Text = "重连", Dock = DockStyle.Right, Width = 70 };
            Ui.StyleButton(reconnect, RhButtonKind.Chip);
            reconnect.Click += delegate { Reconnect(page); };
            Button close = new Button { Text = "关闭标签", Dock = DockStyle.Right, Width = 80 };
            Ui.StyleButton(close, RhButtonKind.Chip);
            close.Click += delegate { CloseTab(page); };
            p.Controls.Add(l);
            p.Controls.Add(reconnect);
            p.Controls.Add(close);
            page.Controls.Add(p);
            s.Banner = p;
            ScheduleFit();
        }

        private void RemoveBanner(TabPage page)
        {
            Session s;
            if (!_sessions.TryGetValue(page, out s)) return;
            if (s.Banner == null) return;
            try { page.Controls.Remove(s.Banner); s.Banner.Dispose(); } catch { }
            s.Banner = null;
            ScheduleFit();
        }

        /// <summary>Full-tab overlay shown while the RDP session is being established.</summary>
        private Panel MakeVeil(ConnectionInfo c)
        {
            Panel veil = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Surface, Tag = "surface" };
            veil.Paint += delegate(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                int cx = veil.Width / 2;
                int cy = veil.Height / 2;
                Ui.DrawIcon(g, UiIcon.Logo, new Rectangle(cx - 24, cy - 78, 48, 48), Ui.Accent);
                Ui.DrawCentered(g, "正在连接 " + c.Address, Ui.F(12F, FontStyle.Bold), Ui.Text,
                    new Rectangle(cx - 220, cy - 22, 440, 28));
                Ui.DrawCentered(g, "建立 RDP 会话", Ui.F(9F), Ui.TextMuted,
                    new Rectangle(cx - 220, cy + 8, 440, 22));
            };
            return veil;
        }

        private void Reconnect(TabPage page)
        {
            if (_gridMode) ExitGrid(-1);
            Session s;
            if (!_sessions.TryGetValue(page, out s)) return;
            if (s.ConnectedAt.HasValue && !s.HistoryWritten)
                RecordEnd(s, "重连", "本地正常断开");
            try { s.Host.Disconnect(); } catch { }
            RemoveBanner(page);
            if (s.Veil != null) { try { page.Controls.Remove(s.Veil); s.Veil.Dispose(); } catch { } s.Veil = null; }
            page.Controls.Remove(s.Host);
            try { s.Host.Dispose(); } catch { }
            RdpHost host = new RdpHost();
            page.Controls.Add(host);
            s.Veil = MakeVeil(s.Info);
            page.Controls.Add(s.Veil);
            s.Host = host;
            s.Notified = false;
            s.HistoryWritten = false;
            s.ConnectedAt = null;
            s.ManualDisconnect = false;
            s.Started = DateTime.Now;
            Application.DoEvents();
            PrepareAndConnect(page, s);
            SetStatus("正在重连 " + s.Info.Address + " ...");
        }

        private void CloseTab(TabPage page)
        {
            if (_gridMode) ExitGrid(-1);
            if (page == null) return;
            Session s;
            if (_sessions.TryGetValue(page, out s))
            {
                if (s.ConnectedAt.HasValue && !s.HistoryWritten)
                    RecordEnd(s, "关闭标签", "本地正常断开");
                try { s.Host.Disconnect(); } catch { }
                _sessions.Remove(page);
            }
            try { _tabs.TabPages.Remove(page); } catch { }
            SyncTabWidth();
            if (s != null) { try { s.Host.Dispose(); } catch { } }
            try { page.Dispose(); } catch { }
            UpdateWelcome();
            SyncUi();
        }

        private void CloseOtherTabs()
        {
            TabPage keep = _tabs.SelectedTab;
            List<TabPage> pages = new List<TabPage>(_sessions.Keys);
            foreach (TabPage p in pages) if (p != keep) CloseTab(p);
        }

        private void CloseAllTabs()
        {
            List<TabPage> pages = new List<TabPage>(_sessions.Keys);
            foreach (TabPage p in pages) CloseTab(p);
        }

        private Session CurrentSession()
        {
            if (_tabs.SelectedTab == null) return null;
            Session s;
            return _sessions.TryGetValue(_tabs.SelectedTab, out s) ? s : null;
        }

        private void DisconnectCurrent()
        {
            Session s = CurrentSession();
            if (s == null) { SetStatus("当前没有打开的会话。"); return; }
            s.ManualDisconnect = true;
            try { s.Host.Disconnect(); } catch { }
            ShowBanner(_tabs.SelectedTab, "已主动断开连接，可点击重连。");
            SetStatus("已断开 " + s.Info.Address);
        }

        private void ReconnectCurrent()
        {
            if (_tabs.SelectedTab == null) { SetStatus("当前没有打开的会话。"); return; }
            Reconnect(_tabs.SelectedTab);
        }

        private void ToggleFullScreen()
        {
            Session s = CurrentSession();
            if (s == null) { SetStatus("当前没有打开的会话。"); return; }
            try
            {
                bool on = s.Host.ToggleFullScreen();
                s.FullScreenAtTick = on;   // already synced inside ToggleFullScreen
                SetStatus(on
                    ? "全屏（Ctrl+Alt+Break 退出）"
                    : "已退出全屏");
                if (!on) this.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("全屏切换失败：" + ex.Message, "RemoteHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            SyncUi();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            int online = 0;
            List<TabPage> pages = new List<TabPage>(_sessions.Keys);
            foreach (TabPage page in pages)
            {
                Session s = _sessions[page];
                if (s.Host == null || s.Host.IsDisposed) continue;
                int state = s.Host.ConnectedState;
                if (state == 1)
                {
                    online++;
                    if (!s.ConnectedAt.HasValue) s.ConnectedAt = DateTime.Now;
                    if (s.Veil != null && s.Veil.Visible) s.Veil.Visible = false;
                    // mstscax can enter / leave fullscreen on its own (Ctrl+Alt+Break):
                    // resync the remote resolution on every transition.
                    bool fs = s.Host.ReadFullScreen();
                    if (fs != s.FullScreenAtTick)
                    {
                        s.FullScreenAtTick = fs;
                        s.Host.SyncResolutionToFullScreen(fs);
                    }
                    // While fullscreen the desktop must stay at screen size - fitting it
                    // back to the (still window-sized) control area is what caused the
                    // big black letterbox bars.
                    if (Ui.FitToWindow && !fs) ApplyFit(s);
                }
                else
                {
                    // Do not let the "connecting" overlay hide a stuck connection forever.
                    if (s.Veil != null && s.Veil.Visible &&
                        (DateTime.Now - s.Started).TotalSeconds > 25)
                        s.Veil.Visible = false;
                    if (state == 0 && s.Host.EverConnected && !s.Notified)
                    {
                        s.Notified = true;
                        RecordEnd(s,
                            s.ManualDisconnect ? "主动断开" : "意外断开",
                            SessionHistory.DescribeReason(s.Host.DisconnectReasonCode));
                        ShowBanner(page, "远程会话已断开，可点击重连。");
                    }
                }
            }
            _onlineCount = online;
            int reachable = 0;
            lock (_probe)
                foreach (ProbeState st in _probe.Values)
                    if (st == ProbeState.Online) reachable++;
            _probeOnline = reachable;
            _statusDot = online > 0 ? Ui.Success : Ui.TextMuted;
            _lblInfo.Text = "在线 " + online + "   ·   标签 " + _sessions.Count +
                            "   ·   主机 " + _store.Items.Count + "   ·   可达 " +
                            reachable + "/" + _store.Items.Count;
            _badge.Invalidate();
            _statusBar.Invalidate();
            InvalidateStrip();
            _tree.Invalidate();
            if (_gridMode && _grid != null)
            {
                for (int i = 0; i < _gridPages.Count; i++)
                {
                    Session gs;
                    bool on = _sessions.TryGetValue(_gridPages[i], out gs) &&
                              gs.Host != null && !gs.Host.IsDisposed && gs.Host.IsOnline;
                    _grid.SetConnected(i, on);
                }
            }
            SyncFullScreenLabel();
        }

        private void SyncFullScreenLabel()
        {
            Session s = CurrentSession();
            bool on = s != null && s.Host.ReadFullScreen();
            _mFullScreen.Text = on ? "退出全屏 (F11)" : "切换全屏 (F11)";
        }

        private void SyncUi()
        {
            Session s = CurrentSession();
            this.Text = "RemoteHub - 内嵌远程桌面工作台" +
                        (s == null ? "" : "  [" + s.Info.Address + "]");
            SyncFullScreenLabel();
        }

        private void SetStatus(string message)
        {
            _lblMsg.Text = message;
        }

        /// <summary>Sends Ctrl+Alt+Del into the current remote session (secure attention sequence).</summary>
        private void SendSecureKey()
        {
            Session s = CurrentSession();
            if (s == null) { SetStatus("当前没有打开的会话。"); return; }
            string err = s.Host.SendCtrlAltDel();
            if (err == null)
            {
                SetStatus("已发送 Ctrl+Alt+Del");
                return;
            }
            SetStatus(err);
            if (_secureKeyHintShown) return;
            _secureKeyHintShown = true;
            MessageBox.Show(err + "\r\n\r\n" +
                "这是本机 Windows 版本对 RDP 控件的接口限制，不是软件故障。\r\n" +
                "在远程桌面窗口内按 Ctrl+Alt+End 可以达到完全相同的效果\r\n" +
                "（Ctrl+Alt+End 是 RDP 标准快捷键，等价于远程机的 Ctrl+Alt+Del）。",
                "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowHistory()
        {
            using (SessionHistoryDialog d = new SessionHistoryDialog()) d.ShowDialog(this);
        }

        private void ClearHistory()
        {
            if (MessageBox.Show("确定清空全部连接历史吗？此操作不可撤销。", "RemoteHub",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SessionHistory.Clear();
            SetStatus("连接历史已清空。");
        }

        /// <summary>Writes one immutable log row; a session is never recorded twice.</summary>
        private void RecordEnd(Session s, string outcome, string detail)
        {
            if (s == null || s.HistoryWritten || s.Info == null) return;
            s.HistoryWritten = true;
            DateTime start = s.ConnectedAt.HasValue ? s.ConnectedAt.Value : s.Started;
            DateTime end = DateTime.Now;
            SessionHistory.Append(new SessionRecord
            {
                Name = s.Info.Name,
                Address = s.Info.Address,
                UserName = s.Info.UserName,
                Started = start,
                Ended = end,
                Seconds = (int)Math.Max(0, (end - start).TotalSeconds),
                Outcome = outcome,
                Detail = detail ?? ""
            });
        }

        private void ShowFileTransfer()
        {
            Session s = CurrentSession();
            ConnectionInfo info = s != null ? s.Info : null;
            string state = (s != null && s.Host != null && !s.Host.IsDisposed)
                ? s.Host.RedirectionReport : "";
            using (FileTransferDialog d = new FileTransferDialog(info, _store.TransferFolder, state))
            {
                d.ShowDialog(this);
                if (!string.Equals(d.TransferFolder, _store.TransferFolder, StringComparison.OrdinalIgnoreCase))
                {
                    _store.TransferFolder = d.TransferFolder;
                    try { _store.Save(); } catch (Exception ex) { Program.Log("TRANSFER", ex); }
                }
            }
            SetStatus(@"磁盘映射与剪贴板共享：开");
        }

        private void ShowDiagnostics()
        {
            string report;
            try { report = Diagnostics.Run(); }
            catch (Exception ex) { report = "自检失败：" + ex; }
            using (DiagnosticsDialog d = new DiagnosticsDialog(report)) d.ShowDialog(this);
        }

        private void ShowShortcuts()
        {
            using (InfoDialog d = new InfoDialog("快捷键", "键盘与鼠标操作",
                "Ctrl + N  新建      Ctrl + F  搜索      Ctrl + W  关闭标签\r\n" +
                "Ctrl + T  文件共享      F5  刷新列表      F11  全屏\r\n" +
                "Enter  连接选中主机      Delete  删除选中主机\r\n\r\n" +
                "画面大小：默认「跟随窗口」，远程分辨率随窗口自动调整；\r\n" +
                "关闭后按主机配置的分辨率显示（需重连生效）。\r\n\r\n" +
                @"文件共享：工具栏「传文件」。远程机地址栏输入 \\tsclient\C" + "\r\n" +
                "即可读写本机磁盘；小文件可「放入剪贴板」后到远程 Ctrl+V。\r\n\r\n" +
                "安全键：工具栏「安全键」向远程发送 Ctrl+Alt+Del；\r\n" +
                "若提示不支持，在远程窗口按 Ctrl+Alt+End 效果相同。\r\n\r\n" +
                "连接历史：菜单「记录 → 查看连接历史」，记录时长与断开方式。\r\n\r\n" +
                "注意：会话建立后键盘优先发给远端，请用工具栏或右键菜单操作。\r\n" +
                "全屏状态可用 Ctrl+Alt+Break 退出。"))
                d.ShowDialog(this);
        }

        private void ShowAbout()
        {
            using (InfoDialog d = new InfoDialog("关于 RemoteHub", "RemoteHub v" + Application.ProductVersion,
                "内嵌远程桌面工作台\r\n" +
                "运行模式：" + ConnectionStore.ModeText + "\r\n" +
                "当前主题：" + Ui.ThemeName + "\r\n" +
                "画面尺寸：" + (Ui.FitToWindow ? "跟随窗口（分辨率随窗口自动调整）" : "固定分辨率（使用主机配置）") + "\r\n\r\n" +
                "远程画面由系统自带 RDP ActiveX 控件 (mstscax.dll) 直接承载在本程序窗口内，\r\n" +
                "不是启动 mstsc.exe 外部窗口，可多标签同时连接多台主机。\r\n\r\n" +
                "· 仅依赖 Windows 自带的 .NET Framework 4.x，无需额外运行时\r\n" +
                "· 不联网、不安装服务、被控端无需装任何 agent（只需开启系统远程桌面）\r\n" +
                "· 默认打通磁盘映射 + 剪贴板，远程机可直接读写本机文件（Ctrl+T）\r\n" +
                "· 音频可重定向到本机、留在远程或静音；工具栏可发送 Ctrl+Alt+Del\r\n" +
                "· 每次会话的开始、时长与断开原因都会写入连接历史（记录 → 查看）\r\n" +
                "· 密码使用当前 Windows 账户的 DPAPI 加密后落盘，明文仅存在于内存\r\n" +
                "· 安装版可从开始菜单 / 应用和功能卸载；绿色版直接删除目录即可"))
                d.ShowDialog(this);
        }

        // ==================== 1.3.0: probe / quick connect / scan / shot / grid / vault ====

        /// <summary>Re-checks every host on the thread pool; the tree repaints as results land.</summary>
        private void StartProbe(bool manual)
        {
            if (_store == null || IsDisposed) return;
            List<ConnectionInfo> items = new List<ConnectionInfo>(_store.Items);
            if (items.Count == 0) return;
            if (Environment.GetEnvironmentVariable("RH_PREVIEW_PROBE") == "1")
            {
                // Screenshot-only: exercise all three dot colours without network traffic.
                lock (_probe)
                {
                    _probe.Clear();
                    for (int i = 0; i < items.Count; i++)
                        _probe[items[i].Address] = (i % 3 == 0) ? ProbeState.Online
                            : (i % 3 == 1) ? ProbeState.Reachable : ProbeState.Offline;
                }
                _tree.Invalidate();
                return;
            }
            int gen = ++_probeGeneration;
            lock (_probe)
            {
                _probe.Clear();
                foreach (ConnectionInfo c in items) _probe[c.Address] = ProbeState.Checking;
            }
            _tree.Invalidate();
            if (manual) SetStatus("正在探测 " + items.Count + " 台主机的在线状态 ...");

            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(items,
                        new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 },
                        delegate(ConnectionInfo c)
                        {
                            ProbeState st = NetProbe.Check(c, 700);
                            try
                            {
                                BeginInvoke(new Action(delegate
                                {
                                    if (IsDisposed || gen != _probeGeneration) return;
                                    lock (_probe) _probe[c.Address] = st;
                                    _tree.Invalidate();
                                }));
                            }
                            catch { }
                        });
                }
                catch { }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || gen != _probeGeneration) return;
                        int ok = 0;
                        lock (_probe)
                        {
                            foreach (ProbeState st in _probe.Values)
                                if (st == ProbeState.Online) ok++;
                        }
                        _probeOnline = ok;
                        if (manual) SetStatus("在线状态探测完成：" + ok + " / " + items.Count + " 台 RDP 端口可达。");
                    }));
                }
                catch { }
            });
        }

        /// <summary>Type an IP / name and press Enter. Falls back to a temporary session.</summary>
        private void QuickConnect()
        {
            string text = (_quick.Text ?? "").Trim();
            if (text.Length == 0) { SetStatus("请输入 IP 或主机名称后再回车。"); return; }

            string user = "";
            int at = text.IndexOf('@');
            if (at > 0) { user = text.Substring(0, at); text = text.Substring(at + 1); }
            string host = text;
            int port = 3389;
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                int p;
                if (int.TryParse(text.Substring(colon + 1), out p) && p > 0 && p <= 65535)
                {
                    host = text.Substring(0, colon);
                    port = p;
                }
            }
            if (host.Length == 0) { SetStatus("地址为空。"); return; }

            ConnectionInfo match = null;
            int hits = 0;
            foreach (ConnectionInfo c in _store.Items)
            {
                bool hit = string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(c.Address, host + (port == 3389 ? "" : ":" + port),
                               StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrEmpty(c.Notes) &&
                            c.Notes.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit) { if (match == null) match = c; hits++; }
            }
            if (match != null)
            {
                OpenConnection(match.Clone());
                SetStatus("快速连接 " + match.Address +
                    (hits > 1 ? "（匹配到 " + hits + " 台，已连接第一台）" : "。"));
                _quick.Text = "";
                return;
            }

            ConnectionInfo temp = new ConnectionInfo();
            temp.Name = string.IsNullOrEmpty(user) ? host : user + "@" + host;
            temp.Host = host;
            temp.Port = port;
            temp.UserName = user;
            temp.Group = "临时连接";
            temp.ShareDrives = true;
            temp.ShareClipboard = true;
            OpenConnection(temp);
            _quick.Text = "";
            SetStatus("临时连接 " + temp.Address + "（未入台账，⋯ → 将当前会话保存到台账）。");
        }

        private void SaveCurrentToBook()
        {
            Session s = CurrentSession();
            if (s == null) { SetStatus("当前没有会话可保存。"); return; }
            ConnectionInfo c = s.Info.Clone();
            if (_store.IsDuplicate(c, null))
            {
                SetStatus("台账里已有 " + c.Address + "，无需重复保存。");
                return;
            }
            _store.Items.Add(c);
            _store.Save();
            BuildTree(CleanFilter());
            StartProbe(false);
            SetStatus("已把 " + c.Address + " 保存到主机台账。");
        }

        private void ShowLanScan()
        {
            using (LanScanDialog d = new LanScanDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                int added = 0, skipped = 0;
                foreach (ConnectionInfo c in d.Selected)
                {
                    if (_store.IsDuplicate(c, null)) { skipped++; continue; }
                    _store.Items.Add(c);
                    added++;
                }
                if (added > 0)
                {
                    _store.Save();
                    BuildTree(CleanFilter());
                    StartProbe(false);
                }
                SetStatus("局域网扫描：新增 " + added + " 台（分组「扫描发现」）" +
                    (skipped > 0 ? "，跳过 " + skipped + " 台重复。" : "。"));
            }
        }

        /// <summary>Grabs the visible remote desktop into a PNG and into the clipboard.</summary>
        private void CaptureSession()
        {
            Session s = CurrentSession();
            if (s == null || s.Host == null || s.Host.IsDisposed || !s.Host.Visible ||
                s.Host.Width < 20 || s.Host.Height < 20)
            {
                SetStatus("当前没有可见的会话画面可截图。");
                return;
            }
            try
            {
                Rectangle r = s.Host.RectangleToScreen(s.Host.ClientRectangle);
                if (r.Width < 20 || r.Height < 20) { SetStatus("会话区域太小，无法截图。"); return; }
                using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    string dir = Path.Combine(ConnectionStore.DirectoryPath, "Screenshots");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir,
                        SafeFileName(s.Info.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                    bmp.Save(file, ImageFormat.Png);
                    try { Clipboard.SetImage(new Bitmap(bmp)); } catch { }
                    SetStatus("已截图 " + file + "（同时已放入剪贴板）");
                }
            }
            catch (Exception ex) { SetStatus("截图失败：" + ex.Message); }
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "session";
            foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s.Length > 48 ? s.Substring(0, 48) : s;
        }

        private void ToggleGrid()
        {
            if (_gridMode) { ExitGrid(-1); return; }
            if (_sessions.Count < 2)
            {
                SetStatus("多画面宫格至少需要 2 个会话，请先连接两台主机。");
                return;
            }
            EnterGrid();
        }

        private void EnterGrid()
        {
            _gridPages.Clear();
            List<SessionGrid.Item> items = new List<SessionGrid.Item>();
            int max = Math.Min(4, _tabs.TabPages.Count);
            for (int i = 0; i < max; i++)
            {
                TabPage page = _tabs.TabPages[i];
                Session s;
                if (!_sessions.TryGetValue(page, out s) || s.Host == null || s.Host.IsDisposed) continue;
                items.Add(new SessionGrid.Item
                {
                    Title = s.Info.Name,
                    Sub = s.Info.Address,
                    View = s.Host,
                    Overlay = s.Veil
                });
                _gridPages.Add(page);
            }
            if (items.Count < 2) { SetStatus("可用会话不足 2 个，无法进入宫格。"); return; }
            _grid.ShowItems(items, 2);
            _gridMode = true;
            _tabs.Visible = false;
            _welcome.Visible = false;
            _grid.Visible = true;
            _grid.BringToFront();
            SetStatus("多画面宫格：" + items.Count + " 台并排显示，双击标题返回标签视图。");
        }

        private void ExitGrid(int selectIndex)
        {
            if (!_gridMode) return;
            _grid.ReleaseViews();
            foreach (TabPage page in _tabs.TabPages)
            {
                Session s;
                if (!_sessions.TryGetValue(page, out s) || s.Host == null || s.Host.IsDisposed) continue;
                if (s.Host.Parent != page)
                {
                    s.Host.Dock = DockStyle.Fill;
                    page.Controls.Add(s.Host);
                }
                if (s.Veil != null && s.Veil.Parent != page)
                {
                    s.Veil.Dock = DockStyle.Fill;
                    page.Controls.Add(s.Veil);
                    if (s.Veil.Visible) s.Veil.BringToFront();
                }
            }
            _grid.Visible = false;
            _tabs.Visible = true;
            _gridMode = false;
            _gridPages.Clear();
            if (selectIndex >= 0 && selectIndex < _tabs.TabPages.Count)
                _tabs.SelectedTab = _tabs.TabPages[selectIndex];
            UpdateWelcome();
            ScheduleFit();
            SetStatus("已返回标签视图。");
        }

        private void Grid_Activate(int index)
        {
            TabPage page = (index >= 0 && index < _gridPages.Count) ? _gridPages[index] : null;
            int at = page == null ? -1 : _tabs.TabPages.IndexOf(page);
            ExitGrid(at);
        }

        private void LockNow()
        {
            if (_vault == null || !_vault.Enabled)
            {
                SetStatus("尚未设置主密码。可在 ⋯ → 主密码 / 自动锁定… 中设置。");
                return;
            }
            if (_gridMode) ExitGrid(-1);
            _lock.ShowLock();
        }

        private void ShowVaultSettings()
        {
            using (VaultSettingsDialog d = new VaultSettingsDialog(_vault))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _vault = VaultConfig.Load();
                _lock.SetConfig(_vault);
                SetStatus(d.Removed ? "已取消主密码。" : "已保存主密码与自动锁定设置。");
            }
        }

        /// <summary>Preview helper for --shotgrid.</summary>
        internal void PreviewShowGrid()
        {
            if (_sessions.Count < 2) PreviewOpenFirstHost();
            EnterGrid();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_lock != null && _lock.Visible) return true;   // locked: swallow every shortcut
            if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { CaptureSession(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.L)) { LockNow(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.G)) { ToggleGrid(); return true; }
            if (keyData == (Keys.Control | Keys.Q)) { _quick.Focus(); _quick.SelectAll(); return true; }
            if (keyData == Keys.F11) { ToggleFullScreen(); return true; }
            if (keyData == (Keys.Control | Keys.W)) { CloseCurrentTab(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { NewConnection(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.T)) { ShowFileTransfer(); return true; }
            if (keyData == Keys.F5) { BuildTree(CleanFilter()); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CloseCurrentTab()
        {
            if (_tabs.SelectedTab == null) { SetStatus("当前没有打开的标签。"); return; }
            CloseTab(_tabs.SelectedTab);
        }

        /// <summary>Used only by the --shotopen preview mode to render a live tab.</summary>
        internal bool PreviewOpenFirstHost()
        {
            if (_store == null || _store.Items.Count == 0) return false;
            OpenConnection(_store.Items[0].Clone());
            if (Environment.GetEnvironmentVariable("RH_PREVIEW_TABS") == "2")
            {
                // Preview only: open a *different* host. Opening the same one twice
                // is rejected by Windows (one interactive session per host), which
                // would render the second cell as "not connected" in the screenshots.
                ConnectionInfo second = _store.Items.Count > 1
                    ? _store.Items[1]
                    : _store.Items[0];
                OpenConnection(second.Clone());
            }
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _timer.Stop(); } catch { }
            List<TabPage> pages = new List<TabPage>(_sessions.Keys);
            foreach (TabPage p in pages)
            {
                Session s;
                if (_sessions.TryGetValue(p, out s) && s != null)
                {
                    if (s.ConnectedAt.HasValue && !s.HistoryWritten)
                        RecordEnd(s, "关闭标签", "程序退出，会话结束");
                    try { s.Host.Disconnect(); } catch { }
                }
            }
            base.OnFormClosing(e);
        }
    }
}
