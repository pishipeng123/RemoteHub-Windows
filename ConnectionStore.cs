using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace RemoteHub
{
    [Serializable]
    public class ConnectionInfo
    {
        public string Name = "";
        public string Group = "";
        public string Host = "";
        public int Port = 3389;
        public string UserName = "";
        public string Domain = "";
        public string Notes = "";
        public int ColorDepth = 32;
        public int DesktopWidth = 0;     // 0 = follow the tab size
        public int DesktopHeight = 0;
        // 0 = play on this computer (audio redirection), 1 = play on remote, 2 = do not play.
        public int AudioMode = 0;
        public bool SmartSizing = true;
        public bool CompatMode = false;  // old servers (2003/XP, no CredSSP)

        // ---- Local <-> remote file sharing (RDP device redirection) ----
        // Defaults are ON: without these the embedded session cannot see local disks,
        // which is the single most common "file sharing is inconvenient" complaint.
        public bool ShareDrives = true;         // map all local drives into \\tsclient
        public bool ShareClipboard = true;      // clipboard incl. file copy/paste

        [XmlElement("PasswordProtected")]
        public string PasswordProtected = "";

        [XmlIgnore]
        public string Password = "";     // plaintext, memory only

        public ConnectionInfo() { }

        public ConnectionInfo Clone()
        {
            return (ConnectionInfo)this.MemberwiseClone();
        }

        public string DisplayGroup
        {
            get { return string.IsNullOrEmpty(Group) ? "未分组" : Group; }
        }

        public string Address
        {
            get { return Port == 3389 ? Host : Host + ":" + Port; }
        }

        public string AudioText
        {
            get
            {
                switch (AudioMode)
                {
                    case 1: return "在远程计算机播放";
                    case 2: return "不播放（静音）";
                    default: return "在此计算机播放";
                }
            }
        }
    }

    [XmlRoot("RemoteHubConnections")]
    public class ConnectionStore
    {
        [XmlAttribute]
        public int Version = 1;

        [XmlArray("Connections")]
        [XmlArrayItem("Connection")]
        public List<ConnectionInfo> Items = new List<ConnectionInfo>();

        /// <summary>Local folder that receives files pasted back from a remote session.</summary>
        [XmlElement]
        public string TransferFolder = "";

        [XmlIgnore]
        public string LastError = "";

        public static string DefaultTransferFolder
        {
            get
            {
                try
                {
                    string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(profile))
                        return Path.Combine(profile, "Downloads", "RemoteHub");
                }
                catch { }
                return Path.Combine(Path.GetTempPath(), "RemoteHub-Transfer");
            }
        }

        public string EffectiveTransferFolder
        {
            get { return string.IsNullOrEmpty(TransferFolder) ? DefaultTransferFolder : TransferFolder; }
        }

        /// <summary>
        /// Portable mode is activated by a "portable.flag" file next to the exe.
        /// In that case the connection book lives beside the program, not in %APPDATA%.
        /// </summary>
        public static bool IsPortable
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "portable.flag"));
                }
                catch { return false; }
            }
        }

        public static string ModeText
        {
            get
            {
                return IsPortable
                    ? "绿色版（台账保存在程序目录 Data\\connections.xml）"
                    : "安装版（台账保存在 %APPDATA%\\RemoteHub\\connections.xml）";
            }
        }

        public static string DirectoryPath
        {
            get
            {
                if (IsPortable)
                    return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "RemoteHub");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "connections.xml"); }
        }

        public static ConnectionStore Load()
        {
            ConnectionStore store = new ConnectionStore();
            try
            {
                if (File.Exists(FilePath))
                {
                    XmlSerializer ser = new XmlSerializer(typeof(ConnectionStore));
                    using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        store = (ConnectionStore)ser.Deserialize(fs);
                    }
                    if (store.Items == null) store.Items = new List<ConnectionInfo>();
                    foreach (ConnectionInfo c in store.Items)
                        c.Password = Crypto.Unprotect(c.PasswordProtected);
                }
            }
            catch (Exception ex)
            {
                store = new ConnectionStore();
                store.LastError = "读取配置失败：" + ex.Message;
                try
                {
                    if (File.Exists(FilePath))
                        File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { }
            }
            return store;
        }

        public void Save()
        {
            foreach (ConnectionInfo c in Items)
                c.PasswordProtected = Crypto.Protect(c.Password);

            Directory.CreateDirectory(DirectoryPath);
            string tmp = FilePath + ".tmp";
            XmlSerializer ser = new XmlSerializer(typeof(ConnectionStore));
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ser.Serialize(fs, this);
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public List<string> Groups()
        {
            List<string> result = new List<string>();
            foreach (ConnectionInfo c in Items)
            {
                string g = c.DisplayGroup;
                if (!result.Contains(g)) result.Add(g);
            }
            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        public bool IsDuplicate(ConnectionInfo candidate, ConnectionInfo except)
        {
            foreach (ConnectionInfo c in Items)
            {
                if (object.ReferenceEquals(c, except)) continue;
                if (string.Equals(c.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) &&
                    c.Port == candidate.Port &&
                    string.Equals(c.UserName, candidate.UserName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Loose CSV import. Supported column layouts (per line):
        ///   host
        ///   host,user
        ///   host,user,password
        ///   name,group,host,port,user,password,domain,notes   (header line is skipped)
        /// </summary>
        public int ImportText(string text, out List<string> problems)
        {
            problems = new List<string>();
            int added = 0;
            int lineNo = 0;
            using (StringReader sr = new StringReader(text ?? ""))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (lineNo == 1 && (line.IndexOf("主机") >= 0 || line.IndexOf("地址") >= 0 ||
                        line.ToLowerInvariant().StartsWith("host") || line.StartsWith("名称")))
                        continue;   // header

                    List<string> cols = SplitCsvLine(line);
                    if (cols.Count == 0 || cols[0].Trim().Length == 0)
                    {
                        problems.Add("第 " + lineNo + " 行：主机为空，已跳过");
                        continue;
                    }

                    ConnectionInfo c = new ConnectionInfo();
                    if (cols.Count <= 3)
                    {
                        string addr = cols[0].Trim();
                        ParseHostPort(addr, c);
                        c.Name = c.Host;
                        c.Group = "导入";
                        if (cols.Count >= 2) c.UserName = cols[1].Trim();
                        if (cols.Count >= 3) c.Password = cols[2];
                    }
                    else
                    {
                        c.Name = cols[0].Trim();
                        c.Group = cols[1].Trim();
                        ParseHostPort(cols[2].Trim(), c);
                        int port;
                        if (cols.Count > 3 && int.TryParse(cols[3].Trim(), out port) && port > 0)
                            c.Port = port;
                        if (cols.Count > 4) c.UserName = cols[4].Trim();
                        if (cols.Count > 5) c.Password = cols[5];
                        if (cols.Count > 6) c.Domain = cols[6].Trim();
                        if (cols.Count > 7) c.Notes = cols[7].Trim();
                        if (cols.Count > 8) c.ShareDrives = CsvBool(cols[8]);
                        if (cols.Count > 9) c.ShareClipboard = CsvBool(cols[9]);
                        if (cols.Count > 10)
                        {
                            int am;
                            if (int.TryParse(cols[10].Trim(), out am) && am >= 0 && am <= 2)
                                c.AudioMode = am;
                        }
                    }
                    if (string.IsNullOrEmpty(c.Name)) c.Name = c.Host;
                    if (IsDuplicate(c, null))
                    {
                        problems.Add("第 " + lineNo + " 行：" + c.Address + " 已存在，已跳过");
                        continue;
                    }
                    Items.Add(c);
                    added++;
                }
            }
            return added;
        }

        private static void ParseHostPort(string addr, ConnectionInfo c)
        {
            int colon = addr.LastIndexOf(':');
            if (colon > 0 && colon < addr.Length - 1)
            {
                int port;
                if (int.TryParse(addr.Substring(colon + 1), out port) && port > 0 && port < 65536)
                {
                    c.Host = addr.Substring(0, colon);
                    c.Port = port;
                    return;
                }
            }
            c.Host = addr;
            c.Port = 3389;
        }

        private static List<string> SplitCsvLine(string line)
        {
            List<string> cols = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (ch == ',' && !quoted)
                {
                    cols.Add(cur.ToString());
                    cur.Length = 0;
                }
                else cur.Append(ch);
            }
            cols.Add(cur.ToString());
            return cols;
        }

        public string ToCsv(bool includePassword)
        {
            // Fixed column layout (password column is always present, empty when not
            // exported) so that a re-import never shifts domain/notes into the wrong slot.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("名称,分组,主机,端口,用户名,密码,域,备注,共享磁盘,共享剪贴板,音频模式");
            foreach (ConnectionInfo c in Items)
            {
                sb.Append(Csv(c.Name)).Append(',');
                sb.Append(Csv(c.Group)).Append(',');
                sb.Append(Csv(c.Host)).Append(',');
                sb.Append(c.Port).Append(',');
                sb.Append(Csv(c.UserName)).Append(',');
                sb.Append(includePassword ? Csv(c.Password) : "").Append(',');
                sb.Append(Csv(c.Domain)).Append(',');
                sb.Append(Csv(c.Notes)).Append(',');
                sb.Append(c.ShareDrives ? "1" : "0").Append(',');
                sb.Append(c.ShareClipboard ? "1" : "0").Append(',');
                sb.AppendLine(c.AudioMode.ToString());
            }
            return sb.ToString();
        }

        private static bool CsvBool(string s)
        {
            string v = (s ?? "").Trim();
            return !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     v == "否" || v == "关" || v == "off");
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    /// <summary>DPAPI, bound to the current Windows account. Nothing leaves this machine.</summary>
    internal static class Crypto
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteHub/v1/local-only/v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy,
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(data);
            }
            catch { return ""; }
        }

        public static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }
    }
}
