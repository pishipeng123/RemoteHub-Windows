using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// Minimal IDispatch helper. The RDP ActiveX control is used through late binding so
    /// no aximp-generated interop assemblies and no Windows SDK are required.
    /// </summary>
    internal static class ComInvoke
    {
        private const BindingFlags Base = BindingFlags.Public | BindingFlags.Instance;

        public static bool TryGet(object target, string name, out object value)
        {
            value = null;
            if (target == null) return false;
            try
            {
                value = target.GetType().InvokeMember(name, Base | BindingFlags.GetProperty, null, target, null);
                return true;
            }
            catch { return false; }
        }

        public static bool TrySet(object target, string name, object value)
        {
            if (target == null) return false;
            try
            {
                target.GetType().InvokeMember(name, Base | BindingFlags.SetProperty,
                    null, target, new object[] { value });
                return true;
            }
            catch { return false; }
        }

        public static bool TryCall(object target, string name, params object[] args)
        {
            if (target == null) return false;
            try
            {
                Call(target, name, args);
                return true;
            }
            catch { return false; }
        }

        public static object Call(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, Base | BindingFlags.InvokeMethod,
                null, target, args ?? new object[0]);
        }

        /// <summary>
        /// Writes a property across every AdvancedSettingsN interface and finally the
        /// control itself. RDP spreads its redirection switches over several interface
        /// levels (RedirectDrives on the base one, RedirectClipboard on v5+), and the
        /// highest version returned by GetAdvanced is not always the one that accepts a
        /// given DISPID. Trying them all keeps us independent of the Windows build.
        /// </summary>
        public static bool TrySetAny(object ocx, object preferredAdv, string name,
                                     object value, out string where)
        {
            where = null;
            if (ocx == null) return false;
            if (preferredAdv != null && TrySet(preferredAdv, name, value))
            {
                where = "AdvancedSettings(优选)";
                return true;
            }
            for (int n = 12; n >= 2; n--)
            {
                object adv;
                if (TryGet(ocx, "AdvancedSettings" + n, out adv) && adv != null &&
                    TrySet(adv, name, value))
                {
                    where = "AdvancedSettings" + n;
                    return true;
                }
            }
            object baseAdv;
            if (TryGet(ocx, "AdvancedSettings", out baseAdv) && baseAdv != null &&
                TrySet(baseAdv, name, value))
            {
                where = "AdvancedSettings";
                return true;
            }
            if (TrySet(ocx, name, value)) { where = "控件主接口"; return true; }
            return false;
        }

        public static bool TryGetAny(object ocx, object preferredAdv, string name,
                                     out object value, out string where)
        {
            value = null;
            where = null;
            if (ocx == null) return false;
            if (preferredAdv != null && TryGet(preferredAdv, name, out value) && value != null)
            {
                where = "AdvancedSettings(优选)";
                return true;
            }
            for (int n = 12; n >= 2; n--)
            {
                object adv;
                if (TryGet(ocx, "AdvancedSettings" + n, out adv) && adv != null &&
                    TryGet(adv, name, out value) && value != null)
                {
                    where = "AdvancedSettings" + n;
                    return true;
                }
            }
            object baseAdv;
            if (TryGet(ocx, "AdvancedSettings", out baseAdv) && baseAdv != null &&
                TryGet(baseAdv, name, out value) && value != null)
            {
                where = "AdvancedSettings";
                return true;
            }
            if (TryGet(ocx, name, out value) && value != null)
            {
                where = "控件主接口";
                return true;
            }
            value = null;
            return false;
        }

        public static object GetAdvanced(object ocx)
        {
            object adv;
            for (int n = 12; n >= 2; n--)
            {
                if (TryGet(ocx, "AdvancedSettings" + n, out adv) && adv != null) return adv;
            }
            if (TryGet(ocx, "AdvancedSettings", out adv)) return adv;
            return null;
        }
    }

    /// <summary>
    /// Hosts the system RDP ActiveX control (mstscax.dll) inside our own window.
    /// This is an in-process embedded control, NOT a launched mstsc.exe window.
    /// </summary>
    internal sealed class RdpHost : AxHost
    {
        // Version probes are ordered from newest to oldest. Different Windows builds
        // register the control under different ProgID families, so all of them are tried.
        public static readonly string[] Candidates = new string[]
        {
            "MsRdpClient12NotSafeForScripting",
            "MsRdpClient11NotSafeForScripting",
            "MsRdpClient10NotSafeForScripting",
            "MsRdpClient9NotSafeForScripting",
            "MsRdpClient8NotSafeForScripting",
            "MsRdpClient7NotSafeForScripting",
            "MsRdpClient6NotSafeForScripting",
            "MsRdpClient5NotSafeForScripting",
            "MsRdpClient2NotSafeForScripting",
            "MsRdpClientNotSafeForScripting",
            "MsTscAx.MsTscAx.12",
            "MsTscAx.MsTscAx.11",
            "MsTscAx.MsTscAx.10",
            "MsTscAx.MsTscAx.9",
            "MsTscAx.MsTscAx.8",
            "MsTscAx.MsTscAx.7",
            "MsTscAx.MsTscAx.6",
            "MsTscAx.MsTscAx.5",
            "MsTscAx.MsTscAx.1",
            "MsRDP.MsRDP.11",
            "MsRDP.MsRDP.10",
            "MsRDP.MsRDP.9",
            "MsRDP.MsRDP.8",
            "MsRDP.MsRDP.7",
            "MsRDP.MsRDP.6",
            "MsRDP.MsRDP.5",
            "MsRDP.MsRDP.2"
        };

        private static string s_clsid;
        private static string s_progId;
        private static string s_preferred;
        private static bool s_probed;

        private object _ocx;
        private object _adv;
        private bool _everConnected;
        private bool _fullScreen;
        private int _appliedW;
        private int _appliedH;

        public static string CurrentProgId { get { return s_progId ?? "(未解析)"; } }
        public bool EverConnected { get { return _everConnected; } }
        public bool FullScreen { get { return _fullScreen; } }
        public string LastError { get; private set; }
        public bool PasswordInjected { get; private set; }
        public ConnectionInfo Info { get; private set; }
        /// <summary>Human-readable result of the redirection switches for the last ApplySettings.</summary>
        public string RedirectionReport { get; private set; }
        /// <summary>Result of the audio redirection switch for the last ApplySettings.</summary>
        public string AudioReport { get; private set; }

        public RdpHost() : base(ResolveClsid())
        {
            this.Dock = DockStyle.Fill;
            this.TabStop = false;
        }

        private static string ResolveClsid()
        {
            if (s_clsid != null) return s_clsid;

            string chosen = PickProgId();
            List<string> order = new List<string>();
            if (!string.IsNullOrEmpty(chosen)) order.Add(chosen);
            foreach (string p in Candidates) if (!order.Contains(p)) order.Add(p);

            foreach (string p in order)
            {
                try
                {
                    Type t = Type.GetTypeFromProgID(p, false);
                    if (t == null || t.GUID == Guid.Empty) continue;
                    s_clsid = t.GUID.ToString("D");
                    s_progId = p;
                    return s_clsid;
                }
                catch { }
            }
            throw new InvalidOperationException(
                "未找到系统自带的远程桌面 ActiveX 控件 (mstscax.dll)。请确认系统已启用「远程桌面客户端」功能。");
        }

        /// <summary>
        /// Picks the newest registered control that can actually accept a password through
        /// the scriptable AdvancedSettings interface. Fallback: any creatable control.
        /// </summary>
        public static string PickProgId()
        {
            if (s_probed) return s_preferred;
            s_probed = true;

            string firstWorkable = null;
            foreach (string p in Candidates)
            {
                Type t;
                try { t = Type.GetTypeFromProgID(p, false); }
                catch { continue; }
                if (t == null || t.GUID == Guid.Empty) continue;

                object o = null;
                try
                {
                    o = Activator.CreateInstance(t);
                    if (o == null) continue;
                    bool serverOk = ComInvoke.TrySet(o, "Server", "127.0.0.1");
                    object adv = ComInvoke.GetAdvanced(o);
                    bool pwdOk = adv != null && ComInvoke.TrySet(adv, "ClearTextPassword", "probe");
                    if (serverOk || adv != null)
                    {
                        if (firstWorkable == null) firstWorkable = p;
                        if (pwdOk) { s_preferred = p; return s_preferred; }
                    }
                }
                catch { }
                finally
                {
                    if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
                }
            }
            s_preferred = firstWorkable;
            return s_preferred;
        }

        /// <summary>Creates the ActiveX instance and returns the raw IDispatch object.</summary>
        public object EnsureOcx()
        {
            if (_ocx != null) return _ocx;

            try { ((ISupportInitialize)this).BeginInit(); } catch { }
            try { this.CreateControl(); } catch { }
            try { ((ISupportInitialize)this).EndInit(); } catch { }
            try { this.CreateControl(); } catch { }

            try { _ocx = GetOcx(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法创建远程桌面控件实例 (" + CurrentProgId + ")：" + ex.Message, ex);
            }
            if (_ocx == null)
                throw new InvalidOperationException("远程桌面控件实例为空 (" + CurrentProgId + ")。");

            _adv = ComInvoke.GetAdvanced(_ocx);
            return _ocx;
        }

        public bool ApplySettings(ConnectionInfo c, int width, int height)
        {
            Info = c;
            LastError = null;
            object ocx = EnsureOcx();

            bool portOk = _adv != null && ComInvoke.TrySet(_adv, "RDPPort", c.Port);
            string server = c.Host ?? "";
            if (!portOk && c.Port != 3389 && server.Length > 0)
                server = server + ":" + c.Port;   // fallback for controls without RDPPort

            bool ok = ComInvoke.TrySet(ocx, "Server", server);
            ComInvoke.TrySet(ocx, "UserName", c.UserName ?? "");
            if (!string.IsNullOrEmpty(c.Domain))
                ComInvoke.TrySet(ocx, "Domain", c.Domain);
            ComInvoke.TrySet(ocx, "ColorDepth", c.ColorDepth);

            if (width > 0 && height > 0)
            {
                width = Normalize(width, 640);
                height = Normalize(height, 480);
                ComInvoke.TrySet(ocx, "DesktopWidth", width);
                ComInvoke.TrySet(ocx, "DesktopHeight", height);
                _appliedW = width;
                _appliedH = height;
            }

            if (_adv != null)
            {
                // In follow-the-window mode the control must never letterbox: scaling is
                // only a safety net for servers that cannot change resolution live.
                ComInvoke.TrySet(_adv, "SmartSizing", c.SmartSizing || Ui.FitToWindow);
                ComInvoke.TrySet(_adv, "EnableCredSspSupport", !c.CompatMode);
                ComInvoke.TrySet(_adv, "EnableAutoReconnect", true);
                if (c.CompatMode)
                {
                    ComInvoke.TrySet(_adv, "AuthenticationLevel", 0);
                    ComInvoke.TrySet(_adv, "EnableCredSspSupport", false);
                }
            }

            ApplyRedirection(ocx, c);

            if (!string.IsNullOrEmpty(c.Password))
            {
                bool injected = (_adv != null && ComInvoke.TrySet(_adv, "ClearTextPassword", c.Password))
                             || ComInvoke.TrySet(ocx, "ClearTextPassword", c.Password);
                PasswordInjected = injected;
                if (!injected)
                    LastError = "当前控件不允许脚本注入密码，连接时会弹出系统凭据窗口，请手动输入密码。";
            }
            return ok;
        }

        /// <summary>
        /// Enables the RDP device-redirection channels that make local files reachable
        /// from inside the session: \\tsclient\C drives, clipboard file copy/paste and
        /// USB drives plugged in while connected. Each switch is attempted on every
        /// AdvancedSettingsN version because the accepted DISPID varies per build.
        /// </summary>
        private void ApplyRedirection(object ocx, ConnectionInfo c)
        {
            RedirectionReport =
                SetFlag(ocx, "磁盘映射", "RedirectDrives", c.ShareDrives) + " / " +
                SetFlag(ocx, "剪贴板", "RedirectClipboard", c.ShareClipboard) + " / " +
                ApplyAudio(ocx, c);

            // Best effort only. These live on IMsRdpClientNonScriptable3 (vtable-only)
            // and are not exposed through IDispatch on most Windows builds, so a failure
            // is normal and must never be reported as "enabled".
            string where;
            ComInvoke.TrySetAny(ocx, _adv, "RedirectDynamicDrives", true, out where);
            ComInvoke.TrySetAny(ocx, _adv, "ShowRedirectionWarningDialog", false, out where);
            ComInvoke.TrySetAny(ocx, _adv, "WarnAboutClipboardRedirection", false, out where);
        }

        private string SetFlag(object ocx, string label, string name, bool on)
        {
            string where;
            bool ok = ComInvoke.TrySetAny(ocx, _adv, name, on, out where);
            return label + "=" + (ok ? (on ? "开" : "关") : "不支持");
        }

        /// <summary>
        /// AudioRedirectionMode (AdvancedSettings6+): 0 = play here, 1 = play on server,
        /// 2 = do not play. Old controls only expose the boolean RedirectSound, which is
        /// used as a degraded fallback (on = play here, off = silent / play on server).
        /// </summary>
        private string ApplyAudio(object ocx, ConnectionInfo c)
        {
            string where;
            bool modern = ComInvoke.TrySetAny(ocx, _adv, "AudioRedirectionMode", c.AudioMode, out where);
            bool legacy = false;
            if (!modern)
                legacy = ComInvoke.TrySetAny(ocx, _adv, "RedirectSound", c.AudioMode == 0, out where);

            object back;
            string readWhere;
            bool read = ComInvoke.TryGetAny(ocx, _adv, "AudioRedirectionMode", out back, out readWhere);
            if (modern)
                AudioReport = c.AudioText + (read ? "(回读 " + back + ")" : "");
            else if (legacy)
                AudioReport = (c.AudioMode == 0 ? "本机播放" : "不重定向") + "（旧接口）";
            else
                AudioReport = "不支持";
            return "音频=" + AudioReport;
        }

        /// <summary>
        /// DISP_E_MEMBERNOTFOUND (0x80020003) and DISP_E_UNKNOWNNAME (0x80020006) both mean
        /// the member is not in the control's IDispatch type information. The vtable-only
        /// "NonScriptable" interfaces are not reachable this way on current builds.
        /// </summary>
        private static bool IsMemberMissing(COMException ce)
        {
            if (ce == null) return false;
            uint code = (uint)ce.ErrorCode;
            return code == 0x80020003u || code == 0x80020006u;
        }

        /// <summary>
        /// Sends the secure attention sequence (Ctrl+Alt+Del) to the remote session.
        /// Returns null on success; otherwise a human-readable reason. Some control builds
        /// only expose this on a vtable-only interface, in which case the standard RDP
        /// shortcut Ctrl+Alt+End is the reliable alternative.
        /// </summary>
        public string SendCtrlAltDel()
        {
            if (_ocx == null || IsDisposed) return "没有可用的会话";
            if (ConnectedState != 1) return "会话尚未连接";
            try
            {
                ComInvoke.Call(_ocx, "SendCtrlAltDel");
                return null;
            }
            catch (Exception ex)
            {
                Exception inner = (ex is TargetInvocationException && ex.InnerException != null)
                    ? ex.InnerException : ex;
                if (IsMemberMissing(inner as COMException))
                    return "本机 Windows 版本未通过脚本接口暴露该功能，请改用 Ctrl+Alt+End";
                return "发送失败：" + inner.Message + "，可改用 Ctrl+Alt+End";
            }
        }

        /// <summary>
        /// Best effort read of the extended disconnect reason. -1 means the control did not
        /// expose it through IDispatch (common on older builds); the numeric code is kept
        /// verbatim in that case rather than guessed.
        /// </summary>
        public int DisconnectReasonCode
        {
            get
            {
                if (_ocx == null || IsDisposed) return -1;
                object v;
                string where;
                if (ComInvoke.TryGetAny(_ocx, _adv, "ExtendedDisconnectReason", out v, out where) && v != null)
                {
                    try { return Convert.ToInt32(v); } catch { }
                }
                try
                {
                    object r = ComInvoke.Call(_ocx, "GetExtendedDisconnectReason");
                    if (r != null) return Convert.ToInt32(r);
                }
                catch { }
                return -1;
            }
        }

        public void Connect()
        {
            object ocx = EnsureOcx();
            try
            {
                ComInvoke.Call(ocx, "Connect");
            }
            catch (Exception ex)
            {
                Exception inner = (ex is TargetInvocationException && ex.InnerException != null)
                    ? ex.InnerException : ex;
                throw new InvalidOperationException("Connect 调用失败：" + inner.Message, inner);
            }
            _everConnected = false;
        }

        /// <summary>RDP accepts 2-pixel steps; keep a sane minimum for old servers.</summary>
        private static int Normalize(int value, int min)
        {
            value = Math.Max(min, value);
            return (value / 2) * 2;
        }

        /// <summary>Client DPI as a 100-based percentage (Windows 10/11 display scaling).</summary>
        private static int DeviceScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    int scale = (int)Math.Round(g.DpiX / 96.0 * 100.0);
                    if (scale < 100) scale = 100;
                    if (scale > 500) scale = 500;
                    return scale;
                }
            }
            catch { return 100; }
        }

        /// <summary>
        /// Makes the remote desktop match the area the control occupies. While connected this
        /// uses IMsRdpClient10::UpdateSessionDisplaySettings (RDP 8.1+), so the session is
        /// re-laid out live without dropping the connection. Returns true when it took effect.
        /// </summary>
        public bool ResizeDesktop(int width, int height)
        {
            if (_ocx == null || IsDisposed) return false;
            width = Normalize(width, 640);
            height = Normalize(height, 480);
            if (width == _appliedW && height == _appliedH) return false;

            int state = ConnectedState;
            bool live = state == 1;
            if (live && TryLiveResize(width, height))
            {
                _appliedW = width;
                _appliedH = height;
                return true;
            }

            // Not connected yet (or server-side live resize unsupported): the properties
            // apply on the next connect / reconnect.
            bool ok = ComInvoke.TrySet(_ocx, "DesktopWidth", width)
                   && ComInvoke.TrySet(_ocx, "DesktopHeight", height);
            if (ok && !live)
            {
                _appliedW = width;
                _appliedH = height;
            }
            return ok;
        }

        /// <summary>
        /// IMsRdpClient10::UpdateSessionDisplaySettings via late binding. Two argument shapes
        /// are tried because the IDispatch marshaller can insist on ULONG or LONG.
        /// </summary>
        private bool TryLiveResize(int width, int height)
        {
            int scale = DeviceScale();
            object[] ulongArgs = new object[]
            {
                (uint)width, (uint)height, (uint)0, (uint)0, (uint)0,
                (uint)scale, (uint)100
            };
            object[] longArgs = new object[] { width, height, 0, 0, 0, scale, 100 };
            try { ComInvoke.Call(_ocx, "UpdateSessionDisplaySettings", ulongArgs); return true; }
            catch { }
            try { ComInvoke.Call(_ocx, "UpdateSessionDisplaySettings", longArgs); return true; }
            catch { return false; }
        }

        /// <summary>Diagnostics: does this control expose the live-resolution API?</summary>
        public static string ProbeDynamicResize()
        {
            object o = null;
            try
            {
                string progId = PickProgId();
                if (string.IsNullOrEmpty(progId)) return "无可用控件";
                Type t = Type.GetTypeFromProgID(progId, false);
                if (t == null) return "无可用控件";
                o = Activator.CreateInstance(t);
                if (o == null) return "创建失败";
                try
                {
                    ComInvoke.Call(o, "UpdateSessionDisplaySettings",
                        (uint)1024, (uint)768, (uint)0, (uint)0, (uint)0, (uint)100, (uint)100);
                    return "OK（已在未连接状态下被接受）";
                }
                catch (Exception ex)
                {
                    Exception inner = (ex is TargetInvocationException && ex.InnerException != null)
                        ? ex.InnerException : ex;
                    COMException ce = inner as COMException;
                    if (ce != null && (uint)ce.ErrorCode == 0x80020003u)
                        return "不可用（控件版本过旧，将退回缩放显示）";
                    string code = ce != null ? string.Format("0x{0:X8}", ce.ErrorCode) : inner.GetType().Name;
                    return "OK（方法可解析，" + code + "，未连接时报错属正常）";
                }
            }
            catch (Exception ex) { return "探测失败：" + ex.Message; }
            finally
            {
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }

        /// <summary>Diagnostics: which redirection switches this control actually accepts.</summary>
        public static string ProbeRedirection()
        {
            object o = null;
            try
            {
                string progId = PickProgId();
                if (string.IsNullOrEmpty(progId)) return "无可用控件";
                Type t = Type.GetTypeFromProgID(progId, false);
                if (t == null) return "无可用控件";
                o = Activator.CreateInstance(t);
                if (o == null) return "创建失败";
                object adv = ComInvoke.GetAdvanced(o);
                string[] names = { "RedirectDrives", "RedirectClipboard",
                                   "RedirectDynamicDrives", "ShowRedirectionWarningDialog" };
                List<string> res = new List<string>();
                foreach (string n in names)
                {
                    string w;
                    bool set = ComInvoke.TrySetAny(o, adv, n, true, out w);
                    object back;
                    string rw;
                    bool get = ComInvoke.TryGetAny(o, adv, n, out back, out rw);
                    res.Add(n + "=" + (set ? "可写" : "不可写") +
                        (get ? "(回读 " + back + ")" : ""));
                }
                return string.Join("  ", res.ToArray());
            }
            catch (Exception ex) { return "探测失败：" + ex.Message; }
            finally
            {
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }

        /// <summary>Diagnostics: which audio redirection switches this control accepts.</summary>
        public static string ProbeAudio()
        {
            object o = null;
            try
            {
                string progId = PickProgId();
                if (string.IsNullOrEmpty(progId)) return "无可用控件";
                Type t = Type.GetTypeFromProgID(progId, false);
                if (t == null) return "无可用控件";
                o = Activator.CreateInstance(t);
                if (o == null) return "创建失败";
                object adv = ComInvoke.GetAdvanced(o);
                string w, rw;
                bool set = ComInvoke.TrySetAny(o, adv, "AudioRedirectionMode", 2, out w);
                object back;
                bool get = ComInvoke.TryGetAny(o, adv, "AudioRedirectionMode", out back, out rw);
                bool legacy = ComInvoke.TrySetAny(o, adv, "RedirectSound", false, out w);
                return "AudioRedirectionMode=" + (set ? "可写" : "不可写") +
                       (get ? "(回读 " + back + ")" : "") +
                       "  RedirectSound(旧)=" + (legacy ? "可写" : "不可写");
            }
            catch (Exception ex) { return "探测失败：" + ex.Message; }
            finally
            {
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }

        /// <summary>Diagnostics: can the secure attention sequence be sent via IDispatch?</summary>
        public static string ProbeCtrlAltDel()
        {
            object o = null;
            try
            {
                string progId = PickProgId();
                if (string.IsNullOrEmpty(progId)) return "无可用控件";
                Type t = Type.GetTypeFromProgID(progId, false);
                if (t == null) return "无可用控件";
                o = Activator.CreateInstance(t);
                if (o == null) return "创建失败";
                try
                {
                    ComInvoke.Call(o, "SendCtrlAltDel");
                    return "OK（方法可解析并调用）";
                }
                catch (Exception ex)
                {
                    Exception inner = (ex is TargetInvocationException && ex.InnerException != null)
                        ? ex.InnerException : ex;
                    COMException ce = inner as COMException;
                    if (IsMemberMissing(ce))
                        return "不可用（IDispatch 未暴露该方法，改用 Ctrl+Alt+End）";
                    string code = ce != null ? string.Format("0x{0:X8}", ce.ErrorCode) : inner.GetType().Name;
                    return "方法可解析（" + code + "，未连接时报错属正常）";
                }
            }
            catch (Exception ex) { return "探测失败：" + ex.Message; }
            finally
            {
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
            }
        }

        public void Disconnect()
        {
            if (_ocx == null || IsDisposed) return;
            try { ComInvoke.Call(_ocx, "Disconnect"); }
            catch { /* not connected - normal */ }
        }

        /// <summary>0 = disconnected, 1 = connected, 2 = connecting.</summary>
        public int ConnectedState
        {
            get
            {
                if (_ocx == null || IsDisposed) return 0;
                object v;
                if (ComInvoke.TryGet(_ocx, "Connected", out v) && v != null)
                {
                    try
                    {
                        int state = Convert.ToInt32(v);
                        if (state == 1) _everConnected = true;
                        return state;
                    }
                    catch { }
                }
                return 0;
            }
        }

        public bool IsOnline { get { return ConnectedState == 1; } }

        /// <summary>Reads the control's real fullscreen flag (it can exit fullscreen on its own).</summary>
        public bool ReadFullScreen()
        {
            if (_ocx == null || IsDisposed) return _fullScreen;
            object v;
            if (ComInvoke.TryGet(_ocx, "FullScreen", out v) && v != null)
            {
                try { _fullScreen = Convert.ToBoolean(v); } catch { }
            }
            return _fullScreen;
        }

        public bool ToggleFullScreen()
        {
            object ocx = EnsureOcx();
            bool next = !ReadFullScreen();
            if (!ComInvoke.TrySet(ocx, "FullScreen", next))
                throw new InvalidOperationException("该控件版本不支持全屏切换。");
            _fullScreen = next;
            SyncResolutionToFullScreen(next);
            return next;
        }

        /// <summary>
        /// Keeps the remote desktop resolution in step with the fullscreen state. mstscax
        /// switches to its own fullscreen window but leaves the session resolution alone,
        /// so a window-sized desktop keeps huge black letterbox bars on a full screen.
        /// Called on our own toggle and whenever the control reports a fullscreen change
        /// (e.g. the user leaving with Ctrl+Alt+Break).
        /// </summary>
        public void SyncResolutionToFullScreen(bool fullScreen)
        {
            if (_ocx == null || IsDisposed) return;
            try
            {
                if (fullScreen)
                {
                    Rectangle b = Screen.FromControl(this).Bounds;
                    if (ConnectedState == 1)
                    {
                        bool live = TryLiveResize(b.Width, b.Height);
                        if (live)
                        {
                            _appliedW = b.Width;
                            _appliedH = b.Height;
                        }
                        else if (_adv != null)
                        {
                            // Live resize rejected: fall back to scaling so the screen still
                            // fills edge to edge instead of letterboxing.
                            ComInvoke.TrySet(_adv, "SmartSizing", true);
                        }
                    }
                    else
                    {
                        // Applies on the next connect.
                        ResizeDesktop(b.Width, b.Height);
                    }
                }
                else
                {
                    Size area = ClientSize;
                    if (area.Width >= 200 && area.Height >= 150)
                        ResizeDesktop(area.Width, area.Height);
                    // Restore the configured scaling behaviour after a fullscreen stretch.
                    if (_adv != null && Info != null)
                        ComInvoke.TrySet(_adv, "SmartSizing", Info.SmartSizing || Ui.FitToWindow);
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Disconnect(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Environment self-check: which RDP control versions are usable on this machine.</summary>
    internal static class Diagnostics
    {
        public static string Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RemoteHub 环境自检  " + DateTime.Now.ToString("s"));
            sb.AppendLine("版本     : " + Application.ProductVersion);
            sb.AppendLine("运行模式 : " + ConnectionStore.ModeText);
            sb.AppendLine("OS       : " + DescribeOs());
            sb.AppendLine("CLR      : " + Environment.Version);
            sb.AppendLine("64位进程 : " + (IntPtr.Size == 8));
            sb.AppendLine("配置文件 : " + ConnectionStore.FilePath);
            sb.AppendLine("外观配色 : " + Ui.PaletteName + " [" + Ui.PaletteId + "]  " + Ui.ThemeName +
                "  强调=" + Ui.Hex(Ui.Accent) + "  导航=" + Ui.Hex(Ui.Sidebar) +
                "  主区=" + Ui.Hex(Ui.Surface));
            sb.AppendLine();
            sb.AppendLine("--- 已注册的 RDP ActiveX 控件 ---");

            int usable = 0;
            foreach (string p in RdpHost.Candidates)
            {
                Type t;
                try { t = Type.GetTypeFromProgID(p, false); }
                catch { continue; }
                if (t == null || t.GUID == Guid.Empty) continue;

                string line = "[OK] " + p.PadRight(38) + " CLSID=" + t.GUID.ToString("B");
                object o = null;
                try
                {
                    o = Activator.CreateInstance(t);
                    bool server = ComInvoke.TrySet(o, "Server", "127.0.0.1");
                    object adv = ComInvoke.GetAdvanced(o);
                    bool pwd = adv != null && ComInvoke.TrySet(adv, "ClearTextPassword", "probe");
                    line += "  Server=" + (server ? "Y" : "N") +
                            "  Advanced=" + (adv != null ? "Y" : "N") +
                            "  密码注入=" + (pwd ? "Y" : "N");
                    if (server && adv != null) usable++;
                }
                catch (Exception ex)
                {
                    line += "  创建失败: " + ex.Message;
                }
                finally
                {
                    if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
                }
                sb.AppendLine(line);
            }

            sb.AppendLine();
            sb.AppendLine("可用版本数 : " + usable);
            sb.AppendLine("自动选用   : " + RdpHost.PickProgId());
            sb.AppendLine("动态分辨率 : " + RdpHost.ProbeDynamicResize());
            sb.AppendLine("文件重定向 : " + RdpHost.ProbeRedirection());
            sb.AppendLine("音频重定向 : " + RdpHost.ProbeAudio());
            sb.AppendLine("安全键方法 : " + RdpHost.ProbeCtrlAltDel());
            sb.AppendLine("连接历史   : " + SessionHistory.Probe());
            sb.AppendLine("跟随窗口   : " + (Ui.FitToWindow ? "开（远程分辨率跟随会话区域）" : "关（使用主机配置的分辨率）"));
            sb.AppendLine();
            sb.AppendLine("--- AxHost 实际承载测试 ---");
            try
            {
                using (Form f = new Form())
                {
                    f.Width = 800;
                    f.Height = 600;
                    RdpHost host = new RdpHost();
                    f.Controls.Add(host);
                    f.CreateControl();
                    object ocx = host.EnsureOcx();
                    bool advOk = ComInvoke.GetAdvanced(ocx) != null;
                    sb.AppendLine("承载结果   : 成功");
                    sb.AppendLine("使用 ProgID: " + RdpHost.CurrentProgId);
                    sb.AppendLine("GetOcx()   : " + (ocx != null ? "OK" : "NULL"));
                    sb.AppendLine("AdvancedSettings: " + (advOk ? "OK" : "不可用"));
                    sb.AppendLine("Connected 初始值 : " + host.ConnectedState);

                    // Verify late-bound property writes/reads on the *hosted* instance.
                    ConnectionInfo probe = new ConnectionInfo();
                    probe.Host = "192.0.2.1";
                    probe.Port = 3390;
                    probe.UserName = "selftest";
                    probe.Password = "selftest";
                    probe.ColorDepth = 16;
                    probe.AudioMode = 2;   // deliberately non-default so a read-back proves the write
                    bool apply = host.ApplySettings(probe, 1024, 768);
                    object srv, usr, dw, cdepth;
                    ComInvoke.TryGet(ocx, "Server", out srv);
                    ComInvoke.TryGet(ocx, "UserName", out usr);
                    ComInvoke.TryGet(ocx, "DesktopWidth", out dw);
                    ComInvoke.TryGet(ocx, "ColorDepth", out cdepth);
                    sb.AppendLine("ApplySettings : " + (apply ? "OK" : "FAILED") +
                        "  Server=" + srv + "  User=" + usr +
                        "  宽=" + dw + "  色深=" + cdepth);
                    sb.AppendLine("密码注入     : " + (host.PasswordInjected ? "OK" : "失败") +
                        (string.IsNullOrEmpty(host.LastError) ? "" : "  " + host.LastError));
                    sb.AppendLine("文件重定向   : " + host.RedirectionReport);
                    sb.AppendLine("音频设置     : " + host.AudioReport);
                    object rd, rc, am;
                    string w1, w2, w3;
                    object advNow = ComInvoke.GetAdvanced(ocx);
                    ComInvoke.TryGetAny(ocx, advNow, "RedirectDrives", out rd, out w1);
                    ComInvoke.TryGetAny(ocx, advNow, "RedirectClipboard", out rc, out w2);
                    ComInvoke.TryGetAny(ocx, advNow, "AudioRedirectionMode", out am, out w3);
                    sb.AppendLine("重定向回读   : RedirectDrives=" + rd + "  RedirectClipboard=" + rc);
                    sb.AppendLine("音频回读     : AudioRedirectionMode=" + am + "  (期望 2)");

                    // Follow-the-window path: resize without a live session (applied on connect).
                    bool resized = host.ResizeDesktop(1280, 800);
                    object dw2, dh2;
                    ComInvoke.TryGet(ocx, "DesktopWidth", out dw2);
                    ComInvoke.TryGet(ocx, "DesktopHeight", out dh2);
                    sb.AppendLine("跟随窗口调整 : " + (resized ? "调用成功" : "未变化") +
                        "  目标=1280x800  回读=" + dw2 + "x" + dh2);

                    // Fullscreen path (1.3.0 fix): the desktop must be resized to the whole
                    // screen, otherwise mstscax letterboxes a window-sized session in black.
                    Rectangle scr = Screen.FromControl(host).Bounds;
                    host.SyncResolutionToFullScreen(true);
                    object fw, fh;
                    ComInvoke.TryGet(ocx, "DesktopWidth", out fw);
                    ComInvoke.TryGet(ocx, "DesktopHeight", out fh);
                    bool fsOk = fw != null && fh != null &&
                        Convert.ToInt32(fw) == scr.Width && Convert.ToInt32(fh) == scr.Height;
                    sb.AppendLine("全屏分辨率   : 屏幕=" + scr.Width + "x" + scr.Height +
                        "  回读=" + fw + "x" + fh + "  " + (fsOk ? "OK" : "未同步"));
                    host.SyncResolutionToFullScreen(false);
                    object bw2, bh2;
                    ComInvoke.TryGet(ocx, "DesktopWidth", out bw2);
                    ComInvoke.TryGet(ocx, "DesktopHeight", out bh2);
                    sb.AppendLine("退出全屏     : 恢复控件区域=" + host.ClientSize.Width + "x" +
                        host.ClientSize.Height + "  回读=" + bw2 + "x" + bh2);

                    try
                    {
                        ComInvoke.Call(ocx, "Disconnect");
                        sb.AppendLine("方法调用     : Disconnect 可解析并调用 (Connect 同属主接口)");
                    }
                    catch (Exception mex)
                    {
                        Exception inner = (mex is TargetInvocationException && mex.InnerException != null)
                            ? mex.InnerException : mex;
                        sb.AppendLine("方法调用     : Disconnect 名称可解析 (" +
                            inner.GetType().Name + "，未连接时报错属正常，Connect 同属主接口)");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("承载结果   : 失败");
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine();
            sb.AppendLine("--- 本地功能自检 ---");
            try
            {
                string sample = "p@ss,中文";
                string enc = Crypto.Protect(sample);
                sb.AppendLine("DPAPI 加解密 : " +
                    (!string.IsNullOrEmpty(enc) && Crypto.Unprotect(enc) == sample ? "OK" : "FAIL"));

                ConnectionStore tmpStore = new ConnectionStore();
                List<string> probs;
                int n = tmpStore.ImportText(
                    "10.0.0.1,administrator,pass123\r\n" +
                    "10.0.0.2:3390\r\n" +
                    "# comment line\r\n" +
                    "Web-01,生产,10.0.1.5,3389,admin,secret,DOMAIN,主站\r\n", out probs);
                sb.AppendLine("CSV 批量导入 : 新增 " + n + " 台 / 提示 " + probs.Count + " 条");

                System.Xml.Serialization.XmlSerializer ser =
                    new System.Xml.Serialization.XmlSerializer(typeof(ConnectionStore));
                using (MemoryStream ms = new MemoryStream())
                {
                    ser.Serialize(ms, tmpStore);
                    ms.Position = 0;
                    ConnectionStore back = (ConnectionStore)ser.Deserialize(ms);
                    sb.AppendLine("XML 配置往返 : " + back.Items.Count + " 台");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("本地功能自检失败: " + ex);
            }

            sb.AppendLine();
            sb.AppendLine("注：常规自检不会真正发起连接。完整连接链路验证请运行 RemoteHub.exe --smoke");
            return sb.ToString();
        }

        private static string DescribeOs()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string name = k.GetValue("ProductName") as string;
                        string build = k.GetValue("CurrentBuild") as string;
                        string disp = k.GetValue("DisplayVersion") as string;
                        if (!string.IsNullOrEmpty(name))
                            return name + " (Build " + build +
                                (string.IsNullOrEmpty(disp) ? "" : ", " + disp) + ")";
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }
    }

    /// <summary>
    /// Verifies the real connect path (ApplySettings + Connect) against localhost.
    /// It never stores a session and exits automatically after ~1.2 seconds.
    /// </summary>
    internal static class SmokeTest
    {
        public static string LogPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke.log"); }
        }

        public static void Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RemoteHub 连接链路自检  " + DateTime.Now.ToString("s"));
            sb.AppendLine("目标: 127.0.0.1:3389 (本机，仅验证 Connect 调用，不建立持久会话)");
            sb.AppendLine();

            // 看门狗：本自检会真正驱动 RDP 控件。一旦系统弹出模态窗口（许可协议、
            // 证书警告、连接失败提示等），窗体消息循环就可能被卡住，--smoke 会永远
            // 退不出去。到达硬性时限后直接结束进程，保证这个命令永远不会挂死。
            // 用全限定名，避免引入 System.Threading 后与 WinForms 的 Timer 重名。
            System.Threading.Thread watchdog = new System.Threading.Thread(delegate ()
            {
                System.Threading.Thread.Sleep(25000);
                try { Write(sb); } catch { }
                Environment.Exit(3);
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            // 先确认系统里确实注册了 RDP ActiveX 控件。没有它就不要去创建窗体——
            // 控件缺失时创建过程本身就可能弹窗。缺控件属于环境限制，不是程序故障。
            string progId = RdpHost.PickProgId();
            if (string.IsNullOrEmpty(progId))
            {
                sb.AppendLine("结果         : 跳过——未找到系统自带的 RDP ActiveX 控件。");
                sb.AppendLine("             请在「Windows 可选功能」中启用「远程桌面客户端」后重试。");
                Write(sb);
                return;
            }
            sb.AppendLine("RDP 控件     : " + progId);
            sb.AppendLine();

            Form f = new Form();
            f.Text = "RemoteHub 连接链路自检";
            f.Width = 420;
            f.Height = 320;
            f.ShowInTaskbar = false;
            // 不在屏幕中央显示：无人值守环境（CI、计划任务）下弹到前台容易被误认为卡死。
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-32000, -32000);
            f.Opacity = 0;

            RdpHost host = new RdpHost();
            f.Controls.Add(host);

            Timer timer = new Timer { Interval = 1200 };
            timer.Tick += delegate
            {
                timer.Stop();
                sb.AppendLine("1.2 秒后 Connected 状态: " + host.ConnectedState);
                try { host.Disconnect(); } catch { }
                Write(sb);
                try { f.Close(); } catch { }
                Application.Exit();
            };

            f.Shown += delegate
            {
                try
                {
                    ConnectionInfo c = new ConnectionInfo();
                    c.Host = "127.0.0.1";
                    c.Port = 3389;
                    c.UserName = "smoketest";
                    c.Password = "smoketest";
                    bool ok = host.ApplySettings(c, 1024, 768);
                    sb.AppendLine("ApplySettings : " + (ok ? "OK" : "FAILED") +
                        "  密码注入=" + host.PasswordInjected +
                        (string.IsNullOrEmpty(host.LastError) ? "" : "  " + host.LastError));
                    host.Connect();
                    sb.AppendLine("Connect()     : 调用成功，未抛出异常");
                }
                catch (Exception ex)
                {
                    Exception e = (ex is TargetInvocationException && ex.InnerException != null)
                        ? ex.InnerException : ex;
                    sb.AppendLine("Connect()     : 失败 " + e.GetType().Name + ": " + e.Message);
                    Program.Log("SMOKE", e);
                }
                sb.AppendLine("立即 Connected 状态: " + host.ConnectedState);
                timer.Start();
            };

            try { Application.Run(f); }
            catch (Exception ex) { sb.AppendLine("窗体运行异常: " + ex); }
            Write(sb);
        }

        private static void Write(StringBuilder sb)
        {
            try { File.WriteAllText(LogPath, sb.ToString(), Encoding.UTF8); } catch { }
        }
    }
}
