using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace RemoteHub
{
    /// <summary>
    /// A named colour scheme. Every scheme is described by three seed colours plus one
    /// switch; Ui derives the full semantic palette from them, so a user-defined scheme
    /// stays legible in both light and dark mode without hand-tuning every control.
    /// </summary>
    internal sealed class UiPalette
    {
        public string Id;
        public string Name;
        public string Note;
        public Color Accent;
        public Color Nav;
        public Color Base;
        public bool DarkNav;

        public UiPalette(string id, string name, string note,
                         Color accent, Color nav, Color baseColor, bool darkNav)
        {
            Id = id; Name = name; Note = note;
            Accent = accent; Nav = nav; Base = baseColor; DarkNav = darkNav;
        }
    }

    internal enum UiIcon
    {
        None, Plus, Edit, Trash, Play, Stop, Refresh, Expand, Import, Export,
        Close, Search, Folder, Monitor, Moon, Sun, Check, Info, Server, Bolt,
        Copy, Help, Gear, Logo, Link, Chevron, Shield, Grid, Key, Power, More, Palette
    }

    internal enum RhButtonKind { Primary, Ghost, Danger, Subtle, Chip, Outline }

    /// <summary>
    /// Central visual language for RemoteHub. Pure GDI+ so the project still builds with the
    /// .NET Framework compiler shipped with Windows (no external UI libraries, no resources).
    /// Two palettes (light / dark) are switchable at runtime and persisted next to the address book.
    /// </summary>
    internal static class Ui
    {
        /// <summary>
        /// Corner radius used by every painted surface. Kept deliberately small: a classic
        /// Windows tool uses near-square corners. The soft pills of web UIs are the single
        /// strongest "this was generated" signal, so they are gone.
        /// </summary>
        public const int Radius = 3;

        private static bool _dark;
        private static bool _fit = true;
        // 1.3.0 goes back to the 1.1.3 look. The parameterised engine below is kept as
        // internal plumbing (the diagnostics report reads PaletteName), but every surface
        // entry point was removed, so the app always renders the classic indigo/white skin.
        private static string _paletteId = "blue";
        private static Color _accent = Color.FromArgb(75, 92, 245);
        private static Color _nav = Color.FromArgb(247, 248, 251);
        private static Color _baseColor = Color.FromArgb(255, 255, 255);
        private static bool _darkNav = false;
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        /// <summary>Built-in schemes. "blue" is the old indigo/white look, kept for comparison.</summary>
        public static readonly UiPalette[] Presets = new UiPalette[]
        {
            new UiPalette("graphite", "石墨青绿", "深墨绿导航 · 青绿强调 · 冷灰白主区",
                Color.FromArgb(14, 159, 120), Color.FromArgb(20, 36, 31), Color.FromArgb(244, 246, 245), true),
            new UiPalette("sand", "暖砂铜橙", "深棕导航 · 陶土橙强调 · 暖米白主区",
                Color.FromArgb(180, 104, 47), Color.FromArgb(43, 32, 25), Color.FromArgb(248, 244, 237), true),
            new UiPalette("mono", "纯黑白灰", "完全去彩，仅在线状态保留绿色",
                Color.FromArgb(59, 65, 72), Color.FromArgb(28, 31, 35), Color.FromArgb(243, 244, 246), true),
            new UiPalette("blue", "经典蓝白", "旧版靛蓝配色，留作对照",
                Color.FromArgb(75, 92, 245), Color.FromArgb(247, 248, 251), Color.FromArgb(255, 255, 255), false),
        };

        public static bool Dark { get { return _dark; } }
        public static string ThemeName { get { return _dark ? "深色" : "浅色"; } }
        public static event EventHandler ThemeChanged;

        // ---- palette (recomputed whenever the theme changes) ----
        public static Color Accent;
        public static Color AccentHover;
        public static Color AccentSoft;
        public static Color Sidebar;
        public static Color SidebarAlt;
        public static Color SidebarText;
        public static Color SidebarMuted;
        public static Color SidebarHover;
        public static Color SidebarSelected;
        public static Color SidebarOffline;   // offline dot drawn inside the navigation column
        public static Color SidebarField;     // search box inside a dark navigation column
        public static Color SidebarBorder;
        public static Color SidebarActiveText; // selected row label, lifted for dark navigation
        public static Color TopBar;
        public static Color Surface;
        public static Color Card;
        public static Color CardAlt;
        public static Color Border;
        public static Color BorderSoft;
        public static Color Text;
        public static Color TextMuted;
        public static Color StatusBar;
        public static Color Field;
        public static Color Success;
        public static Color Warning;
        public static Color Danger;
        public static Color TabActive;
        public static Color TabInactive;
        public static Color Shadow;

        static Ui()
        {
            Dictionary<string, string> ini = ReadIni();
            _dark = ReadBool(ini, "dark", false);
            _fit = ReadBool(ini, "fit", true);
            string pid = ReadText(ini, "palette", "blue");
            UiPalette p = FindPreset(pid);
            _paletteId = (p == null && pid != "custom") ? "blue" : pid;
            _accent = ReadColor(ini, "accent", p == null ? Color.FromArgb(75, 92, 245) : p.Accent);
            _nav = ReadColor(ini, "nav", p == null ? Color.FromArgb(247, 248, 251) : p.Nav);
            _baseColor = ReadColor(ini, "base", p == null ? Color.White : p.Base);
            _darkNav = ReadBool(ini, "darknav", p != null && p.DarkNav);
            if (p != null)
            {
                // A named preset always resolves to its canonical seeds, so a stale ui.ini
                // entry can never leave a preset half-customised. The dark-navigation
                // preference is deliberately *not* reset here: it is an independent toggle,
                // so "graphite + light navigation" survives a restart.
                _accent = p.Accent; _nav = p.Nav; _baseColor = p.Base;
            }
            ApplyPalette();
        }

        private static string SettingsFile
        {
            get { return Path.Combine(ConnectionStore.DirectoryPath, "ui.ini"); }
        }

        private static Dictionary<string, string> ReadIni()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(SettingsFile))
                    foreach (string line in File.ReadAllLines(SettingsFile))
                    {
                        string s = line.Trim();
                        int i = s.IndexOf('=');
                        if (i <= 0 || s[0] == ';' || s[0] == '#') continue;
                        d[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        private static bool ReadBool(Dictionary<string, string> ini, string key, bool fallback)
        {
            string v;
            if (!ini.TryGetValue(key, out v)) return fallback;
            return v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadText(Dictionary<string, string> ini, string key, string fallback)
        {
            string v;
            return (ini.TryGetValue(key, out v) && v.Length > 0) ? v : fallback;
        }

        private static Color ReadColor(Dictionary<string, string> ini, string key, Color fallback)
        {
            string v;
            Color c;
            return (ini.TryGetValue(key, out v) && TryParseHex(v, out c)) ? c : fallback;
        }

        public static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static bool TryParseHex(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6) return false;
            int r, g, b;
            if (!int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
                !int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
                !int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            c = Color.FromArgb(r, g, b);
            return true;
        }

        /// <summary>
        /// True = the remote desktop resolution follows the session area (no letterboxing,
        /// image always fills the window). False = use the resolution configured per host.
        /// </summary>
        public static bool FitToWindow
        {
            get { return _fit; }
            set
            {
                if (_fit == value) return;
                _fit = value;
                Save();
            }
        }

        // ---- appearance API ---------------------------------------------
        public static UiPalette FindPreset(string id)
        {
            foreach (UiPalette p in Presets)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public static string PaletteId { get { return _paletteId; } }
        public static string PaletteName
        {
            get { UiPalette p = FindPreset(_paletteId); return p == null ? "自定义" : p.Name; }
        }
        public static Color AccentSeed { get { return _accent; } }
        public static Color NavSeed { get { return _nav; } }
        public static Color BaseSeed { get { return _baseColor; } }
        public static bool DarkNav { get { return _darkNav; } }

        /// <summary>
        /// Recomputes every palette entry and repaints the app through ThemeChanged.
        /// persist=false is used by the appearance dialog for live preview (ui.ini untouched);
        /// persist=true commits the choice to disk.
        /// </summary>
        public static void SetAppearance(string id, Color accent, Color nav, Color baseColor,
                                         bool darkNav, bool persist)
        {
            _paletteId = string.IsNullOrEmpty(id) ? "custom" : id;
            _accent = accent; _nav = nav; _baseColor = baseColor; _darkNav = darkNav;
            ApplyPalette();
            if (persist) Save();
            if (ThemeChanged != null) ThemeChanged(null, EventArgs.Empty);
        }

        public static void UsePreset(string id, bool persist)
        {
            UiPalette p = FindPreset(id);
            if (p == null) return;
            SetAppearance(p.Id, p.Accent, p.Nav, p.Base, p.DarkNav, persist);
        }

        public static void ResetAppearance(bool persist)
        {
            UiPalette p = Presets[0];
            SetAppearance(p.Id, p.Accent, p.Nav, p.Base, p.DarkNav, persist);
        }

        public static void SetDark(bool dark, bool persist)
        {
            if (_dark == dark) return;
            _dark = dark;
            ApplyPalette();
            if (persist) Save();
            if (ThemeChanged != null) ThemeChanged(null, EventArgs.Empty);
        }

        public static void ToggleTheme()
        {
            SetDark(!_dark, true);
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(ConnectionStore.DirectoryPath);
                // 1.3.0 keeps the config file in the 1.1.3 shape: only dark / fit.
                // The colour scheme is fixed to the classic palette and no longer
                // emits any of the 1.2.0 appearance keys.
                File.WriteAllText(SettingsFile,
                    "dark=" + (_dark ? "1" : "0") + "\r\n" +
                    "fit=" + (_fit ? "1" : "0") + "\r\n",
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        // ---- seed colours -> full semantic palette ------------------------
        private static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t + 0.5),
                (int)(a.G + (b.G - a.G) * t + 0.5),
                (int)(a.B + (b.B - a.B) * t + 0.5));
        }

        private static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        /// <summary>
        /// Literal 1.1.3 colour table. The 1.2.0 scheme was rejected, so the classic
        /// indigo/white look is restored verbatim instead of being re-derived from seeds.
        /// </summary>
        private static void ApplyClassicPalette()
        {
            if (_dark)
            {
                Accent = Color.FromArgb(123, 136, 255);
                AccentHover = Color.FromArgb(146, 157, 255);
                AccentSoft = Color.FromArgb(37, 43, 69);
                Sidebar = Color.FromArgb(34, 38, 47);
                SidebarAlt = Color.FromArgb(34, 38, 47);
                SidebarText = Color.FromArgb(233, 235, 240);
                SidebarMuted = Color.FromArgb(139, 147, 163);
                SidebarHover = Color.FromArgb(42, 48, 59);
                SidebarSelected = Color.FromArgb(43, 51, 89);
                TopBar = Color.FromArgb(27, 31, 39);
                Surface = Color.FromArgb(27, 31, 39);
                Card = Color.FromArgb(32, 36, 45);
                CardAlt = Color.FromArgb(34, 38, 47);
                Border = Color.FromArgb(46, 52, 64);
                BorderSoft = Color.FromArgb(39, 44, 54);
                Text = Color.FromArgb(233, 235, 240);
                TextMuted = Color.FromArgb(139, 147, 163);
                StatusBar = Color.FromArgb(31, 35, 43);
                Field = Color.FromArgb(34, 38, 47);
                Success = Color.FromArgb(61, 220, 132);
                Warning = Color.FromArgb(251, 191, 36);
                Danger = Color.FromArgb(248, 113, 113);
                TabActive = Color.FromArgb(27, 31, 39);
                TabInactive = Color.FromArgb(34, 38, 47);
                Shadow = Color.FromArgb(12, 15, 20);
                SidebarOffline = Color.FromArgb(96, 105, 120);
                SidebarField = Field;
                SidebarBorder = Border;
                SidebarActiveText = Accent;
            }
            else
            {
                Accent = Color.FromArgb(75, 92, 245);
                AccentHover = Color.FromArgb(58, 74, 224);
                AccentSoft = Color.FromArgb(238, 240, 255);
                Sidebar = Color.FromArgb(247, 248, 251);
                SidebarAlt = Color.FromArgb(247, 248, 251);
                SidebarText = Color.FromArgb(23, 26, 33);
                SidebarMuted = Color.FromArgb(121, 129, 143);
                SidebarHover = Color.FromArgb(238, 240, 246);
                SidebarSelected = Color.FromArgb(230, 234, 252);
                TopBar = Color.FromArgb(255, 255, 255);
                Surface = Color.FromArgb(255, 255, 255);
                Card = Color.FromArgb(255, 255, 255);
                CardAlt = Color.FromArgb(247, 248, 251);
                Border = Color.FromArgb(229, 232, 238);
                BorderSoft = Color.FromArgb(238, 240, 244);
                Text = Color.FromArgb(23, 26, 33);
                TextMuted = Color.FromArgb(121, 129, 143);
                StatusBar = Color.FromArgb(247, 248, 251);
                Field = Color.FromArgb(255, 255, 255);
                Success = Color.FromArgb(34, 197, 94);
                Warning = Color.FromArgb(217, 119, 6);
                Danger = Color.FromArgb(239, 68, 68);
                TabActive = Color.FromArgb(255, 255, 255);
                TabInactive = Color.FromArgb(247, 248, 251);
                Shadow = Color.FromArgb(232, 235, 242);
                SidebarOffline = Color.FromArgb(185, 193, 206);
                SidebarField = Field;
                SidebarBorder = Border;
                SidebarActiveText = Accent;
            }
        }

        private static void ApplyPalette()
        {
            if (string.Equals(_paletteId, "blue", StringComparison.OrdinalIgnoreCase) &&
                _accent.R == 75 && _accent.G == 92 && _accent.B == 245)
            {
                ApplyClassicPalette();
                return;
            }
            Color A = _accent, N = _nav, B = _baseColor;
            // A near-white accent would make white button labels unreadable.
            if (Luma(A) > 0.72) A = Mix(A, Color.Black, 0.18);

            if (_dark)
            {
                Color deep = Mix(Color.FromArgb(21, 25, 31), N, 0.20);
                Surface = deep;
                Card = Mix(deep, Color.White, 0.075);
                CardAlt = Mix(deep, Color.White, 0.040);
                TopBar = Mix(deep, Color.Black, 0.16);
                StatusBar = Mix(deep, Color.Black, 0.08);
                Field = Mix(deep, Color.White, 0.06);
                Border = Mix(deep, Color.White, 0.15);
                BorderSoft = Mix(deep, Color.White, 0.085);
                Text = Color.FromArgb(233, 237, 244);
                TextMuted = Color.FromArgb(141, 151, 167);
                Accent = Mix(A, Color.White, 0.16);
                AccentHover = Mix(A, Color.White, 0.30);
                AccentSoft = Mix(deep, A, 0.24);
                Sidebar = Mix(deep, Color.Black, 0.20);
                SidebarAlt = Sidebar;
                SidebarHover = Mix(Sidebar, Color.White, 0.09);
                SidebarSelected = Mix(Sidebar, A, 0.30);
                SidebarText = Text;
                SidebarMuted = TextMuted;
                SidebarOffline = Mix(Sidebar, Color.White, 0.30);
                SidebarField = Mix(Sidebar, Color.White, 0.10);
                SidebarBorder = Mix(Sidebar, Color.White, 0.18);
                SidebarActiveText = Mix(A, Color.White, 0.35);
                TabActive = Card;
                TabInactive = CardAlt;
                Shadow = Mix(deep, Color.Black, 0.35);
            }
            else
            {
                Color white = Color.White;
                Surface = B;
                Card = Mix(B, white, 0.62);
                CardAlt = Mix(B, Color.Black, 0.028);
                TopBar = Card;
                StatusBar = Mix(B, Color.Black, 0.025);
                Field = Mix(B, white, 0.78);
                Border = Mix(B, Color.Black, 0.14);
                BorderSoft = Mix(B, Color.Black, 0.075);
                Text = Mix(B, Color.Black, 0.87);
                TextMuted = Mix(B, Color.Black, 0.52);
                Accent = A;
                AccentHover = Luma(A) < 0.35 ? Mix(A, white, 0.16) : Mix(A, Color.Black, 0.13);
                AccentSoft = Mix(B, A, 0.13);
                if (_darkNav)
                {
                    // Workbench look: a deep navigation column next to a light workspace.
                    Sidebar = N;
                    SidebarAlt = Mix(N, white, 0.075);
                    SidebarHover = Mix(N, white, 0.10);
                    SidebarSelected = Mix(N, A, 0.30);
                    SidebarText = Mix(N, white, 0.92);
                    SidebarMuted = Mix(N, white, 0.58);
                    SidebarOffline = Mix(N, white, 0.36);
                    SidebarField = Mix(N, white, 0.085);
                    SidebarBorder = Mix(N, white, 0.16);
                    SidebarActiveText = Mix(A, white, 0.35);
                }
                else
                {
                    Sidebar = Mix(B, Color.Black, 0.028);
                    SidebarAlt = Sidebar;
                    SidebarHover = Mix(B, Color.Black, 0.065);
                    SidebarSelected = Mix(B, A, 0.13);
                    SidebarText = Text;
                    SidebarMuted = TextMuted;
                    SidebarOffline = Mix(B, Color.Black, 0.30);
                    SidebarField = Field;
                    SidebarBorder = Border;
                    SidebarActiveText = A;
                }
                TabActive = Card;
                TabInactive = CardAlt;
                Shadow = Mix(B, Color.Black, 0.13);
            }
            Success = _dark ? Color.FromArgb(61, 220, 132) : Color.FromArgb(22, 163, 74);
            Warning = _dark ? Color.FromArgb(251, 191, 36) : Color.FromArgb(217, 119, 6);
            Danger = _dark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(220, 53, 53);
        }

        // ---- fonts -------------------------------------------------------
        // F() returns a cached font for painting only. Never assign it to a Control
        // (controls dispose their fonts, which would invalidate the shared instance).
        public static Font F(float size)
        {
            return F(size, FontStyle.Regular);
        }

        public static Font F(float size, FontStyle style)
        {
            string key = size.ToString("0.##") + "|" + ((int)style).ToString();
            Font f;
            if (!FontCache.TryGetValue(key, out f))
            {
                f = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
                FontCache[key] = f;
            }
            return f;
        }

        public static Font Mono(float size)
        {
            string key = "mono|" + size.ToString("0.##");
            Font f;
            if (!FontCache.TryGetValue(key, out f))
            {
                f = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
                FontCache[key] = f;
            }
            return f;
        }

        /// <summary>Fresh font instance, safe to assign to a control.</summary>
        public static Font NewF(float size)
        {
            return NewF(size, FontStyle.Regular);
        }

        public static Font NewF(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }

        // ---- geometry ----------------------------------------------------
        public static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (radius <= 0 || d > r.Width || d > r.Height)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Brush b, Rectangle r, int radius)
        {
            using (GraphicsPath p = RoundPath(r, radius)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Pen pen, Rectangle r, int radius)
        {
            using (GraphicsPath p = RoundPath(r, radius)) g.DrawPath(pen, p);
        }

        /// <summary>
        /// The one check-box glyph used everywhere in the app. Sharing it keeps the
        /// dialog controls and the dialog lists on the same visual language.
        /// </summary>
        public static void DrawCheckGlyph(Graphics g, Rectangle box, bool on, bool enabled, bool hot)
        {
            Color fill, edge;
            if (on)
            {
                fill = enabled ? Accent : BorderSoft;
                edge = fill;
            }
            else
            {
                fill = hot ? AccentSoft : Field;
                edge = !enabled ? BorderSoft : (hot ? Accent : Border);
            }
            using (SolidBrush b = new SolidBrush(fill)) FillRound(g, b, box, 3);
            if (!on)
            {
                using (Pen p = new Pen(edge, 1f)) DrawRound(g, p, box, 3);
                return;
            }
            Color tick = enabled ? Color.White : TextMuted;
            using (Pen p = new Pen(tick, 1.7f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(box.Left + 3.4f, box.Top + 7.4f),
                    new PointF(box.Left + 6.2f, box.Top + 10.2f),
                    new PointF(box.Left + 10.8f, box.Top + 4.6f)
                });
            }
        }

        /// <summary>Path with only the top two corners rounded (used by the session tabs).</summary>
        public static GraphicsPath RoundPathTop(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (radius <= 0 || d > r.Width || d > r.Height)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Clips a panel to a rounded shape so its square background can never peek out
        /// past the rounded border (the classic "rounded box with square corners" artefact).
        /// The clip radius is one pixel smaller than the painted radius, which keeps the
        /// anti-aliased stroke of the border visible all the way round.
        /// </summary>
        public static void RoundRegion(Control c, int radius)
        {
            if (c == null) return;
            if (c.Width <= 0 || c.Height <= 0) return;
            if (radius <= 0)
            {
                Region old0 = c.Region;
                c.Region = null;
                if (old0 != null) old0.Dispose();
                return;
            }
            int r = Math.Max(1, radius - 1);
            int d = r * 2;
            if (d >= c.Width || d >= c.Height)
            {
                c.Region = null;
                return;
            }
            using (GraphicsPath p = RoundPath(new Rectangle(0, 0, c.Width, c.Height), r))
            {
                Region old = c.Region;
                c.Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        /// <summary>
        /// Walks up the parent chain until a solid background colour is found. Needed because
        /// Graphics.Clear(Color.Transparent) paints black on a window surface, which used to
        /// leave black square corners behind every rounded button sitting on a transparent panel.
        /// </summary>
        public static Color ResolveBackColor(Control c)
        {
            Control p = c;
            int guard = 0;
            while (p != null && guard++ < 32)
            {
                Color b = p.BackColor;
                if (b.A == 255) return b;
                p = p.Parent;
            }
            return Surface;
        }

        public static void DrawCentered(Graphics g, string text, Font font, Color color, Rectangle area)
        {
            TextRenderer.DrawText(g, text, font, area, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        // ---- vector icons ------------------------------------------------
        public static void DrawIcon(Graphics g, UiIcon icon, Rectangle bounds, Color color)
        {
            DrawIcon(g, icon, bounds, color, 1.35f);
        }

        /// <summary>
        /// Draws one vector glyph. The glyphs are authored on a 24x24 grid, so the pen width
        /// has to be divided by the scale factor - otherwise a 16px icon would render its
        /// strokes at 1.9 * 16/24 = 1.27 device pixels and look washed out and grey.
        /// <paramref name="weight"/> is therefore the width in *device* pixels.
        /// </summary>
        public static void DrawIcon(Graphics g, UiIcon icon, Rectangle bounds, Color color, float weight)
        {
            if (icon == UiIcon.None || bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Keep the glyph on the pixel grid: without this, half-covered pixels along a
            // vertical stroke get smeared over two columns and the icon reads as blurry.
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(bounds.Left, bounds.Top);
            float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
            g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
            using (Pen p = new Pen(color, weight / scale))
            using (SolidBrush b = new SolidBrush(color))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                switch (icon)
                {
                    case UiIcon.Plus:
                        g.DrawLine(p, 12, 4, 12, 20);
                        g.DrawLine(p, 4, 12, 20, 12);
                        break;
                    case UiIcon.Close:
                        g.DrawLine(p, 6, 6, 18, 18);
                        g.DrawLine(p, 18, 6, 6, 18);
                        break;
                    case UiIcon.More:
                        g.FillEllipse(b, 3.2f, 10.5f, 3.2f, 3.2f);
                        g.FillEllipse(b, 10.4f, 10.5f, 3.2f, 3.2f);
                        g.FillEllipse(b, 17.6f, 10.5f, 3.2f, 3.2f);
                        break;
                    case UiIcon.Check:
                        g.DrawLines(p, new PointF[] { new PointF(4, 12.5f), new PointF(9.5f, 18), new PointF(20, 6) });
                        break;
                    case UiIcon.Search:
                        g.DrawEllipse(p, 3.5f, 3.5f, 12.5f, 12.5f);
                        g.DrawLine(p, 15.2f, 15.2f, 21, 21);
                        break;
                    case UiIcon.Play:
                        g.FillPolygon(b, new PointF[] { new PointF(7, 4), new PointF(19.5f, 12), new PointF(7, 20) });
                        break;
                    case UiIcon.Stop:
                        FillRound(g, b, new Rectangle(6, 6, 12, 12), 2);
                        break;
                    case UiIcon.Power:
                        g.DrawArc(p, new RectangleF(4.5f, 4.5f, 15, 15), 132, 276);
                        g.DrawLine(p, 12, 3, 12, 11);
                        break;
                    case UiIcon.Refresh:
                        g.DrawArc(p, new RectangleF(3.5f, 3.5f, 17, 17), 35, 285);
                        g.FillPolygon(b, new PointF[] { new PointF(19, 2), new PointF(20, 10), new PointF(13, 6) });
                        break;
                    case UiIcon.Expand:
                        g.DrawLine(p, 3, 9, 3, 3); g.DrawLine(p, 3, 3, 9, 3);
                        g.DrawLine(p, 15, 3, 21, 3); g.DrawLine(p, 21, 3, 21, 9);
                        g.DrawLine(p, 21, 15, 21, 21); g.DrawLine(p, 21, 21, 15, 21);
                        g.DrawLine(p, 9, 21, 3, 21); g.DrawLine(p, 3, 21, 3, 15);
                        break;
                    case UiIcon.Import:
                        g.DrawLine(p, 12, 3, 12, 14);
                        g.DrawLine(p, 7, 9, 12, 14);
                        g.DrawLine(p, 17, 9, 12, 14);
                        g.DrawLines(p, new PointF[] { new PointF(3, 16), new PointF(3, 20), new PointF(21, 20), new PointF(21, 16) });
                        break;
                    case UiIcon.Export:
                        g.DrawLine(p, 12, 15, 12, 4);
                        g.DrawLine(p, 7, 9, 12, 4);
                        g.DrawLine(p, 17, 9, 12, 4);
                        g.DrawLines(p, new PointF[] { new PointF(3, 16), new PointF(3, 20), new PointF(21, 20), new PointF(21, 16) });
                        break;
                    case UiIcon.Trash:
                        g.DrawLine(p, 4, 6, 20, 6);
                        g.DrawLine(p, 9, 6, 9, 3.5f);
                        g.DrawLine(p, 15, 6, 15, 3.5f);
                        g.DrawLines(p, new PointF[] { new PointF(6, 6), new PointF(7, 21), new PointF(17, 21), new PointF(18, 6) });
                        g.DrawLine(p, 10, 10, 10, 17);
                        g.DrawLine(p, 14, 10, 14, 17);
                        break;
                    case UiIcon.Edit:
                        g.DrawLines(p, new PointF[] { new PointF(4, 20), new PointF(4, 16.5f), new PointF(15, 5.5f), new PointF(18.5f, 9), new PointF(7.5f, 20), new PointF(4, 20) });
                        g.DrawLine(p, 13.5f, 7, 17, 10.5f);
                        break;
                    case UiIcon.Folder:
                        g.DrawLines(p, new PointF[] { new PointF(3, 19), new PointF(3, 6), new PointF(9, 6), new PointF(11, 8.5f), new PointF(21, 8.5f), new PointF(21, 19), new PointF(3, 19) });
                        break;
                    case UiIcon.Monitor:
                        g.DrawRectangle(p, 3, 4, 18, 13);
                        g.DrawLine(p, 8, 21, 16, 21);
                        g.DrawLine(p, 12, 17, 12, 21);
                        break;
                    case UiIcon.Moon:
                        // Crescent: a disc with a second disc cut out of it. The inner
                        // ellipse is traced in the opposite direction so the Winding
                        // fill mode cancels it out; two overlapping arcs produced a
                        // broken blob at 16px.
                        using (GraphicsPath moon = new GraphicsPath())
                        {
                            moon.FillMode = FillMode.Winding;
                            moon.AddEllipse(3.5f, 3.5f, 17, 17);
                            // Traced counter-clockwise so it subtracts from the outer disc.
                            // Offset up and to the right, which leaves a slim crescent.
                            moon.AddArc(11.5f, 0.5f, 17, 17, 0, -180);
                            moon.AddArc(11.5f, 0.5f, 17, 17, 180, -180);
                            g.FillPath(b, moon);
                        }
                        break;
                    case UiIcon.Sun:
                        g.DrawEllipse(p, 7, 7, 10, 10);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            float x1 = 12 + (float)(7.5 * Math.Cos(a));
                            float y1 = 12 + (float)(7.5 * Math.Sin(a));
                            float x2 = 12 + (float)(10.3 * Math.Cos(a));
                            float y2 = 12 + (float)(10.3 * Math.Sin(a));
                            g.DrawLine(p, x1, y1, x2, y2);
                        }
                        break;
                    case UiIcon.Info:
                        g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
                        g.DrawLine(p, 12, 11, 12, 17);
                        g.FillEllipse(b, 10.6f, 6.4f, 2.8f, 2.8f);
                        break;
                    case UiIcon.Server:
                        g.DrawRectangle(p, 3, 4, 18, 7);
                        g.DrawRectangle(p, 3, 13, 18, 7);
                        g.FillEllipse(b, 6, 6.6f, 2.2f, 2.2f);
                        g.FillEllipse(b, 6, 15.6f, 2.2f, 2.2f);
                        g.DrawLine(p, 10, 7.5f, 18, 7.5f);
                        g.DrawLine(p, 10, 16.5f, 18, 16.5f);
                        break;
                    case UiIcon.Bolt:
                        g.FillPolygon(b, new PointF[] { new PointF(13, 2), new PointF(5, 13), new PointF(11, 13), new PointF(10, 22), new PointF(19, 10), new PointF(13, 10) });
                        break;
                    case UiIcon.Copy:
                        g.DrawRectangle(p, 8, 8, 13, 13);
                        g.DrawLines(p, new PointF[] { new PointF(16, 5), new PointF(4, 5), new PointF(4, 17) });
                        break;
                    case UiIcon.Help:
                        // The question mark is drawn as strokes rather than with a font:
                        // a scaled DrawString produced a double-exposed, colour-fringed
                        // glyph at 16px.
                        g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
                        g.DrawArc(p, new RectangleF(7.5f, 7, 9, 8), 200, 260);
                        g.DrawLine(p, 12, 14.4f, 12, 16.4f);
                        g.FillEllipse(b, 10.4f, 18.2f, 3.2f, 3.2f);
                        break;
                    case UiIcon.Gear:
                        g.DrawEllipse(p, 7.5f, 7.5f, 9, 9);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            float x1 = 12 + (float)(7.4 * Math.Cos(a));
                            float y1 = 12 + (float)(7.4 * Math.Sin(a));
                            float x2 = 12 + (float)(10.2 * Math.Cos(a));
                            float y2 = 12 + (float)(10.2 * Math.Sin(a));
                            g.DrawLine(p, x1, y1, x2, y2);
                        }
                        break;
                    case UiIcon.Link:
                        g.DrawLines(p, new PointF[] { new PointF(10, 7), new PointF(8, 7), new PointF(8, 12), new PointF(10, 12) });
                        g.DrawLines(p, new PointF[] { new PointF(14, 12), new PointF(16, 12), new PointF(16, 7), new PointF(14, 7) });
                        g.DrawLine(p, 10, 12, 14, 7);
                        break;
                    case UiIcon.Chevron:
                        g.DrawLines(p, new PointF[] { new PointF(9, 5), new PointF(15, 12), new PointF(9, 19) });
                        break;
                    case UiIcon.Shield:
                        g.DrawLines(p, new PointF[] { new PointF(12, 2.5f), new PointF(20.5f, 5.5f), new PointF(20.5f, 12), new PointF(12, 21.5f), new PointF(3.5f, 12), new PointF(3.5f, 5.5f), new PointF(12, 2.5f) });
                        g.DrawLines(p, new PointF[] { new PointF(8.5f, 12), new PointF(11, 14.5f), new PointF(16, 9) });
                        break;
                    case UiIcon.Grid:
                        g.DrawRectangle(p, 3, 3, 8, 8);
                        g.DrawRectangle(p, 13, 3, 8, 8);
                        g.DrawRectangle(p, 3, 13, 8, 8);
                        g.DrawRectangle(p, 13, 13, 8, 8);
                        break;
                    case UiIcon.Key:
                        g.DrawEllipse(p, 4, 8, 8, 8);
                        g.DrawLine(p, 11, 12, 21, 12);
                        g.DrawLine(p, 17, 12, 17, 16);
                        g.DrawLine(p, 20, 12, 20, 15);
                        break;
                    case UiIcon.Palette:
                        // Colour wheel: an outline ring with four pigment dots.
                        g.DrawEllipse(p, 3, 3, 18, 18);
                        g.FillEllipse(b, 7.2f, 7.2f, 3.6f, 3.6f);
                        g.FillEllipse(b, 13.2f, 6.6f, 3.6f, 3.6f);
                        g.FillEllipse(b, 15.4f, 12.4f, 3.6f, 3.6f);
                        g.FillEllipse(b, 9.2f, 14.2f, 3.6f, 3.6f);
                        break;
                    case UiIcon.Logo:
                        // Full mark: coloured tile plus the glyph. Callers that already
                        // paint their own tile must use DrawLogoMark instead, otherwise
                        // the two tiles stack up and the mark reads as a blurry blob.
                        using (SolidBrush tile = new SolidBrush(Accent))
                            FillRound(g, tile, new Rectangle(0, 0, 24, 24), 3);
                        DrawLogoGlyph(g, Color.White, 1.8f / scale);
                        break;
                }
            }
            g.Restore(state);
        }

        /// <summary>
        /// The logo glyph on its own, without the surrounding tile: a monitor with a
        /// connection arrow inside. Used where the tile is painted by the caller.
        /// </summary>
        public static void DrawLogoMark(Graphics g, Rectangle bounds, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(bounds.Left, bounds.Top);
            float scale = Math.Min(bounds.Width, bounds.Height) / 24f;
            g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
            DrawLogoGlyph(g, color, 1.8f / scale);
            g.Restore(state);
        }

        private static void DrawLogoGlyph(Graphics g, Color color, float width)
        {
            // A monitor outline with a small connection arrow. Kept to three strokes so it
            // stays legible at 14px - more detail than that turns into a smudge.
            using (Pen pen = new Pen(color, width))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawRectangle(pen, 4, 6.5f, 16, 11);
                g.DrawLine(pen, 9.5f, 20, 14.5f, 20);
            }
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPolygon(brush, new PointF[] { new PointF(9, 9.5f), new PointF(15, 12), new PointF(9, 14.5f) });
        }

        public static Bitmap IconBitmap(UiIcon icon, int size, Color color)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawIcon(g, icon, new Rectangle(0, 0, size, size), color);
            }
            return bmp;
        }

        // ---- control styling ---------------------------------------------
        public static void StyleButton(Button b, RhButtonKind kind)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.Font = NewF(9F, kind == RhButtonKind.Primary ? FontStyle.Bold : FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 0;
            b.Tag = "rhbtn|" + kind.ToString();
            b.Paint += delegate(object s, PaintEventArgs e)
            {
                Button bb = (Button)s;
                RhButtonKind k = RhButtonKind.Ghost;
                string t = bb.Tag as string;
                if (t != null && t.StartsWith("rhbtn|"))
                {
                    try { k = (RhButtonKind)Enum.Parse(typeof(RhButtonKind), t.Substring(6)); }
                    catch { }
                }
                PaintPlainButton(e.Graphics, bb, k);
            };
            b.BackColor = Card;
            b.ForeColor = Text;
            b.Invalidate();
        }

        internal static void PaintPlainButton(Graphics g, Button b, RhButtonKind kind)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(ResolveBackColor(b));
            Rectangle r = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
            bool hot = b.ClientRectangle.Contains(b.PointToClient(Cursor.Position)) && b.Enabled;
            Color bg;
            Color fg;
            if (!b.Enabled)
            {
                bg = CardAlt; fg = TextMuted;
            }
            else if (kind == RhButtonKind.Primary)
            {
                bg = hot ? AccentHover : Accent; fg = Color.White;
            }
            else if (kind == RhButtonKind.Danger)
            {
                bg = hot ? Danger : Color.FromArgb(Math.Max(0, Danger.R - 18), Danger.G, Danger.B);
                fg = Color.White;
            }
            else if (kind == RhButtonKind.Subtle)
            {
                bg = hot ? SidebarHover : SidebarAlt; fg = SidebarText;
            }
            else if (kind == RhButtonKind.Chip)
            {
                bg = hot ? AccentSoft : ResolveBackColor(b); fg = hot ? Accent : Text;
            }
            else
            {
                bg = hot ? AccentSoft : Card; fg = hot ? Accent : Text;
            }
            using (SolidBrush brush = new SolidBrush(bg)) FillRound(g, brush, r, Radius);
            if (kind == RhButtonKind.Ghost && b.Enabled)
                using (Pen pen = new Pen(hot ? Accent : BorderSoft)) DrawRound(g, pen, r, Radius);
            DrawCentered(g, b.Text, F(9F, kind == RhButtonKind.Primary ? FontStyle.Bold : FontStyle.Regular), fg, r);
        }

        public static void StyleField(Control c)
        {
            c.ForeColor = Text;
            c.BackColor = Field;
            c.Font = NewF(9F);
            if (c is TextBox)
            {
                TextBox t = (TextBox)c;
                t.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (c is ComboBox)
            {
                ComboBox cb = (ComboBox)c;
                cb.FlatStyle = FlatStyle.Flat;
            }
            else if (c is NumericUpDown)
            {
                NumericUpDown n = (NumericUpDown)c;
                n.BorderStyle = BorderStyle.FixedSingle;
            }
            c.Tag = "field";
        }

        public static void StyleCheck(RhCheck c)
        {
            c.BackColor = Color.Transparent;
            c.Font = NewF(9F);
            c.Tag = "check";
        }

        public static void StyleLabel(Label l, string role)
        {
            l.BackColor = Color.Transparent;
            l.Font = NewF(9F);
            l.Tag = role;
            switch (role)
            {
                case "muted": l.ForeColor = TextMuted; break;
                case "accent": l.ForeColor = Accent; break;
                case "sidetext": l.ForeColor = SidebarText; break;
                case "sidemuted": l.ForeColor = SidebarMuted; break;
                default: l.ForeColor = Text; break;
            }
        }

        /// <summary>
        /// Recursively repaints controls according to their Tag role. Colours are always read
        /// from the current palette, so calling this after a theme switch updates everything.
        /// </summary>
        public static void ApplyTheme(Control root)
        {
            ApplyOne(root);
            try
            {
                foreach (Control c in root.Controls) ApplyTheme(c);
            }
            catch { }
            root.Invalidate(true);
        }

        private static void ApplyOne(Control c)
        {
            if (c is RhButton) { c.Invalidate(); return; }
            if (c is RhSelect) { c.Invalidate(); return; }
            if (c is RhNumber) { c.Invalidate(); return; }
            if (c is RhCheck) { c.Invalidate(); return; }
            string tag = c.Tag as string;
            if (tag != null && tag.StartsWith("rhbtn|")) { c.Invalidate(); return; }
            if (tag == "rhselect")
            {
                if (c is TextBox) { c.BackColor = Field; c.ForeColor = Text; }
                c.Invalidate();
                return;
            }
            if (tag == "plain") { c.Invalidate(); return; }

            switch (tag)
            {
                case "sidebar": c.BackColor = Sidebar; c.ForeColor = SidebarMuted; break;
                case "sidehead": c.BackColor = SidebarAlt; break;
                case "topbar": c.BackColor = TopBar; break;
                case "toolbar": c.BackColor = Card; break;
                case "status": c.BackColor = StatusBar; break;
                case "card": c.BackColor = Card; break;
                case "cardalt": c.BackColor = CardAlt; break;
                case "welcome": c.BackColor = Surface; break;
                case "surface": c.BackColor = Surface; break;
                case "field": c.BackColor = Field; c.ForeColor = Text; break;
                case "fieldwrap": c.BackColor = Field; break;
                case "line": c.BackColor = Border; break;
                case "muted": c.ForeColor = TextMuted; c.BackColor = Color.Transparent; break;
                case "accent": c.ForeColor = Accent; c.BackColor = Color.Transparent; break;
                case "title": c.ForeColor = Text; c.BackColor = Color.Transparent; break;
                case "sidetext": c.ForeColor = SidebarText; c.BackColor = Color.Transparent; break;
                case "sidemuted": c.ForeColor = SidebarMuted; c.BackColor = Color.Transparent; break;
                case "check": c.ForeColor = Text; c.BackColor = Color.Transparent; break;
                case "sidepanel": c.BackColor = Sidebar; break;
                case "sidefield": c.BackColor = SidebarField; c.ForeColor = SidebarText; break;
                default:
                    if (c is TextBox || c is ComboBox || c is NumericUpDown)
                    {
                        c.BackColor = Field; c.ForeColor = Text;
                    }
                    else if (c is TreeView) { c.BackColor = Sidebar; c.ForeColor = SidebarText; }
                    else if (c is TabControl) { c.BackColor = Surface; }
                    break;
            }
            if (c is MenuStrip || c is ToolStrip || c is StatusStrip) c.Invalidate();
        }

        public static void DrawPanelBorder(Graphics g, Rectangle bounds, Color color, int radius)
        {
            using (Pen p = new Pen(color)) DrawRound(g, p, new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), radius);
        }
    }

    /// <summary>Fully custom-drawn flat button with an optional vector icon.</summary>
    internal sealed class RhButton : Button
    {
        private bool _hover;
        private bool _down;
        private UiIcon _icon = UiIcon.None;
        private RhButtonKind _kind = RhButtonKind.Ghost;

        public UiIcon Icon { get { return _icon; } set { _icon = value; Invalidate(); } }
        public RhButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }

        public RhButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.BackColor = Color.Transparent;
            this.Cursor = Cursors.Hand;
            this.Font = Ui.NewF(9F);
            this.Height = 34;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // Clear with the nearest *solid* ancestor background. Using Color.Transparent here
            // paints black on a window surface and leaves square black corners around the radius.
            g.Clear(Ui.ResolveBackColor(this));

            int radius = Ui.Radius;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg;
            Color fg;
            bool hasBg = true;
            bool border = false;
            bool active = _hover && Enabled;
            if (!Enabled)
            {
                bg = Ui.CardAlt; fg = Ui.TextMuted;
            }
            else if (_kind == RhButtonKind.Primary)
            {
                bg = (_down || active) ? Ui.AccentHover : Ui.Accent;
                fg = Color.White;
            }
            else if (_kind == RhButtonKind.Danger)
            {
                bg = active || _down ? Ui.Danger : Color.FromArgb(Math.Max(0, Ui.Danger.R - 16), Ui.Danger.G, Ui.Danger.B);
                fg = Color.White;
            }
            else if (_kind == RhButtonKind.Subtle)
            {
                bg = active || _down ? Ui.SidebarHover : Ui.SidebarAlt;
                fg = Ui.SidebarText;
            }
            else if (_kind == RhButtonKind.Chip)
            {
                // Borderless toolbar button: only the hover state paints a surface.
                bg = Ui.AccentSoft;
                fg = (active || _down) ? Ui.Accent : Ui.Text;
                hasBg = active || _down;
            }
            else if (_kind == RhButtonKind.Outline)
            {
                // Secondary command: visible frame, accent label, no fill until hovered.
                bg = active || _down ? Ui.AccentSoft : Ui.Card;
                fg = Ui.Accent;
                border = true;
            }
            else
            {
                bg = active ? Ui.AccentSoft : Ui.Card;
                fg = active ? Ui.Accent : Ui.Text;
                border = true;
            }

            if (hasBg)
                using (SolidBrush brush = new SolidBrush(bg)) Ui.FillRound(g, brush, r, radius);
            if (border && Enabled)
            {
                Color edge = active ? Ui.Accent
                    : (_kind == RhButtonKind.Outline ? Color.FromArgb(105, Ui.Accent) : Ui.BorderSoft);
                using (Pen pen = new Pen(edge)) Ui.DrawRound(g, pen, r, radius);
            }

            int iconSize = 16;
            int gap = 7;
            int textW = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            int total = (Icon != UiIcon.None ? iconSize + (textW > 0 ? gap : 0) : 0) + textW;
            int x = (Width - total) / 2;
            if (x < 6) x = 6;

            if (Icon != UiIcon.None)
            {
                Ui.DrawIcon(g, Icon, new Rectangle(x, Height / 2 - iconSize / 2, iconSize, iconSize), fg);
                x += iconSize + gap;
            }
            if (textW > 0)
            {
                // Draw the label exactly after the icon. Centring it inside the *remaining*
                // width (the old approach) pushed text away from the icon on wide buttons.
                Rectangle textRect = new Rectangle(x - 1, 0, textW + 4, Height);
                TextRenderer.DrawText(g, Text, Font, textRect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>
    /// Rounded, fully themed drop-down that replaces ComboBox. The stock ComboBox always paints a
    /// square 1px frame, which shows up as a straight line inside every rounded input wrapper.
    /// </summary>
    internal sealed class RhSelect : Control
    {
        private readonly List<string> _items = new List<string>();
        private readonly TextBox _edit;
        private int _index = -1;
        private bool _hover;

        public event EventHandler ValueChanged;

        public RhSelect() : this(false) { }

        public RhSelect(bool editable)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Ui.NewF(9F);
            Height = 28;
            TabStop = true;
            if (editable)
            {
                _edit = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    BackColor = Ui.Field,
                    ForeColor = Ui.Text,
                    Font = Ui.NewF(9F),
                    Tag = "rhselect"
                };
                _edit.TextChanged += delegate { Raise(); };
                Controls.Add(_edit);
            }
        }

        private void Raise()
        {
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        public void SetItems(params string[] items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            if (_index >= _items.Count) _index = _items.Count - 1;
            Invalidate();
        }

        public int ItemCount { get { return _items.Count; } }

        public string ItemAt(int index)
        {
            return (index >= 0 && index < _items.Count) ? _items[index] : "";
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (value < -1) value = -1;
                if (value >= _items.Count) value = _items.Count - 1;
                if (_index == value) return;
                _index = value;
                if (_edit != null && _index >= 0) _edit.Text = _items[_index];
                Invalidate();
                Raise();
            }
        }

        public string Value
        {
            get
            {
                if (_edit != null) return _edit.Text;
                return (_index >= 0 && _index < _items.Count) ? _items[_index] : "";
            }
            set
            {
                string v = value == null ? "" : value;
                if (_edit != null)
                {
                    if (_edit.Text != v) _edit.Text = v;
                    return;
                }
                SelectedIndex = _items.IndexOf(v);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_edit != null)
                _edit.SetBounds(11, Math.Max(1, (Height - 18) / 2), Math.Max(10, Width - 40), 18);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) ShowPopup();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.F4 || (e.KeyCode == Keys.Space && _edit == null))
            {
                ShowPopup();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void ShowPopup()
        {
            if (_items.Count == 0) return;
            ContextMenuStrip m = new ContextMenuStrip
            {
                Renderer = new UiToolStripRenderer(),
                Font = Ui.NewF(9F),
                ShowImageMargin = false
            };
            m.MinimumSize = new Size(Math.Max(140, Width), 0);
            for (int i = 0; i < _items.Count; i++)
            {
                int idx = i;
                ToolStripMenuItem it = new ToolStripMenuItem(_items[i]);
                if (i == _index) it.Font = Ui.F(9F, FontStyle.Bold);
                it.Click += delegate { SelectedIndex = idx; };
                m.Items.Add(it);
            }
            m.Closed += delegate { try { m.Dispose(); } catch { } };
            m.Show(this, new Point(0, Height + 3));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Ui.ResolveBackColor(this));

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(Ui.Field)) Ui.FillRound(g, b, r, Ui.Radius);
            using (Pen p = new Pen(_hover ? Ui.Accent : Ui.Border)) Ui.DrawRound(g, p, r, Ui.Radius);

            if (_edit == null)
            {
                string text = (_index >= 0 && _index < _items.Count) ? _items[_index] : "";
                TextRenderer.DrawText(g, text, Font,
                    new Rectangle(11, 0, Math.Max(10, Width - 36), Height), Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }

            int cx = Width - 19, cy = Height / 2;
            using (Pen p = new Pen(_hover ? Ui.Accent : Ui.TextMuted, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2)
                });
            }
        }
    }

    /// <summary>
    /// Self-drawn spin box. The stock NumericUpDown always stamps its own square
    /// grey stepper in the corner, which clashes badly with the rounded input frame
    /// around it - the clearest sign of a mixed, unpolished UI.
    /// </summary>
    internal sealed class RhNumber : Control
    {
        private int _value;
        private int _minimum;
        private int _maximum = 100;
        private bool _hoverUp, _hoverDown;
        private bool _pressUp, _pressDown;
        private int _increment = 1;

        public event EventHandler ValueChanged;

        public RhNumber()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Default;
            Font = Ui.NewF(9F);
            Height = 28;
            TabStop = true;
        }

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; if (_value < value) _value = value; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = value; if (_value > value) _value = value; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < _minimum) v = _minimum;
                if (v > _maximum) v = _maximum;
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>How much one step of the arrow keys / wheel changes the value.</summary>
        public int Increment
        {
            get { return _increment < 1 ? 1 : _increment; }
            set { _increment = value < 1 ? 1 : value; }
        }

        private Rectangle StepperRect
        {
            get { return new Rectangle(Width - 24, 0, 24, Height); }
        }

        private Rectangle UpRect
        {
            get { Rectangle s = StepperRect; return new Rectangle(s.Left, 0, s.Width, Height / 2); }
        }

        private Rectangle DownRect
        {
            get { Rectangle s = StepperRect; return new Rectangle(s.Left, Height / 2, s.Width, Height - Height / 2); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hoverUp = UpRect.Contains(e.Location);
            _hoverDown = DownRect.Contains(e.Location);
            Cursor = (_hoverUp || _hoverDown) ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverUp = _hoverDown = false;
            _pressUp = _pressDown = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (UpRect.Contains(e.Location)) { _pressUp = true; Invalidate(); }
                else if (DownRect.Contains(e.Location)) { _pressDown = true; Invalidate(); }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressUp && UpRect.Contains(e.Location)) Value = _value + Increment;
            if (_pressDown && DownRect.Contains(e.Location)) Value = _value - Increment;
            _pressUp = _pressDown = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value = _value + (e.Delta > 0 ? Increment : -Increment);
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up) { Value = _value + Increment; return true; }
            if (keyData == Keys.Down) { Value = _value - Increment; return true; }
            return base.IsInputKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Ui.ResolveBackColor(this));

            bool on = Enabled;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(on ? Ui.Field : Ui.CardAlt)) Ui.FillRound(g, b, r, Ui.Radius);
            using (Pen p = new Pen(on ? Ui.Border : Ui.BorderSoft)) Ui.DrawRound(g, p, r, Ui.Radius);

            TextRenderer.DrawText(g, _value.ToString(), Font,
                new Rectangle(9, 0, Math.Max(10, Width - 34), Height), on ? Ui.Text : Ui.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine);

            if (!on) return;

            // Two small chevrons stacked on the right, matching the select control.
            int cx = Width - 12;
            int cy = Height / 2;
            using (Pen p = new Pen(Ui.TextMuted, 1.4f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[]
                {
                    new PointF(cx - 4, cy - 6), new PointF(cx, cy - 10), new PointF(cx + 4, cy - 6)
                });
                g.DrawLines(p, new PointF[]
                {
                    new PointF(cx - 4, cy + 6), new PointF(cx, cy + 10), new PointF(cx + 4, cy + 6)
                });
            }
        }
    }

    /// <summary>
    /// Self-drawn check box. The stock control paints a square system-blue glyph that
    /// belongs to no theme in particular, so a dialog ends up with two visual languages
    /// side by side.
    /// </summary>
    internal sealed class RhCheck : Control
    {
        private bool _checked;
        private bool _hover;
        private bool _press;

        public event EventHandler CheckedChanged;

        public RhCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Ui.NewF(9F);
            Height = 20;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public string Caption
        {
            get { return Text; }
            set { Text = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool inside = ClientRectangle.Contains(e.Location);
            _press = false;
            if (inside && Enabled) Checked = !_checked;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !_checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Ui.ResolveBackColor(this));

            const int Box = 14;
            int cy = (Height - Box) / 2;
            Rectangle box = new Rectangle(0, cy, Box, Box);

            // Hover and press tint the empty box towards the accent so the control
            // reacts before the click lands.
            bool hot = Enabled && (_hover || _press);
            Ui.DrawCheckGlyph(g, box, _checked, Enabled, hot);

            if (!string.IsNullOrEmpty(Text))
            {
                TextRenderer.DrawText(g, Text, Font,
                    new Rectangle(Box + 7, 0, Math.Max(10, Width - Box - 7), Height),
                    Enabled ? Ui.Text : Ui.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>
    /// TabControl repaints its own square frame with a fixed dark colour. Overpainting the
    /// non-client edge with our border colour keeps the workspace free of stray hard lines.
    /// </summary>
    internal sealed class RhTabControl : TabControl
    {
        /// <summary>
        /// The whole strip is painted by the owner. The stock control stamps its own tab
        /// separators *after* DrawItem, which leaves a stray 1px line beside every pill,
        /// so we take the painting over completely.
        /// </summary>
        public Action<Graphics> PaintStrip;

        public RhTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Nothing of the native strip is reused: background + pills + hairline only.
            if (PaintStrip != null) PaintStrip(e.Graphics);
            else e.Graphics.Clear(Ui.Surface);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0085 /*WM_NCPAINT*/)
            {
                try
                {
                    using (Graphics g = Graphics.FromHwnd(Handle))
                    using (Pen p = new Pen(Ui.Border))
                    {
                        g.DrawLine(p, 0, Height - 1, Width - 1, Height - 1);
                        g.DrawLine(p, 0, 0, 0, Height - 1);
                        g.DrawLine(p, Width - 1, 0, Width - 1, Height - 1);
                    }
                }
                catch (Exception ex) { Program.Log("TABWND", ex); }
            }
        }
    }

    /// <summary>Modern colour table; values are read live so theme switching needs no rebuild.</summary>
    internal sealed class UiColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Ui.Border; } }
        public override Color MenuItemBorder { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelected { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelectedGradientBegin { get { return Ui.AccentSoft; } }
        public override Color MenuItemSelectedGradientEnd { get { return Ui.AccentSoft; } }
        public override Color MenuItemPressedGradientBegin { get { return Ui.AccentSoft; } }
        public override Color MenuItemPressedGradientEnd { get { return Ui.AccentSoft; } }
        public override Color MenuItemPressedGradientMiddle { get { return Ui.AccentSoft; } }
        public override Color MenuStripGradientBegin { get { return Ui.TopBar; } }
        public override Color MenuStripGradientEnd { get { return Ui.TopBar; } }
        public override Color ToolStripDropDownBackground { get { return Ui.Card; } }
        public override Color ImageMarginGradientBegin { get { return Ui.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Ui.Card; } }
        public override Color ImageMarginGradientEnd { get { return Ui.Card; } }
        public override Color ToolStripGradientBegin { get { return Ui.Card; } }
        public override Color ToolStripGradientMiddle { get { return Ui.Card; } }
        public override Color ToolStripGradientEnd { get { return Ui.Card; } }
        public override Color SeparatorDark { get { return Ui.Border; } }
        public override Color SeparatorLight { get { return Ui.Card; } }
        public override Color CheckBackground { get { return Ui.AccentSoft; } }
        public override Color CheckSelectedBackground { get { return Ui.AccentSoft; } }
        public override Color CheckPressedBackground { get { return Ui.AccentSoft; } }
        public override Color ButtonSelectedBorder { get { return Ui.AccentSoft; } }
        public override Color ButtonSelectedGradientBegin { get { return Ui.AccentSoft; } }
        public override Color ButtonSelectedGradientEnd { get { return Ui.AccentSoft; } }
        public override Color ButtonPressedBorder { get { return Ui.AccentSoft; } }
        public override Color GripDark { get { return Ui.Border; } }
        public override Color GripLight { get { return Ui.Card; } }
    }

    internal sealed class UiToolStripRenderer : ToolStripProfessionalRenderer
    {
        public UiToolStripRenderer() : base(new UiColorTable()) { }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(e.ToolStrip is MenuStrip ? Ui.TopBar : Ui.Card))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Ui.Border))
                e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Ui.TextMuted : (e.Item.Selected ? Ui.Accent : Ui.Text);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Ui.Border))
                e.Graphics.DrawLine(p, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Ui.TextMuted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = new Rectangle(4, 3, 16, 16);
            using (SolidBrush b = new SolidBrush(Ui.AccentSoft)) Ui.FillRound(e.Graphics, b, r, 4);
            Ui.DrawIcon(e.Graphics, UiIcon.Check, new Rectangle(6, 5, 12, 12), Ui.Accent, 2.4f);
        }
    }
}
