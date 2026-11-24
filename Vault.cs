using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// Screen-lock for the address book. This is a UI lock (it stops somebody from
    /// opening the app and reading the saved hosts); connection passwords stay
    /// protected by Windows DPAPI exactly as before. The password itself is never
    /// stored - only a salted SHA-256 digest in vault.ini.
    /// </summary>
    internal sealed class VaultConfig
    {
        private byte[] _salt = new byte[0];
        private byte[] _hash = new byte[0];
        public int IdleMinutes = 0;

        public bool Enabled { get { return _hash.Length > 0; } }

        private static string FilePath
        {
            get { return Path.Combine(ConnectionStore.DirectoryPath, "vault.ini"); }
        }

        public static VaultConfig Load()
        {
            VaultConfig v = new VaultConfig();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string s = line.Trim();
                        int i = s.IndexOf('=');
                        if (i <= 0) continue;
                        string k = s.Substring(0, i).Trim();
                        string val = s.Substring(i + 1).Trim();
                        if (k.Equals("salt", StringComparison.OrdinalIgnoreCase)) v._salt = FromHex(val);
                        else if (k.Equals("hash", StringComparison.OrdinalIgnoreCase)) v._hash = FromHex(val);
                        else if (k.Equals("idle", StringComparison.OrdinalIgnoreCase))
                        {
                            int n;
                            if (int.TryParse(val, out n) && n >= 0 && n <= 240) v.IdleMinutes = n;
                        }
                    }
                }
            }
            catch { }
            return v;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConnectionStore.DirectoryPath);
                File.WriteAllText(FilePath,
                    "salt=" + ToHex(_salt) + "\r\n" +
                    "hash=" + ToHex(_hash) + "\r\n" +
                    "idle=" + IdleMinutes + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        public void SetPassword(string password)
        {
            _salt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(_salt);
            _hash = Digest(_salt, password);
        }

        public void ClearPassword()
        {
            _salt = new byte[0];
            _hash = new byte[0];
            IdleMinutes = 0;
        }

        public bool Verify(string password)
        {
            if (!Enabled) return true;
            byte[] d = Digest(_salt, password);
            if (d.Length != _hash.Length) return false;
            for (int i = 0; i < d.Length; i++) if (d[i] != _hash[i]) return false;
            return true;
        }

        private static byte[] Digest(byte[] salt, string password)
        {
            byte[] pwd = Encoding.UTF8.GetBytes(password ?? "");
            byte[] buf = new byte[salt.Length + pwd.Length];
            Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
            Buffer.BlockCopy(pwd, 0, buf, salt.Length, pwd.Length);
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(buf);
        }

        private static string ToHex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }

        private static byte[] FromHex(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.Length % 2) != 0) return new byte[0];
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                byte v;
                if (!byte.TryParse(s.Substring(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out v)) return new byte[0];
                b[i] = v;
            }
            return b;
        }
    }

    /// <summary>Elapsed time since the last keyboard / mouse input anywhere on the desktop.</summary>
    internal static class IdleWatcher
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static TimeSpan Idle
        {
            get
            {
                LASTINPUTINFO li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
                uint now = unchecked((uint)Environment.TickCount);
                uint delta = now - li.dwTime;
                return TimeSpan.FromMilliseconds(delta);
            }
        }
    }

    /// <summary>Full-window opaque overlay shown while the address book is locked.</summary>
    internal sealed class VaultLock : Panel
    {
        private VaultConfig _cfg;
        private readonly Panel _card;
        private readonly TextBox _pwd;
        private readonly Label _err;
        private readonly Label _hint;

        public event EventHandler Unlocked;

        public VaultLock(VaultConfig cfg)
        {
            _cfg = cfg;
            BackColor = Ui.CardAlt;
            TabStop = true;

            _card = new Panel { Size = new Size(392, 268), Tag = "card", BackColor = Ui.Card };
            _card.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, _card.Width - 1, _card.Height - 1);
                using (SolidBrush b = new SolidBrush(Ui.Card)) Ui.FillRound(g, b, r, 4);
                using (Pen p = new Pen(Ui.Border)) Ui.DrawRound(g, p, r, 4);
                using (SolidBrush a = new SolidBrush(Ui.Accent)) Ui.FillRound(g, a, new Rectangle(24, 26, 40, 40), 11);
                Ui.DrawIcon(g, UiIcon.Shield, new Rectangle(33, 35, 22, 22), Color.White);
            };
            Ui.RoundRegion(_card, 4);
            Controls.Add(_card);

            Label title = new Label
            {
                Text = "台账已锁定",
                Location = new Point(78, 30),
                Size = new Size(280, 28),
                Font = Ui.NewF(13F, FontStyle.Bold),
                ForeColor = Ui.Text,
                BackColor = Color.Transparent,
                Tag = "title"
            };
            Label sub = new Label
            {
                Text = "输入主密码后继续使用 RemoteHub",
                Location = new Point(78, 58),
                Size = new Size(290, 22),
                Font = Ui.NewF(9F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            _card.Controls.Add(title);
            _card.Controls.Add(sub);

            Panel wrap = new Panel { Location = new Point(28, 104), Size = new Size(336, 34), Tag = "fieldwrap", BackColor = Ui.Field };
            wrap.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, wrap.Width - 1, wrap.Height - 1);
                using (SolidBrush b = new SolidBrush(Ui.Field)) Ui.FillRound(g, b, r, Ui.Radius);
                using (Pen p = new Pen(Ui.Border)) Ui.DrawRound(g, p, r, Ui.Radius);
            };
            Ui.RoundRegion(wrap, Ui.Radius);
            _pwd = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Ui.Field,
                ForeColor = Ui.Text,
                Font = Ui.NewF(10F),
                UseSystemPasswordChar = true,
                Location = new Point(12, 8),
                Size = new Size(312, 20),
                Tag = "field"
            };
            _pwd.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; TryUnlock(); }
            };
            wrap.Controls.Add(_pwd);
            _card.Controls.Add(wrap);

            RhButton ok = new RhButton
            {
                Text = "解锁",
                Icon = UiIcon.Key,
                Kind = RhButtonKind.Primary,
                Size = new Size(336, 34),
                Location = new Point(28, 148)
            };
            ok.Click += delegate { TryUnlock(); };
            _card.Controls.Add(ok);

            _err = new Label
            {
                Text = "",
                Location = new Point(28, 190),
                Size = new Size(336, 20),
                ForeColor = Ui.Danger,
                BackColor = Color.Transparent,
                Font = Ui.NewF(9F),
                Tag = "muted"
            };
            _hint = new Label
            {
                Text = "主密码只锁界面；忘记密码需删除配置目录中的 vault.ini。",
                Location = new Point(28, 218),
                Size = new Size(336, 36),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Font = Ui.NewF(8F),
                Tag = "muted"
            };
            _card.Controls.Add(_err);
            _card.Controls.Add(_hint);

            Resize += delegate { CenterCard(); };
        }

        private void CenterCard()
        {
            if (_card == null) return;
            _card.Location = new Point(
                Math.Max(0, (ClientSize.Width - _card.Width) / 2),
                Math.Max(0, (ClientSize.Height - _card.Height) / 2));
        }

        public void ApplyTheme()
        {
            BackColor = Ui.CardAlt;
            _card.BackColor = Ui.Card;
            foreach (Control c in _card.Controls)
            {
                if (c.Tag != null && c.Tag.ToString() == "fieldwrap") c.BackColor = Ui.Field;
                if (c is Label)
                {
                    if (c == _err) c.ForeColor = Ui.Danger;
                    else if (c.Tag != null && c.Tag.ToString() == "muted") c.ForeColor = Ui.TextMuted;
                    else c.ForeColor = Ui.Text;
                }
            }
            _pwd.BackColor = Ui.Field;
            _pwd.ForeColor = Ui.Text;
            Invalidate(true);
        }

        public void SetConfig(VaultConfig cfg) { _cfg = cfg; }

        public void ShowLock()
        {
            _pwd.Text = "";
            _err.Text = "";
            Visible = true;
            BringToFront();
            CenterCard();
            try { _pwd.Focus(); } catch { }
        }

        private void TryUnlock()
        {
            if (_cfg.Verify(_pwd.Text))
            {
                Visible = false;
                _pwd.Text = "";
                if (Unlocked != null) Unlocked(this, EventArgs.Empty);
            }
            else
            {
                _err.Text = "主密码不正确，请重试。";
                _pwd.SelectAll();
                _pwd.Focus();
            }
        }
    }

    /// <summary>Set / change / remove the master password and the idle timeout.</summary>
    internal sealed class VaultSettingsDialog : Form
    {
        private readonly VaultConfig _cfg;
        private readonly TextBox _p1 = new TextBox();
        private readonly TextBox _p2 = new TextBox();
        private readonly RhNumber _idle = new RhNumber();
        public bool Removed;

        public VaultSettingsDialog(VaultConfig cfg)
        {
            _cfg = cfg;
            Dlg.Init(this, 430, 366, "主密码与自动锁定");
            Controls.Add(Dlg.Header(this, 430, "台账主密码", "锁住界面，防止他人查看主机台账与已保存的密码", UiIcon.Shield));

            Panel card = Dlg.Card(16, 78, 398, 208, "安全设置", UiIcon.Key);
            _p1.UseSystemPasswordChar = true;
            _p2.UseSystemPasswordChar = true;
            Dlg.AddRow(card, 36, "新密码", Dlg.Wrap(_p1, 28), 270);
            Dlg.AddRow(card, 76, "确认密码", Dlg.Wrap(_p2, 28), 270);
            _idle.Minimum = 0;
            _idle.Maximum = 240;
            _idle.Value = cfg.IdleMinutes;
            Dlg.AddRow(card, 116, "空闲锁定", Dlg.Wrap(_idle, 28), 120);
            Label note = new Label
            {
                Text = "0 = 不自动锁定。" + (_cfg.Enabled ? "当前已设置主密码，留空表示不修改。" : "当前未设置主密码。"),
                Location = new Point(18, 158),
                Size = new Size(362, 40),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            card.Controls.Add(note);
            Controls.Add(card);

            Panel footer = Dlg.Footer(430);
            RhButton cancel = Dlg.Button("取消", UiIcon.Close, RhButtonKind.Ghost, 84);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Dlg.PlaceRight(footer, 430, cancel, 0);

            RhButton save = Dlg.Button("保存", UiIcon.Check, RhButtonKind.Primary, 84);
            save.Click += delegate
            {
                string a = _p1.Text, b = _p2.Text;
                if (a.Length > 0 || b.Length > 0)
                {
                    if (a.Length < 4) { MessageBox.Show("主密码至少 4 位。", "RemoteHub"); return; }
                    if (a != b) { MessageBox.Show("两次输入的密码不一致。", "RemoteHub"); return; }
                    _cfg.SetPassword(a);
                }
                else if (!_cfg.Enabled)
                {
                    MessageBox.Show("请填写新密码，或直接点「取消」。", "RemoteHub");
                    return;
                }
                _cfg.IdleMinutes = (int)_idle.Value;
                _cfg.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            Dlg.PlaceRight(footer, 430, save, 92);

            if (_cfg.Enabled)
            {
                RhButton remove = Dlg.Button("取消主密码", UiIcon.Trash, RhButtonKind.Ghost, 116);
                remove.Location = new Point(14, 10);
                remove.Click += delegate
                {
                    if (MessageBox.Show("取消主密码后，启动 RemoteHub 将不再要求输入密码。继续？",
                        "RemoteHub", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    _cfg.ClearPassword();
                    _cfg.Save();
                    Removed = true;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                footer.Controls.Add(remove);
            }
            Controls.Add(footer);
            Ui.ApplyTheme(this);
        }
    }
}
