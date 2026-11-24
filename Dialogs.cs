using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>Shared dialog chrome: header, rounded cards, bordered input fields, footer.</summary>
    internal static class Dlg
    {
        public static void Init(Form f, int w, int h, string caption)
        {
            f.Text = caption;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.ShowInTaskbar = false;
            f.ClientSize = new Size(w, h);
            f.Font = Ui.NewF(9F);
            f.BackColor = Ui.CardAlt;
            f.ForeColor = Ui.Text;
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
        }

        public static Panel Header(Form f, int w, string title, string subtitle, UiIcon icon)
        {
            // Plain dialog header: title and one line of context, no accent bar and no
            // decorative icon - that combo is the usual "designed by a generator" tell.
            Panel p = new Panel { Dock = DockStyle.Top, Height = 64, Tag = "card", BackColor = Ui.Card };
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen line = new Pen(Ui.Border)) g.DrawLine(line, 0, p.Height - 1, p.Width, p.Height - 1);
            };
            Label t = new Label
            {
                Text = title,
                Location = new Point(20, 16),
                Size = new Size(w - 46, 24),
                Font = Ui.NewF(11F, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            Label st = new Label
            {
                Text = subtitle,
                Location = new Point(21, 38),
                Size = new Size(w - 46, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            p.Controls.Add(t);
            p.Controls.Add(st);
            return p;
        }

        public static Panel Card(int x, int y, int w, int h, string title, UiIcon icon)
        {
            Panel p = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                Tag = "card",
                BackColor = Ui.Card
            };
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using (SolidBrush fill = new SolidBrush(Ui.Card)) Ui.FillRound(g, fill, r, 4);
                using (Pen line = new Pen(Ui.Border)) Ui.DrawRound(g, line, r, 4);
            };
            Label t = new Label
            {
                Text = title,
                Location = new Point(14, 7),
                Size = new Size(w - 30, 22),
                Font = Ui.NewF(9.5F, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            p.Controls.Add(t);
            Ui.RoundRegion(p, 4);
            return p;
        }

        /// <summary>A rounded border wrapper that hosts a borderless editor control.</summary>
        public static Panel Wrap(Control inner, int height)
        {
            bool selfDrawn = inner is RhSelect || inner is RhNumber;
            Panel box = new Panel
            {
                Tag = selfDrawn ? "plain" : "fieldwrap",
                BackColor = selfDrawn ? Color.Transparent : Ui.Field,
                Height = height
            };
            if (!selfDrawn)
            {
                box.Paint += delegate(object s, PaintEventArgs e)
                {
                    Graphics g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(0, 0, box.Width - 1, box.Height - 1);
                    using (SolidBrush b = new SolidBrush(Ui.Field)) Ui.FillRound(g, b, r, Ui.Radius);
                    using (Pen line = new Pen(Ui.Border)) Ui.DrawRound(g, line, r, Ui.Radius);
                };
                inner.BackColor = Ui.Field;
                inner.ForeColor = Ui.Text;
                inner.Font = Ui.NewF(9F);
                inner.Tag = "field";
                if (inner is TextBox) ((TextBox)inner).BorderStyle = BorderStyle.None;
                else if (inner is NumericUpDown) ((NumericUpDown)inner).BorderStyle = BorderStyle.None;
                inner.Location = new Point(9, Math.Max(1, (height - 21) / 2));
                inner.Size = new Size(Math.Max(20, box.Width - 18), 21);
                box.Resize += delegate
                {
                    inner.Width = Math.Max(20, box.Width - 18);
                    Ui.RoundRegion(box, Ui.Radius);
                };
                Ui.RoundRegion(box, Ui.Radius);
            }
            else
            {
                inner.Dock = DockStyle.Fill;
                inner.BackColor = Color.Transparent;
                inner.Tag = "rhselect";
            }            box.Controls.Add(inner);
            return box;
        }

        public static void AddRow(Panel card, int y, string label, Control field, int width)
        {
            Label l = new Label
            {
                Text = label,
                Location = new Point(16, y + 5),
                Size = new Size(80, 20),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            field.SetBounds(100, y, width, 28);
            card.Controls.Add(l);
            card.Controls.Add(field);
        }

        public static Panel Footer(int w)
        {
            Panel p = new Panel { Dock = DockStyle.Bottom, Height = 54, Tag = "card", BackColor = Ui.Card };
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen line = new Pen(Ui.Border)) e.Graphics.DrawLine(line, 0, 0, p.Width, 0);
            };
            p.SetBounds(0, 0, w, 54);
            return p;
        }

        public static RhButton Button(string text, UiIcon icon, RhButtonKind kind, int width)
        {
            return new RhButton
            {
                Text = text,
                Icon = icon,
                Kind = kind,
                Size = new Size(width, 34)
            };
        }

        public static void PlaceRight(Panel footer, int formWidth, RhButton b, int offset)
        {
            b.Location = new Point(formWidth - 14 - offset - b.Width, 10);
            footer.Controls.Add(b);
        }
    }

    /// <summary>Add / edit one connection entry.</summary>
    internal sealed class ConnectionDialog : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly RhSelect _group = new RhSelect(true);
        private readonly TextBox _host = new TextBox();
        private readonly RhNumber _port = new RhNumber();
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _password = new TextBox();
        private readonly RhCheck _showPwd = new RhCheck();
        private readonly TextBox _domain = new TextBox();
        private readonly TextBox _notes = new TextBox();
        private readonly RhSelect _depth = new RhSelect();
        private readonly RhSelect _resolution = new RhSelect();
        private readonly RhSelect _audio = new RhSelect();
        private readonly RhNumber _width = new RhNumber();
        private readonly RhNumber _height = new RhNumber();
        private readonly RhCheck _smart = new RhCheck();
        private readonly RhCheck _compat = new RhCheck();
        private readonly RhCheck _shareDrives = new RhCheck();
        private readonly RhCheck _shareClipboard = new RhCheck();
        private readonly RhButton _ok;
        private readonly RhButton _cancel;

        public ConnectionInfo Result { get; private set; }

        public ConnectionDialog(ConnectionInfo source, List<string> groups, bool isNew)
        {
            const int W = 540, H = 792;
            Dlg.Init(this, W, H, isNew ? "新建连接" : "编辑连接");

            Panel header = Dlg.Header(this, W,
                isNew ? "新建连接" : "编辑连接",
                isNew ? "地址、账号与显示设置" : "已打开的会话需重连后生效",
                isNew ? UiIcon.Plus : UiIcon.Edit);

            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "cardalt", BackColor = Ui.CardAlt };

            Panel basic = Dlg.Card(16, 12, 508, 244, "连接信息", UiIcon.Server);
            _group.SetItems(groups.ToArray());
            _port.Minimum = 1; _port.Maximum = 65535; _port.Value = 3389;
            _password.UseSystemPasswordChar = true;
            _showPwd.Text = "显示";
            _showPwd.CheckedChanged += delegate { _password.UseSystemPasswordChar = !_showPwd.Checked; };
            Ui.StyleCheck(_showPwd);

            Dlg.AddRow(basic, 34, "名称", Dlg.Wrap(_name, 28), 392);
            Dlg.AddRow(basic, 64, "分组", Dlg.Wrap(_group, 28), 392);
            Dlg.AddRow(basic, 94, "主机 / IP", Dlg.Wrap(_host, 28), 392);
            Dlg.AddRow(basic, 124, "端口", Dlg.Wrap(_port, 28), 392);
            Dlg.AddRow(basic, 154, "用户名", Dlg.Wrap(_user, 28), 392);
            Dlg.AddRow(basic, 184, "密码", Dlg.Wrap(_password, 28), 300);
            _showPwd.Location = new Point(408, 188);
            _showPwd.Size = new Size(84, 22);
            basic.Controls.Add(_showPwd);
            Dlg.AddRow(basic, 214, "域", Dlg.Wrap(_domain, 28), 392);

            Panel display = Dlg.Card(16, 266, 508, 152, "显示", UiIcon.Monitor);
            _depth.SetItems("32 位", "24 位", "16 位", "15 位", "8 位");
            _depth.SelectedIndex = 0;
            _resolution.SetItems(
                "自适应 (跟随标签窗口)", "1024 x 768", "1280 x 720", "1366 x 768",
                "1600 x 900", "1920 x 1080", "自定义");
            _resolution.SelectedIndex = 0;
            _resolution.ValueChanged += delegate { SyncResolution(); };
            _width.Minimum = 640; _width.Maximum = 7680; _width.Value = 1920; _width.Enabled = false;
            _height.Minimum = 480; _height.Maximum = 4320; _height.Value = 1080; _height.Enabled = false;
            _smart.Text = "智能缩放";
            _smart.Checked = true;
            Ui.StyleCheck(_smart);

            Dlg.AddRow(display, 34, "颜色深度", Dlg.Wrap(_depth, 28), 392);
            Dlg.AddRow(display, 64, "分辨率", Dlg.Wrap(_resolution, 28), 392);
            Label wh = new Label
            {
                Text = "宽 × 高",
                Location = new Point(16, 99),
                Size = new Size(80, 20),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            Panel w1 = Dlg.Wrap(_width, 28); w1.SetBounds(100, 94, 84, 28);
            Label mul = new Label
            {
                Text = "×",
                Location = new Point(187, 98),
                Size = new Size(14, 20),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Tag = "muted"
            };
            Panel w2 = Dlg.Wrap(_height, 28); w2.SetBounds(202, 94, 84, 28);
            Label tip = new Label
            {
                Text = "自定义",
                Location = new Point(296, 99),
                Size = new Size(200, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            display.Controls.AddRange(new Control[] { wh, w1, w2, mul, tip });
            _smart.Location = new Point(100, 126);
            _smart.Size = new Size(390, 22);
            display.Controls.Add(_smart);

            Panel advanced = Dlg.Card(16, 428, 508, 212, "高级设置（文件共享 / 音频）", UiIcon.Shield);
            _compat.Text = "兼容模式（Win2003 / XP）";
            _compat.Location = new Point(16, 34);
            _compat.Size = new Size(480, 22);
            Ui.StyleCheck(_compat);
            advanced.Controls.Add(_compat);
            Dlg.AddRow(advanced, 62, "备注", Dlg.Wrap(_notes, 28), 392);
            _audio.SetItems("本机播放", "远程播放", "静音");
            _audio.SelectedIndex = 0;
            Dlg.AddRow(advanced, 94, "远程音频", Dlg.Wrap(_audio, 28), 392);

            Label shareTip = new Label
            {
                Text = @"远程机可用 \tsclient 访问本机磁盘，改动后需重连",
                Location = new Point(16, 128),
                Size = new Size(480, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            advanced.Controls.Add(shareTip);
            _shareDrives.Text = "映射本机磁盘到远程";
            _shareClipboard.Text = "共享剪贴板（可传文件）";
            _shareDrives.Location = new Point(16, 150);
            _shareClipboard.Location = new Point(264, 150);
            _shareDrives.Size = new Size(240, 22);
            _shareClipboard.Size = new Size(228, 22);
            Ui.StyleCheck(_shareDrives);
            Ui.StyleCheck(_shareClipboard);
            advanced.Controls.AddRange(new Control[] { _shareDrives, _shareClipboard });
            Label usbTip = new Label
            {
                Text = "新插入的 U 盘需在远程按 F5 刷新。",
                Location = new Point(16, 174),
                Size = new Size(480, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            advanced.Controls.Add(usbTip);

            content.Controls.Add(basic);
            content.Controls.Add(display);
            content.Controls.Add(advanced);

            Panel footer = Dlg.Footer(W);
            _ok = Dlg.Button("保存", UiIcon.Check, RhButtonKind.Primary, 96);
            _ok.Click += delegate { if (ValidateAndFill()) this.DialogResult = DialogResult.OK; };
            _cancel = Dlg.Button("取消", UiIcon.Close, RhButtonKind.Ghost, 96);
            _cancel.DialogResult = DialogResult.Cancel;
            Dlg.PlaceRight(footer, W, _ok, 0);
            Dlg.PlaceRight(footer, W, _cancel, 104);

            this.Controls.Add(content);   // Fill
            this.Controls.Add(footer);    // Bottom
            this.Controls.Add(header);    // Top
            this.AcceptButton = _ok;
            this.CancelButton = _cancel;

            _name.Text = source.Name;
            _group.Value = source.Group;
            _host.Text = source.Host;
            _port.Value = source.Port > 0 ? source.Port : 3389;
            _user.Text = source.UserName;
            _password.Text = source.Password;
            _domain.Text = source.Domain;
            _notes.Text = source.Notes;
            _compat.Checked = source.CompatMode;
            _smart.Checked = source.SmartSizing;
            _shareDrives.Checked = source.ShareDrives;
            _shareClipboard.Checked = source.ShareClipboard;
            SetAudio(source.AudioMode);
            SetDepth(source.ColorDepth);
            SetResolution(source.DesktopWidth, source.DesktopHeight);
            Result = source.Clone();

            Ui.ApplyTheme(this);
        }

        private void SetAudio(int mode)
        {
            _audio.SelectedIndex = (mode >= 0 && mode <= 2) ? mode : 0;
        }

        private void SetDepth(int depth)
        {
            string[] vals = { "32", "24", "16", "15", "8" };
            for (int i = 0; i < vals.Length; i++)
                if (vals[i] == depth.ToString()) { _depth.SelectedIndex = i; return; }
            _depth.SelectedIndex = 0;
        }

        private void SetResolution(int w, int h)
        {
            if (w <= 0 || h <= 0) { _resolution.SelectedIndex = 0; return; }
            string key = w + " x " + h;
            for (int i = 0; i < _resolution.ItemCount; i++)
                if (_resolution.ItemAt(i) == key) { _resolution.SelectedIndex = i; return; }
            _resolution.SelectedIndex = _resolution.ItemCount - 1;
            _width.Value = Math.Min(Math.Max(w, 640), 7680);
            _height.Value = Math.Min(Math.Max(h, 480), 4320);
        }

        private void SyncResolution()
        {
            bool custom = _resolution.SelectedIndex == _resolution.ItemCount - 1;
            _width.Enabled = custom;
            _height.Enabled = custom;
        }

        private bool ValidateAndFill()
        {
            string host = _host.Text.Trim();
            if (host.Length == 0)
            {
                MessageBox.Show("请填写主机地址。", "RemoteHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _host.Focus();
                return false;
            }
            ConnectionInfo c = Result;
            c.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : host;
            c.Group = _group.Value.Trim();
            c.Host = host;
            c.Port = (int)_port.Value;
            c.UserName = _user.Text.Trim();
            c.Password = _password.Text;
            c.Domain = _domain.Text.Trim();
            c.Notes = _notes.Text.Trim();
            c.SmartSizing = _smart.Checked;
            c.CompatMode = _compat.Checked;
            c.ShareDrives = _shareDrives.Checked;
            c.ShareClipboard = _shareClipboard.Checked;
            c.AudioMode = Math.Max(0, Math.Min(2, _audio.SelectedIndex));
            string[] depths = { "32", "24", "16", "15", "8" };
            c.ColorDepth = int.Parse(depths[Math.Max(0, _depth.SelectedIndex)]);
            if (_resolution.SelectedIndex == 0)
            {
                c.DesktopWidth = 0;
                c.DesktopHeight = 0;
            }
            else if (_resolution.SelectedIndex == _resolution.ItemCount - 1)
            {
                c.DesktopWidth = (int)_width.Value;
                c.DesktopHeight = (int)_height.Value;
            }
            else
            {
                string[] parts = _resolution.Value.Split(new string[] { " x " }, StringSplitOptions.None);
                c.DesktopWidth = int.Parse(parts[0]);
                c.DesktopHeight = int.Parse(parts[1]);
            }
            return true;
        }
    }

    /// <summary>Bulk import of hosts from pasted text or a CSV file.</summary>
    internal sealed class ImportDialog : Form
    {
        private readonly TextBox _text = new TextBox();
        public string ImportText { get { return _text.Text; } }

        public ImportDialog()
        {
            const int W = 660, H = 552;
            Dlg.Init(this, W, H, "批量导入主机");

            Panel header = Dlg.Header(this, W, "批量导入主机",
                "从 CSV 或文本导入", UiIcon.Import);

            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "cardalt", BackColor = Ui.CardAlt };

            Panel hint = Dlg.Card(12, 10, 636, 96, "支持的格式", UiIcon.Info);
            Label tip = new Label
            {
                Location = new Point(18, 32),
                Size = new Size(600, 58),
                Text =
                    "每行一台主机，逗号分隔（字段含逗号可用双引号包裹）：\r\n" +
                    "  host   /   host:3390   /   host,user   /   host,user,password\r\n" +
                    "  name,group,host,port,user,password,domain,notes\r\n" +
                    "# 开头为注释；首行表头自动跳过；同地址+端口+用户名自动去重。",
                Font = Ui.NewF(9F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            hint.Controls.Add(tip);

            Panel input = Dlg.Card(12, 114, 636, 300, "主机列表", UiIcon.Server);
            _text.Multiline = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.AcceptsTab = true;
            _text.BorderStyle = BorderStyle.None;
            _text.BackColor = Ui.Field;
            _text.ForeColor = Ui.Text;
            _text.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            _text.Tag = "field";
            _text.SetBounds(10, 34, 616, 256);
            input.Controls.Add(_text);

            content.Controls.Add(hint);
            content.Controls.Add(input);

            Panel footer = Dlg.Footer(W);
            RhButton fromFile = Dlg.Button("从文件读取...", UiIcon.Folder, RhButtonKind.Ghost, 130);
            fromFile.Location = new Point(14, 10);
            fromFile.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "CSV/文本|*.csv;*.txt;*.ini|所有文件|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        try { _text.Text = File.ReadAllText(d.FileName, System.Text.Encoding.Default); }
                        catch (Exception ex) { MessageBox.Show("读取失败：" + ex.Message); }
                    }
                }
            };
            RhButton ok = Dlg.Button("导入", UiIcon.Check, RhButtonKind.Primary, 96);
            ok.DialogResult = DialogResult.OK;
            RhButton cancel = Dlg.Button("取消", UiIcon.Close, RhButtonKind.Ghost, 96);
            cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(fromFile);
            Dlg.PlaceRight(footer, W, ok, 0);
            Dlg.PlaceRight(footer, W, cancel, 104);

            this.Controls.Add(content);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.AcceptButton = ok;
            this.CancelButton = cancel;
            Ui.ApplyTheme(this);
        }
    }

    /// <summary>Shows the ActiveX environment report.</summary>
    internal sealed class DiagnosticsDialog : Form
    {
        public DiagnosticsDialog(string report)
        {
            const int W = 840, H = 574;
            Dlg.Init(this, W, H, "环境自检 - RDP 控件");

            Panel header = Dlg.Header(this, W, "环境自检",
                "本机 RDP 控件检查", UiIcon.Shield);

            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "cardalt", BackColor = Ui.CardAlt };
            Panel card = Dlg.Card(12, 10, 816, 430, "检测报告", UiIcon.Info);
            TextBox box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Field,
                ForeColor = Ui.Text,
                Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
                Text = report,
                Tag = "field"
            };
            box.SetBounds(10, 34, 796, 386);
            card.Controls.Add(box);
            content.Controls.Add(card);

            Panel footer = Dlg.Footer(W);
            RhButton copy = Dlg.Button("复制报告", UiIcon.Copy, RhButtonKind.Ghost, 116);
            copy.Location = new Point(14, 10);
            copy.Click += delegate { try { Clipboard.SetText(report); } catch { } };
            RhButton close = Dlg.Button("关闭", UiIcon.Close, RhButtonKind.Primary, 96);
            close.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(copy);
            Dlg.PlaceRight(footer, W, close, 0);

            this.Controls.Add(content);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.CancelButton = close;
            Ui.ApplyTheme(this);
        }
    }

    /// <summary>Styled read-only information dialog (shortcuts / about).</summary>
    internal sealed class InfoDialog : Form
    {
        public InfoDialog(string caption, string title, string body)
        {
            const int W = 600, H = 528;
            Dlg.Init(this, W, H, caption);

            Panel header = Dlg.Header(this, W, title, "RemoteHub  ·  内嵌远程桌面工作台", UiIcon.Logo);
            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "cardalt", BackColor = Ui.CardAlt };
            Panel card = Dlg.Card(14, 12, 572, 382, "详细信息", UiIcon.Info);
            Label text = new Label
            {
                Location = new Point(18, 34),
                Size = new Size(536, 338),
                Text = body,
                Font = Ui.NewF(9.5F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            card.Controls.Add(text);
            content.Controls.Add(card);

            Panel footer = Dlg.Footer(W);
            RhButton close = Dlg.Button("关闭", UiIcon.Check, RhButtonKind.Primary, 96);
            close.DialogResult = DialogResult.Cancel;
            Dlg.PlaceRight(footer, W, close, 0);

            this.Controls.Add(content);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.CancelButton = close;
            Ui.ApplyTheme(this);
        }
    }
}
