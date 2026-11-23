using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// Local &lt;-&gt; remote file helper. RDP already provides the transport; this dialog
    /// just makes the three usable paths obvious instead of hiding them behind the stock
    /// client's "Local Resources" tab.
    /// </summary>
    internal sealed class FileTransferDialog : Form
    {
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _dir = new TextBox();
        private Label _hint;

        public string TransferFolder { get { return _dir.Text.Trim(); } }

        public FileTransferDialog(ConnectionInfo info, string transferFolder, string redirectionState)
        {
            const int W = 640, H = 664;
            string state = info == null
                ? "当前没有打开的会话；共享开关将在下次连接时生效"
                : info.Address + "   ·   " +
                  (info.ShareDrives ? "磁盘映射 开" : "磁盘映射 关") + " / " +
                  (info.ShareClipboard ? "剪贴板 开" : "剪贴板 关");

            Dlg.Init(this, W, H, "本机 ↔ 远程 文件共享");
            Panel header = Dlg.Header(this, W, "本机 ↔ 远程 文件共享",
                "磁盘映射与剪贴板", UiIcon.Folder);

            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "surface", BackColor = Ui.Surface };

            // ---------- 1. drive redirection ----------
            Panel disk = Dlg.Card(16, 10, W - 32, 168, "磁盘映射", UiIcon.Server);
            Label diskText = new Label
            {
                Location = new Point(18, 30),
                Size = new Size(W - 68, 66),
                Text =
                    "远程桌面打开资源管理器，地址栏输入 \\tsclient\\C（C 换成本机盘符）；\r\n" +
                    "或在「此电脑」的「重定向的驱动器」里找本机磁盘。\r\n" +
                    "远程看不到本机盘时，检查组策略是否禁用驱动器重定向。",
                Font = Ui.NewF(9F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            Label diskState = new Label
            {
                Location = new Point(18, 97),
                Size = new Size(W - 68, 22),
                Text = state + (string.IsNullOrEmpty(redirectionState) ? "" : "   ·   " + redirectionState),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            RhButton copyPath = Dlg.Button(@"复制 \tsclient 路径", UiIcon.Copy, RhButtonKind.Ghost, 186);
            copyPath.Location = new Point(18, 124);
            copyPath.Click += delegate
            {
                try
                {
                    Clipboard.SetText(@"\tsclient\C");
                    SetHint(@"已复制 \tsclient\C，到远程桌面地址栏粘贴即可。");
                }
                catch (Exception ex) { MessageBox.Show("复制失败：" + ex.Message); }
            };
            RhButton openLocal = Dlg.Button("打开本机文件", UiIcon.Folder, RhButtonKind.Ghost, 140);
            openLocal.Location = new Point(212, 124);
            openLocal.Click += delegate { OpenFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); };
            disk.Controls.AddRange(new Control[] { diskText, diskState, copyPath, openLocal });

            // ---------- 2. clipboard fast transfer ----------
            Panel clip = Dlg.Card(16, 186, W - 32, 208, "剪贴板", UiIcon.Copy);
            Label clipText = new Label
            {
                Location = new Point(18, 30),
                Size = new Size(W - 68, 40),
                Text =
                    "选好文件 → 放入剪贴板 → 远程窗口 Ctrl+V。\r\n" +
                    "磁盘映射被禁用时用这个。",
                Font = Ui.NewF(9F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            _list.SetBounds(18, 72, W - 68, 86);
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Ui.Field;
            _list.ForeColor = Ui.Text;
            _list.Font = Ui.NewF(9F);
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 22;
            _list.Tag = "field";
            _list.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= _list.Items.Count) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (SolidBrush b = new SolidBrush(sel ? Ui.CardAlt : Ui.Field))
                    e.Graphics.FillRectangle(b, e.Bounds);
                using (SolidBrush t = new SolidBrush(sel ? Ui.Accent : Ui.Text))
                    e.Graphics.DrawString(_list.Items[e.Index].ToString(), Ui.F(9F), t,
                        new PointF(e.Bounds.Left + 6, e.Bounds.Top + 3));
            };

            RhButton addFiles = Dlg.Button("添加文件", UiIcon.Plus, RhButtonKind.Chip, 96);
            addFiles.Location = new Point(18, 166);
            addFiles.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Multiselect = true;
                    d.Filter = "所有文件|*.*";
                    if (d.ShowDialog(this) == DialogResult.OK)
                        foreach (string f in d.FileNames) AddItem(f);
                }
            };
            RhButton addFolder = Dlg.Button("添加文件夹", UiIcon.Folder, RhButtonKind.Chip, 116);
            addFolder.Location = new Point(120, 166);
            addFolder.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择要发送到远程的文件夹";
                    if (d.ShowDialog(this) == DialogResult.OK) AddItem(d.SelectedPath);
                }
            };
            RhButton clear = Dlg.Button("清空", UiIcon.Trash, RhButtonKind.Chip, 80);
            clear.Location = new Point(242, 166);
            clear.Click += delegate { _list.Items.Clear(); SetHint("已清空待发送列表。"); };
            RhButton send = Dlg.Button("放入剪贴板", UiIcon.Export, RhButtonKind.Primary, 120);
            send.Location = new Point(328, 166);
            send.Click += PutClipboard;
            clip.Controls.AddRange(new Control[] { clipText, _list, addFiles, addFolder, clear, send });

            // ---------- 3. receiving folder ----------
            Panel recv = Dlg.Card(16, 402, W - 32, 140, "从远程取回", UiIcon.Import);
            Label recvText = new Label
            {
                Location = new Point(18, 30),
                Size = new Size(W - 68, 40),
                Text =
                    "远程 Ctrl+C → 本机收件目录 Ctrl+V，RDP 自动传回。\r\n" +
                    "点「打开收件目录」直接粘贴。",
                Font = Ui.NewF(9F),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            Label dirLabel = new Label
            {
                Text = "收件目录",
                Location = new Point(18, 81),
                Size = new Size(80, 20),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            _dir.Text = string.IsNullOrEmpty(transferFolder)
                ? ConnectionStore.DefaultTransferFolder : transferFolder;
            Panel dirWrap = Dlg.Wrap(_dir, 28);
            dirWrap.SetBounds(100, 76, 414, 28);
            RhButton browse = Dlg.Button("浏览", UiIcon.Folder, RhButtonKind.Chip, 76);
            browse.Location = new Point(520, 76);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择从远程取回文件的落地目录";
                    if (Directory.Exists(_dir.Text)) d.SelectedPath = _dir.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _dir.Text = d.SelectedPath;
                }
            };
            RhButton openRecv = Dlg.Button("打开收件目录", UiIcon.Export, RhButtonKind.Chip, 140);
            openRecv.Location = new Point(18, 108);
            openRecv.Click += delegate
            {
                string p = _dir.Text.Trim();
                if (p.Length == 0) p = ConnectionStore.DefaultTransferFolder;
                try { Directory.CreateDirectory(p); OpenFolder(p); SetHint("已打开收件目录，切回本机按 Ctrl+V 粘贴即可。"); }
                catch (Exception ex) { MessageBox.Show("打开目录失败：" + ex.Message); }
            };
            recv.Controls.AddRange(new Control[] { recvText, dirLabel, dirWrap, browse, openRecv });

            content.Controls.Add(disk);
            content.Controls.Add(clip);
            content.Controls.Add(recv);

            Panel footer = Dlg.Footer(W);
            _hint = new Label
            {
                Text = "改动共享开关后，需要重连当前会话才生效。",
                Location = new Point(14, 17),
                Size = new Size(330, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            RhButton close = Dlg.Button("关闭", UiIcon.Check, RhButtonKind.Primary, 96);
            close.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(_hint);
            Dlg.PlaceRight(footer, W, close, 0);

            this.Controls.Add(content);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.CancelButton = close;
            Ui.ApplyTheme(this);
        }

        private void AddItem(string path)
        {
            if (string.IsNullOrEmpty(path) || _list.Items.Contains(path)) return;
            _list.Items.Add(path);
            SetHint("已加入 " + _list.Items.Count + " 项，点「放入剪贴板」后到远程窗口粘贴。");
        }

        private void PutClipboard(object sender, EventArgs e)
        {
            if (_list.Items.Count == 0)
            {
                MessageBox.Show("请先添加要发送的文件或文件夹。", "RemoteHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringCollection col = new StringCollection();
            foreach (object o in _list.Items) col.Add(o.ToString());
            try
            {
                Clipboard.SetFileDropList(col);
                SetHint("已放入剪贴板（" + col.Count + " 项）。切到远程桌面按 Ctrl+V 粘贴。");
            }
            catch (Exception ex)
            {
                MessageBox.Show("写入剪贴板失败：" + ex.Message, "RemoteHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { MessageBox.Show("打开目录失败：" + ex.Message); }
        }

        private void SetHint(string text)
        {
            if (_hint != null) _hint.Text = text;
        }
    }
}
