using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace RemoteHub
{
    /// <summary>One completed (or failed) remote session.</summary>
    [Serializable]
    public class SessionRecord
    {
        public string Name = "";
        public string Address = "";
        public string UserName = "";
        public DateTime Started;
        public DateTime Ended;
        public int Seconds;
        public string Outcome = "";
        public string Detail = "";

        public string DurationText
        {
            get
            {
                int s = Seconds < 0 ? 0 : Seconds;
                if (s < 60) return s + " 秒";
                if (s < 3600) return (s / 60) + " 分 " + (s % 60) + " 秒";
                return (s / 3600) + " 时 " + ((s % 3600) / 60) + " 分";
            }
        }

        public string RowText
        {
            get
            {
                return Started.ToString("MM-dd HH:mm") + "   " +
                       (string.IsNullOrEmpty(Name) ? Address : Name + " (" + Address + ")") +
                       "   " + DurationText + "   " + Outcome +
                       (string.IsNullOrEmpty(Detail) ? "" : "   " + Detail);
            }
        }
    }

    [XmlRoot("RemoteHubHistory")]
    public class HistoryBook
    {
        [XmlArray("Records")]
        [XmlArrayItem("Record")]
        public List<SessionRecord> Items = new List<SessionRecord>();
    }

    /// <summary>
    /// Append-only session log stored next to the address book (history.xml).
    /// Deliberately separate from connections.xml so a corrupt history can never
    /// take down the host book, and so clearing it is a safe, isolated operation.
    /// </summary>
    internal static class SessionHistory
    {
        private const int MaxRecords = 500;

        public static string FilePath
        {
            get { return Path.Combine(ConnectionStore.DirectoryPath, "history.xml"); }
        }

        public static HistoryBook Load()
        {
            HistoryBook book = new HistoryBook();
            try
            {
                if (File.Exists(FilePath))
                {
                    XmlSerializer ser = new XmlSerializer(typeof(HistoryBook));
                    using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        book = (HistoryBook)ser.Deserialize(fs);
                    if (book.Items == null) book.Items = new List<SessionRecord>();
                }
            }
            catch { book = new HistoryBook(); }
            return book;
        }

        private static void Save(HistoryBook book)
        {
            Directory.CreateDirectory(ConnectionStore.DirectoryPath);
            string tmp = FilePath + ".tmp";
            XmlSerializer ser = new XmlSerializer(typeof(HistoryBook));
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                ser.Serialize(fs, book);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public static void Append(SessionRecord r)
        {
            if (r == null) return;
            try
            {
                HistoryBook book = Load();
                book.Items.Add(r);
                while (book.Items.Count > MaxRecords) book.Items.RemoveAt(0);
                Save(book);
            }
            catch (Exception ex) { Program.Log("HISTORY", ex); }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception ex) { Program.Log("HISTORY-CLEAR", ex); }
        }

        public static string ToCsv(HistoryBook book)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("开始时间,结束时间,名称,地址,用户名,时长(秒),结果,说明");
            if (book != null)
                foreach (SessionRecord r in book.Items)
                {
                    sb.Append(Csv(r.Started.ToString("s"))).Append(',');
                    sb.Append(Csv(r.Ended.ToString("s"))).Append(',');
                    sb.Append(Csv(r.Name)).Append(',');
                    sb.Append(Csv(r.Address)).Append(',');
                    sb.Append(Csv(r.UserName)).Append(',');
                    sb.Append(r.Seconds).Append(',');
                    sb.Append(Csv(r.Outcome)).Append(',');
                    sb.AppendLine(Csv(r.Detail));
                }
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>
        /// Only the four common extended disconnect codes are described. mstscax does not
        /// reliably expose the rest through IDispatch, so unknown codes are shown verbatim
        /// rather than guessed at.
        /// </summary>
        public static string DescribeReason(int code)
        {
            switch (code)
            {
                case -1: return "控件未提供原因码";
                case 0: return "无附加信息";
                case 1: return "本地正常断开";
                case 2: return "远端用户注销";
                case 3: return "服务器断开会话";
                default: return "原因码 " + code + "（控件未提供文字描述）";
            }
        }

        public static string Probe()
        {
            try
            {
                HistoryBook book = Load();
                XmlSerializer ser = new XmlSerializer(typeof(HistoryBook));
                using (MemoryStream ms = new MemoryStream())
                {
                    ser.Serialize(ms, book);
                    ms.Position = 0;
                    HistoryBook back = (HistoryBook)ser.Deserialize(ms);
                    return "OK（XML 往返 " + back.Items.Count + " 条，上限 " + MaxRecords +
                           " 条，文件 " + FilePath + "）";
                }
            }
            catch (Exception ex) { return "失败：" + ex.Message; }
        }
    }

    /// <summary>Session log viewer. Uses an owner-drawn list so no system square frame appears.</summary>
    internal sealed class SessionHistoryDialog : Form
    {
        private readonly ListBox _list = new ListBox();
        private readonly Label _v1 = new Label();
        private readonly Label _v2 = new Label();
        private readonly Label _v3 = new Label();
        private readonly Label _v4 = new Label();
        private HistoryBook _book = new HistoryBook();

        public SessionHistoryDialog()
        {
            const int W = 760, H = 566;
            Dlg.Init(this, W, H, "连接历史");
            Panel header = Dlg.Header(this, W, "连接历史",
                "记录每次会话的开始、结束与断开方式，最多保留 500 条", UiIcon.Grid);

            Panel content = new Panel { Dock = DockStyle.Fill, Tag = "surface", BackColor = Ui.Surface };

            Panel stat = Dlg.Card(14, 10, W - 28, 84, "概览", UiIcon.Grid);
            string[] caps = { "会话记录", "正常结束", "异常 / 失败", "累计在线" };
            Label[] vals = { _v1, _v2, _v3, _v4 };
            for (int i = 0; i < 4; i++)
            {
                int x = 18 + i * 178;
                Label v = vals[i];
                v.Text = "0";
                v.Location = new Point(x, 32);
                v.Size = new Size(170, 26);
                v.Font = Ui.NewF(15F, FontStyle.Bold);
                v.ForeColor = Ui.Text;
                v.BackColor = Color.Transparent;
                v.Tag = "title";
                Label c = new Label
                {
                    Text = caps[i],
                    Location = new Point(x, 59),
                    Size = new Size(170, 18),
                    Font = Ui.NewF(8.5F),
                    ForeColor = Ui.TextMuted,
                    BackColor = Color.Transparent,
                    Tag = "muted"
                };
                stat.Controls.Add(v);
                stat.Controls.Add(c);
            }

            Panel card = Dlg.Card(14, 102, W - 28, 336, "会话记录（最新在最下方）", UiIcon.Monitor);
            _list.SetBounds(12, 34, W - 52, 290);
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Ui.Field;
            _list.ForeColor = Ui.Text;
            _list.Font = Ui.NewF(9F);
            _list.IntegralHeight = false;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 24;
            _list.Tag = "field";
            _list.DrawItem += List_DrawItem;
            card.Controls.Add(_list);

            content.Controls.Add(stat);
            content.Controls.Add(card);

            Panel footer = Dlg.Footer(W);
            RhButton export = Dlg.Button("导出 CSV", UiIcon.Export, RhButtonKind.Ghost, 116);
            export.Location = new Point(14, 10);
            export.Click += ExportCsv;
            RhButton clear = Dlg.Button("清空记录", UiIcon.Trash, RhButtonKind.Ghost, 106);
            clear.Location = new Point(138, 10);
            clear.Click += ClearHistory;
            Label tip = new Label
            {
                Text = "断线原因码由 RDP 控件提供，部分系统只给出数字。",
                Location = new Point(258, 17),
                Size = new Size(340, 20),
                Font = Ui.NewF(8.5F),
                ForeColor = Ui.TextMuted,
                BackColor = Color.Transparent,
                Tag = "muted"
            };
            RhButton close = Dlg.Button("关闭", UiIcon.Check, RhButtonKind.Primary, 96);
            close.DialogResult = DialogResult.Cancel;
            footer.Controls.AddRange(new Control[] { export, clear, tip });
            Dlg.PlaceRight(footer, W, close, 0);

            this.Controls.Add(content);
            this.Controls.Add(footer);
            this.Controls.Add(header);
            this.CancelButton = close;
            Ui.ApplyTheme(this);
            Reload();
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(sel ? Ui.CardAlt : Ui.Field))
                g.FillRectangle(b, e.Bounds);
            SessionRecord r = _list.Items[e.Index] as SessionRecord;
            if (r != null)
            {
                using (SolidBrush t = new SolidBrush(sel ? Ui.Accent : Ui.Text))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(r.RowText, Ui.F(9F), t,
                        new RectangleF(e.Bounds.Left + 8, e.Bounds.Top,
                            Math.Max(10, e.Bounds.Width - 16), e.Bounds.Height), sf);
                }
            }
            using (Pen p = new Pen(Ui.BorderSoft))
                g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        private void Reload()
        {
            _book = SessionHistory.Load();
            Render();
        }

        private void Render()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (SessionRecord r in _book.Items) _list.Items.Add(r);
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.TopIndex = _list.Items.Count - 1;

            int total = _book.Items.Count, normal = 0, bad = 0;
            long seconds = 0;
            foreach (SessionRecord r in _book.Items)
            {
                seconds += r.Seconds;
                if (r.Outcome == "主动断开" || r.Outcome == "关闭标签" || r.Outcome == "重连")
                    normal++;
                else bad++;
            }
            _v1.Text = total.ToString();
            _v2.Text = normal.ToString();
            _v3.Text = bad.ToString();
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            _v4.Text = ts.TotalHours >= 1
                ? ((int)ts.TotalHours) + " 时 " + ts.Minutes + " 分"
                : ts.Minutes + " 分";
        }

        private void ExportCsv(object sender, EventArgs e)
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV 文件|*.csv";
                d.FileName = "RemoteHub-连接历史-" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, SessionHistory.ToCsv(_book), new UTF8Encoding(true));
                    MessageBox.Show("已导出 " + _book.Items.Count + " 条记录。", "RemoteHub",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
            }
        }

        private void ClearHistory(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定清空全部连接历史吗？此操作不可撤销。", "RemoteHub",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SessionHistory.Clear();
            Reload();
        }

        /// <summary>Preview-only sample rows for the --shothistory screenshot mode.</summary>
        internal void UseSamples()
        {
            DateTime now = DateTime.Now;
            _book = new HistoryBook();
            _book.Items.Add(new SessionRecord
            {
                Name = "Web-01", Address = "10.0.1.5", UserName = "admin",
                Started = now.AddHours(-5).AddMinutes(-12), Ended = now.AddHours(-5),
                Seconds = 720, Outcome = "主动断开", Detail = "本地正常断开"
            });
            _book.Items.Add(new SessionRecord
            {
                Name = "DB-02", Address = "10.0.1.8:3390", UserName = "sa",
                Started = now.AddHours(-4), Ended = now.AddHours(-3).AddMinutes(-41),
                Seconds = 1140, Outcome = "意外断开", Detail = "服务器断开会话"
            });
            _book.Items.Add(new SessionRecord
            {
                Name = "Test-VM", Address = "192.168.1.30", UserName = "tester",
                Started = now.AddMinutes(-96), Ended = now.AddMinutes(-96).AddSeconds(3),
                Seconds = 3, Outcome = "连接失败", Detail = "Connect 调用失败"
            });
            _book.Items.Add(new SessionRecord
            {
                Name = "Office-PC", Address = "172.16.4.22", UserName = "user",
                Started = now.AddMinutes(-48), Ended = now.AddMinutes(-21),
                Seconds = 1620, Outcome = "关闭标签", Detail = "本地正常断开"
            });
            _book.Items.Add(new SessionRecord
            {
                Name = "Build-Srv", Address = "10.0.2.11", UserName = "ci",
                Started = now.AddMinutes(-14), Ended = now,
                Seconds = 840, Outcome = "重连", Detail = "本地正常断开"
            });
            Render();
        }
    }
}
