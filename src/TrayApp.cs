using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenCodeGoUsage
{
    /// <summary>单个额度窗口。</summary>
    class WindowData
    {
        public string Key;
        public string Label;
        public int Percent;
        public string Status;
        public DateTime? Reset;
    }

    /// <summary>一套配色。</summary>
    class Theme
    {
        public bool Dark;
        public Color Bg, Border, Ink, Label, Mute, Track, Pill, Button, ButtonText;

        public static Theme Light = new Theme
        {
            Dark = false,
            Bg = ColorTranslator.FromHtml("#F7F8FA"),
            Border = ColorTranslator.FromHtml("#E3E6EB"),
            Ink = ColorTranslator.FromHtml("#111827"),
            Label = ColorTranslator.FromHtml("#4B5563"),
            Mute = ColorTranslator.FromHtml("#9CA3AF"),
            Track = ColorTranslator.FromHtml("#E4E7EC"),
            Pill = ColorTranslator.FromHtml("#EDEFF3"),
            Button = ColorTranslator.FromHtml("#111827"),
            ButtonText = Color.White,
        };

        public static Theme DarkTh = new Theme
        {
            Dark = true,
            Bg = ColorTranslator.FromHtml("#1C1D22"),
            Border = ColorTranslator.FromHtml("#2E3038"),
            Ink = ColorTranslator.FromHtml("#E8E9ED"),
            Label = ColorTranslator.FromHtml("#B4B7C1"),
            Mute = ColorTranslator.FromHtml("#7C7F8B"),
            Track = ColorTranslator.FromHtml("#2E3037"),
            Pill = ColorTranslator.FromHtml("#2A2C34"),
            Button = ColorTranslator.FromHtml("#33353F"),
            ButtonText = ColorTranslator.FromHtml("#E8E9ED"),
        };

        public static string LoadMode()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\OpenCodeGoUsage"))
            {
                string v = k == null ? null : k.GetValue("theme") as string;
                if (v == "light" || v == "dark") return v;
                return "auto";
            }
        }

        /// <summary>Windows「应用模式」：浅色为 1，深色为 0。</summary>
        public static bool SystemIsDark()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                if (v is int) return (int)v == 0;
            }
            return false;
        }

        public static Theme Resolve(string mode)
        {
            if (mode == "dark") return DarkTh;
            if (mode == "light") return Light;
            return SystemIsDark() ? DarkTh : Light;
        }

        public static void SaveMode(string mode)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\OpenCodeGoUsage"))
                k.SetValue("theme", mode);
        }
    }

    static class Brand
    {
        static Bitmap cached;

        /// <summary>嵌入的 opencode logo，按需缩放到指定边长。</summary>
        public static Bitmap Logo(int size)
        {
            if (cached == null)
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("OpenCodeGoUsage.logo.png"))
                {
                    if (s != null) cached = new Bitmap(s);
                }
            }
            if (cached == null) return null;

            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(cached, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }
    }

    static class Usage
    {
        const string Endpoint = "https://opencode.ai/zen/go/v1/usage";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "OpenCodeGoUsage";

        public static string ReadKey()
        {
            string env = Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();

            string file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", ".credentials.yaml");
            if (!File.Exists(file)) return null;

            string text = File.ReadAllText(file);
            Match m = Regex.Match(text, @"^\s+OPENCODE_GO_API_KEY:\s*(\S+)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>HTTPS_PROXY / HTTP_PROXY 环境变量，大小写都认。</summary>
        static string EnvProxy()
        {
            string[] names = new string[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" };
            foreach (string n in names)
            {
                string v = Environment.GetEnvironmentVariable(n);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        static HttpClient Client(bool useProxy)
        {
            HttpClientHandler handler = new HttpClientHandler();
            if (!useProxy) handler.UseProxy = false;
            else
            {
                // 有环境变量就用它，否则留给 .NET 用系统代理设置
                string env = EnvProxy();
                if (!string.IsNullOrEmpty(env)) handler.Proxy = new WebProxy(env);
            }
            HttpClient c = new HttpClient(handler);
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            return c;
        }

        static string TryGet(string key, bool useProxy, out string error)
        {
            error = null;
            try
            {
                using (HttpClient c = Client(useProxy))
                {
                    c.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
                    HttpResponseMessage r = c.GetAsync(Endpoint).Result;
                    string body = r.Content.ReadAsStringAsync().Result;
                    if (!r.IsSuccessStatusCode)
                    {
                        error = "HTTP " + (int)r.StatusCode;
                        if ((int)r.StatusCode == 401 || (int)r.StatusCode == 403)
                            error += "（密钥失效或无 Go 套餐权限）";
                        return null;
                    }
                    return body;
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                error = inner.Message;
                return null;
            }
        }

        public static List<WindowData> Fetch(out string error)
        {
            error = null;
            string key = ReadKey();
            if (string.IsNullOrEmpty(key))
            {
                error = "找不到 OPENCODE_GO_API_KEY";
                return null;
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // 有代理就先走代理，不会再退回直连；没有代理则直连。两条都失败才报错。
            bool useProxy = false;
            try
            {
                IWebProxy sys = WebRequest.GetSystemWebProxy();
                useProxy = !string.IsNullOrEmpty(EnvProxy())
                    || (sys != null && !sys.IsBypassed(new Uri(Endpoint)));
            }
            catch { }

            string body = TryGet(key, useProxy, out error);
            if (body == null)
            {
                string retryError;
                body = TryGet(key, !useProxy, out retryError);
                if (body != null) error = null;
                else error = (useProxy ? "代理" : "直连") + "失败：" + error
                    + "；" + (useProxy ? "直连" : "代理") + "也失败：" + retryError;
            }
            if (body == null) return null;

            List<WindowData> list = new List<WindowData>();
            string[][] specs = new string[][] {
                new string[] { "rolling", "5 小时" },
                new string[] { "weekly",  "本周" },
                new string[] { "monthly", "本月" }
            };

            foreach (string[] spec in specs)
            {
                Match m = Regex.Match(body,
                    "\"" + spec[0] + "\"\\s*:\\s*\\{(.*?)\\}", RegexOptions.Singleline);
                if (!m.Success) continue;

                WindowData w = new WindowData();
                w.Key = spec[0];
                w.Label = spec[1];

                Match p = Regex.Match(m.Groups[1].Value, "\"percent\"\\s*:\\s*(\\d+)");
                w.Percent = p.Success ? int.Parse(p.Groups[1].Value) : 0;

                Match s = Regex.Match(m.Groups[1].Value, "\"status\"\\s*:\\s*\"([^\"]*)\"");
                w.Status = s.Success ? s.Groups[1].Value : "?";

                Match t = Regex.Match(m.Groups[1].Value, "\"resetsAt\"\\s*:\\s*\"([^\"]*)\"");
                if (t.Success)
                {
                    DateTime parsed;
                    if (DateTime.TryParse(t.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                        w.Reset = parsed.ToLocalTime();
                }
                list.Add(w);
            }

            if (list.Count == 0)
            {
                error = "响应里没有可识别的窗口字段";
                return null;
            }
            return list;
        }

        public static bool AutoStart
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
        }

        public static Color Accent(int percent)
        {
            if (percent >= 90) return ColorTranslator.FromHtml("#EF4444");
            if (percent >= 60) return ColorTranslator.FromHtml("#F59E0B");
            return ColorTranslator.FromHtml("#3B82F6");
        }

        public static string Countdown(DateTime? reset)
        {
            if (!reset.HasValue) return "-";
            TimeSpan left = reset.Value - DateTime.Now;
            if (left.TotalSeconds < 0) left = TimeSpan.Zero;
            if (left.TotalDays >= 1)
                return string.Format("{0} 天 {1} 小时后重置", (int)left.TotalDays, left.Hours);
            return string.Format("{0} 小时 {1:D2} 分后重置", left.Hours, left.Minutes);
        }
    }

    /// <summary>跟随面板主题的下拉菜单渲染器。</summary>
    class MenuSkin : ToolStripProfessionalRenderer
    {
        readonly Theme skin;

        public bool SkinDark { get { return skin.Dark; } }

        public MenuSkin(Theme t) : base(new MenuColorTable()) { skin = t; }

        class MenuColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.Transparent; } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color ToolStripDropDownBackground { get { return Color.Transparent; } }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(skin.Dark
                ? ColorTranslator.FromHtml("#23252C")
                : ColorTranslator.FromHtml("#FFFFFF")))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(skin.Dark
                ? ColorTranslator.FromHtml("#23252C")
                : ColorTranslator.FromHtml("#FFFFFF"));
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (Pen p = new Pen(skin.Border))
                e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            Rectangle r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (SolidBrush b = new SolidBrush(skin.Dark
                ? ColorTranslator.FromHtml("#2F323B")
                : ColorTranslator.FromHtml("#F1F3F6")))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? skin.Ink : skin.Mute;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            using (SolidBrush bg = new SolidBrush(skin.Dark
                ? ColorTranslator.FromHtml("#23252C")
                : ColorTranslator.FromHtml("#FFFFFF")))
                e.Graphics.FillRectangle(bg, r);
            using (SolidBrush b = new SolidBrush(skin.Dark
                ? ColorTranslator.FromHtml("#8AB4FF")
                : ColorTranslator.FromHtml("#3B82F6")))
                e.Graphics.DrawString("✓", new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                    b, r.X - 2, r.Y + 1);
        }
    }
    class Panel : Form
    {
        public List<WindowData> Data;
        public string Error;
        public DateTime Updated;
        public string Mode = "auto";
        public Theme Skin = Theme.Light;

        const int W = 420;
        const int H = 320;

        Rectangle closeRect = new Rectangle(384, 16, 22, 22);
        Rectangle themeRect = new Rectangle(288, 14, 86, 26);
        Rectangle refreshRect = new Rectangle(324, 274, 78, 30);

        public bool Busy;
        ContextMenuStrip modeMenu;
        public float spinAngle;
        System.Windows.Forms.Timer spinTimer;
        int hover;
        int press;
        bool dragging;
        Point dragStart;

        public Panel()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(W, H);
            DoubleBuffered = true;
            BackColor = Theme.Light.Bg;
            SetStyle(ControlStyles.ResizeRedraw, true);
            using (GraphicsPath path = Round(0, 0, W, H, 14))
                Region = new Region(path);

            spinTimer = new System.Windows.Forms.Timer();
            spinTimer.Interval = 40;
            spinTimer.Tick += delegate
            {
                spinAngle = (spinAngle + 30) % 360;
                Invalidate(refreshRect);
            };
        }

        public void ApplyMode(string mode)
        {
            Mode = mode;
            Skin = Theme.Resolve(mode);
            BackColor = Skin.Bg;
            Invalidate();
        }

        /// <summary>刷新期间按钮显示转圈。</summary>
        public void SetBusy(bool busy)
        {
            Busy = busy;
            if (busy)
            {
                spinAngle = 0;
                spinTimer.Start();
            }
            else
            {
                spinTimer.Stop();
            }
            Invalidate(refreshRect);
        }

        public static GraphicsPath Round(int x, int y, int w, int h, int r)
        {
            GraphicsPath p = new GraphicsPath();
            int d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        int HitTest(Point p)
        {
            if (closeRect.Contains(p)) return 2;
            if (themeRect.Contains(p)) return 1;
            if (refreshRect.Contains(p)) return 3;
            return 0;
        }

        static Color Shift(Color c, int delta)
        {
            if (delta >= 0)
                return Color.FromArgb(Math.Min(255, c.R + delta), Math.Min(255, c.G + delta), Math.Min(255, c.B + delta));
            return Color.FromArgb(Math.Max(0, c.R + delta), Math.Max(0, c.G + delta), Math.Max(0, c.B + delta));
        }

        Color Surface(Color baseColor, int target)
        {
            int d = Skin.Dark ? 14 : -8;
            if (press == target) return Shift(baseColor, d * 2);
            if (hover == target) return Shift(baseColor, d);
            return baseColor;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Skin.Bg);

            Font fTitle = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            Font fSmall = new Font("Microsoft YaHei UI", 8.5f);
            Font fLabel = new Font("Microsoft YaHei UI", 9.5f);
            Font fPct = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);

            using (Pen border = new Pen(Skin.Border))
            using (GraphicsPath bp = Round(0, 0, W - 1, H - 1, 14))
                g.DrawPath(border, bp);

            using (Bitmap logo = Brand.Logo(30))
            {
                if (logo != null) g.DrawImage(logo, new Rectangle(18, 12, 30, 30));
            }
            TextRenderer.DrawText(g, "opencode Go", fTitle, new Point(58, 18), Skin.Ink);

            // 主题按钮
            using (SolidBrush tb = new SolidBrush(Surface(Skin.Pill, 1)))
            using (GraphicsPath tp = Round(themeRect.X, themeRect.Y, themeRect.Width, themeRect.Height, 13))
                g.FillPath(tb, tp);
            DrawModeGlyph(g, new Rectangle(themeRect.X + 12, themeRect.Y + 8, 13, 12), Skin.Mute);
            TextRenderer.DrawText(g, ModeLabel(), fSmall,
                new Rectangle(themeRect.X + 29, themeRect.Y, themeRect.Width - 50, themeRect.Height),
                Skin.Mute, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "⌄", fSmall,
                new Rectangle(themeRect.Right - 20, themeRect.Y - 1, 14, themeRect.Height),
                Skin.Mute, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 关闭按钮
            using (SolidBrush cb = new SolidBrush(Surface(Skin.Pill, 2)))
            using (GraphicsPath cp = Round(closeRect.X, closeRect.Y, closeRect.Width, closeRect.Height, 11))
                g.FillPath(cb, cp);
            TextRenderer.DrawText(g, "✕", fSmall, closeRect, hover == 2 ? Skin.Ink : Skin.Mute,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            if (Data == null)
            {
                string msg = Error == null ? "加载中…" : "查询失败：" + Error;
                TextRenderer.DrawText(g, msg, fLabel, new Rectangle(20, 90, W - 40, 120),
                    ColorTranslator.FromHtml("#EF4444"), TextFormatFlags.WordBreak);
            }
            else
            {
                int y = 62;
                foreach (WindowData w in Data)
                {
                    Color acc = Usage.Accent(w.Percent);
                    TextRenderer.DrawText(g, w.Label, fLabel, new Point(20, y), Skin.Label);
                    TextRenderer.DrawText(g, w.Percent + "%", fPct,
                        new Rectangle(20, y - 4, W - 40, 26), acc, TextFormatFlags.Right);

                    Rectangle bar = new Rectangle(20, y + 26, W - 40, 10);
                    using (GraphicsPath tp = Round(bar.X, bar.Y, bar.Width, bar.Height, 5))
                    using (SolidBrush tb = new SolidBrush(Skin.Track))
                        g.FillPath(tb, tp);

                    int fill = (int)(bar.Width * Math.Max(0, Math.Min(100, w.Percent)) / 100.0);
                    if (fill > 4)
                    {
                        using (GraphicsPath fp = Round(bar.X, bar.Y, fill, bar.Height, 5))
                        using (LinearGradientBrush fb = new LinearGradientBrush(
                            new Rectangle(bar.X, bar.Y, Math.Max(fill, 8), bar.Height),
                            Shift(acc, 38), acc, LinearGradientMode.Horizontal))
                            g.FillPath(fb, fp);
                    }

                    TextRenderer.DrawText(g, Usage.Countdown(w.Reset), fSmall, new Point(20, y + 42), Skin.Mute);
                    y += 68;
                }
            }

            using (Pen pen = new Pen(Skin.Border))
                g.DrawLine(pen, 20, 262, W - 20, 262);

            string stamp = Data == null ? "—" : "更新 " + Updated.ToString("HH:mm:ss");
            TextRenderer.DrawText(g, stamp, fSmall, new Point(20, 282), Skin.Mute);

            using (SolidBrush rb = new SolidBrush(Surface(Skin.Button, 3)))
            using (GraphicsPath rp = Round(refreshRect.X, refreshRect.Y, refreshRect.Width, refreshRect.Height, 8))
                g.FillPath(rb, rp);

            if (Busy)
            {
                float cx = refreshRect.X + refreshRect.Width / 2f;
                float cy = refreshRect.Y + refreshRect.Height / 2f;
                using (Pen sp = new Pen(Color.FromArgb(230, Skin.ButtonText), 2.2f))
                {
                    sp.StartCap = LineCap.Round;
                    sp.EndCap = LineCap.Round;
                    g.DrawArc(sp, cx - 7, cy - 7, 14, 14, spinAngle, 285);
                }
            }
            else
            {
                TextRenderer.DrawText(g, "刷新", fSmall, refreshRect, Skin.ButtonText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            fTitle.Dispose(); fSmall.Dispose(); fLabel.Dispose(); fPct.Dispose();
        }

        string ModeLabel()
        {
            if (Mode == "light") return "浅色";
            if (Mode == "dark") return "深色";
            return "自动";
        }

        /// <summary>按当前模式画小图标：自动=半明半暗，浅色=太阳，深色=月牙。</summary>
        void DrawModeGlyph(Graphics g, Rectangle r, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            using (Pen pen = new Pen(color, 1.3f))
            {
                if (Mode == "auto")
                {
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        p.AddPie(r.X, r.Y, r.Width, r.Height, 90, 180);
                        g.FillPath(brush, p);
                    }
                    g.DrawEllipse(pen, r);
                }
                else if (Mode == "light")
                {
                    float cx = r.X + r.Width / 2f - 0.5f;
                    float cy = r.Y + r.Height / 2f;
                    g.DrawEllipse(pen, cx - 2.4f, cy - 2.4f, 4.8f, 4.8f);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * i / 4.0;
                        g.DrawLine(pen,
                            cx + (float)Math.Cos(a) * 4.2f, cy + (float)Math.Sin(a) * 4.2f,
                            cx + (float)Math.Cos(a) * 6.0f, cy + (float)Math.Sin(a) * 6.0f);
                    }
                }
                else
                {
                    using (GraphicsPath moon = new GraphicsPath())
                    {
                        moon.AddEllipse(r);
                        using (GraphicsPath cut = new GraphicsPath())
                        {
                            cut.AddEllipse(new Rectangle(r.X + 4, r.Y - 2, r.Width, r.Height));
                            Region reg = new Region(moon);
                            reg.Exclude(cut);
                            g.FillRegion(brush, reg);
                            reg.Dispose();
                        }
                    }
                }
            }
        }

        void BuildModeMenu()
        {
            modeMenu = new ContextMenuStrip();
            modeMenu.Font = new Font("Microsoft YaHei UI", 9f);
            modeMenu.ShowImageMargin = true;
            modeMenu.Renderer = new MenuSkin(Skin);
            modeMenu.BackColor = Skin.Dark
                ? ColorTranslator.FromHtml("#23252C") : ColorTranslator.FromHtml("#FFFFFF");
            modeMenu.Items.Add(ModeItem("跟随系统", "auto"));
            modeMenu.Items.Add(ModeItem("浅色", "light"));
            modeMenu.Items.Add(ModeItem("深色", "dark"));
        }

        ToolStripMenuItem ModeItem(string text, string mode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = mode;
            item.Click += delegate
            {
                ApplyMode(mode);
                Theme.SaveMode(mode);
                if (TrayApp.Current != null) TrayApp.Current.SyncMenu();
            };
            return item;
        }

        public ContextMenuStrip ModeMenuForShot
        {
            get { return modeMenu; }
        }

        public void ShowModeMenu()
        {
            if (modeMenu == null) BuildModeMenu();
            else if (!(modeMenu.Renderer is MenuSkin) || ((MenuSkin)modeMenu.Renderer).SkinDark != Skin.Dark)
                modeMenu.Renderer = new MenuSkin(Skin);
            foreach (ToolStripItem it in modeMenu.Items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null) mi.Checked = (string)mi.Tag == Mode;
            }
            modeMenu.Show(this, new Point(themeRect.X, themeRect.Bottom + 6));
        }

        void DoAction(int target)
        {
            if (target == 1)
            {
                ShowModeMenu();
            }
            else if (target == 2)
            {
                Hide();
            }
            else if (target == 3)
            {
                if (!Busy && TrayApp.Current != null) TrayApp.Current.RefreshNow();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y);
            }
            else
            {
                int h = HitTest(e.Location);
                if (h != hover)
                {
                    hover = h;
                    Cursor = h == 0 ? Cursors.Default : Cursors.Hand;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                press = HitTest(e.Location);
                if (press == 0)
                {
                    dragging = true;
                    dragStart = e.Location;
                }
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging) dragging = false;
            int t = HitTest(e.Location);
            if (t != 0 && t == press) DoAction(t);
            press = 0;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!dragging && hover != 0)
            {
                hover = 0;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }
    }

    class TrayApp : ApplicationContext
    {
        public static TrayApp Current;

        NotifyIcon tray;
        Panel panel;
        System.Windows.Forms.Timer ticker;
        ToolStripMenuItem miAuto;
        ToolStripMenuItem miLight;
        ToolStripMenuItem miDark;
        bool busy;

        public TrayApp()
        {
            Current = this;

            panel = new Panel();
            panel.ApplyMode(Theme.LoadMode());
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            panel.Location = new Point(
                work.Right - panel.Width - 24,
                work.Bottom - panel.Height - 24);

            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem miShow = new ToolStripMenuItem("显示面板");
            miShow.Click += delegate { TogglePanel(); };
            ToolStripMenuItem miRefresh = new ToolStripMenuItem("刷新");
            miRefresh.Click += delegate { RefreshNow(); };

            ToolStripMenuItem miTheme = new ToolStripMenuItem("主题");
            miAuto = new ToolStripMenuItem("跟随系统");
            miLight = new ToolStripMenuItem("浅色");
            miDark = new ToolStripMenuItem("深色");
            miAuto.Click += delegate { SetMode("auto"); };
            miLight.Click += delegate { SetMode("light"); };
            miDark.Click += delegate { SetMode("dark"); };
            miTheme.DropDownItems.Add(miAuto);
            miTheme.DropDownItems.Add(miLight);
            miTheme.DropDownItems.Add(miDark);

            ToolStripMenuItem miAutoStart = new ToolStripMenuItem("开机自启");
            miAutoStart.CheckOnClick = true;
            miAutoStart.Checked = Usage.AutoStart;
            miAutoStart.CheckedChanged += delegate { Usage.AutoStart = miAutoStart.Checked; };

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Quit(); };

            menu.Items.Add(miShow);
            menu.Items.Add(miRefresh);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miTheme);
            menu.Items.Add(miAutoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            SyncMenu();

            tray = new NotifyIcon();
            tray.Icon = BuildIcon();
            tray.Text = "opencode Go 用量";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePanel();
            };

            ticker = new System.Windows.Forms.Timer();
            ticker.Interval = 30000;
            ticker.Tick += delegate
            {
                // 跟随系统模式下，系统主题变了要跟着切
                if (panel.Mode == "auto")
                {
                    Theme now = Theme.Resolve("auto");
                    if (now.Dark != panel.Skin.Dark) panel.ApplyMode("auto");
                }
                RefreshNow();
            };
            ticker.Start();

            RefreshNow();
            panel.Show();
        }

        public void SetMode(string mode)
        {
            Theme.SaveMode(mode);
            panel.ApplyMode(mode);
            SyncMenu();
        }

        public void SyncMenu()
        {
            if (miAuto == null) return;
            miAuto.Checked = panel.Mode == "auto";
            miLight.Checked = panel.Mode == "light";
            miDark.Checked = panel.Mode == "dark";
        }

        static Icon BuildIcon()
        {
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            Bitmap bmp = Brand.Logo(size) ?? new Bitmap(size, size);
            return Icon.FromHandle(bmp.GetHicon());
        }

        public void TogglePanel()
        {
            if (panel.Visible) panel.Hide();
            else { panel.Show(); panel.BringToFront(); }
        }

        public void RefreshNow()
        {
            if (busy) return;
            busy = true;
            panel.SetBusy(true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                List<WindowData> data = Usage.Fetch(out error);
                try
                {
                    panel.Invoke((MethodInvoker)delegate
                    {
                        if (data != null)
                        {
                            panel.Data = data;
                            panel.Error = null;
                            panel.Updated = DateTime.Now;
                            tray.Text = BuildTooltip(data);
                        }
                        else
                        {
                            panel.Error = error;
                            tray.Text = "opencode Go：查询失败";
                        }
                        panel.SetBusy(false);
                        panel.Invalidate();
                    });
                }
                catch { }
                busy = false;
            });
        }

        static string BuildTooltip(List<WindowData> data)
        {
            StringBuilder sb = new StringBuilder();
            foreach (WindowData w in data)
                sb.Append(w.Label).Append(' ').Append(w.Percent).Append("%   ");
            string s = sb.ToString().Trim();
            if (s.Length > 62) s = s.Substring(0, 62);
            return s;
        }

        void Quit()
        {
            tray.Visible = false;
            tray.Dispose();
            panel.Close();
            ExitThread();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "opencode-go-usage.log"); }
        }

        static void Log(string line)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n");
            }
            catch { }
        }

        static void Probe(string path)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                string error;
                List<WindowData> data = Usage.Fetch(out error);
                if (data == null) sb.AppendLine("ERR " + error);
                else
                {
                    sb.AppendLine("OK " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    foreach (WindowData w in data)
                        sb.AppendLine(string.Format("{0}\t{1}%\t{2}\t{3}\t{4}",
                            w.Label, w.Percent, w.Status,
                            w.Reset.HasValue ? w.Reset.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-",
                            Usage.Countdown(w.Reset)));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EX " + ex);
            }
            File.WriteAllText(path, sb.ToString());
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--probe")
            {
                Probe(args[1]);
                return;
            }
            if (args.Length >= 2 && args[0] == "--shot")
            {
                Shot(args[1], args.Length >= 3 ? args[2] : "auto", args.Length >= 4 && args[3] == "busy");
                return;
            }
            if (args.Length >= 2 && args[0] == "--menushot")
            {
                MenuShot(args[1], args.Length >= 3 ? args[2] : "auto");
                return;
            }
            if (args.Length >= 2 && args[0] == "--iconshot")
            {
                IconShot(args[1]);
                return;
            }

            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log("UI 异常: " + e.Exception);
                MessageBox.Show(e.Exception.Message, "opencode Go 用量");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("未处理异常: " + e.ExceptionObject);
            };

            bool created;
            using (Mutex mutex = new Mutex(true, "OpenCodeGoUsageTray", out created))
            {
                if (!created) return;
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }

        static void Shot(string path, string mode, bool busy)
        {
            try { SetProcessDPIAware(); } catch { }
            string error;
            List<WindowData> data = Usage.Fetch(out error);
            Panel p = new Panel();
            p.ApplyMode(mode);
            if (busy)
            {
                p.SetBusy(true);
                p.spinAngle = 120;
            }
            p.Data = data;
            p.Error = error;
            p.Updated = DateTime.Now;
            p.Show();
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(p.Width, p.Height))
            {
                p.DrawToBitmap(bmp, new Rectangle(0, 0, p.Width, p.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            p.Close();
            Console.WriteLine(data == null ? "shot with error: " + error : "shot ok");
        }

        static void MenuShot(string path, string mode)
        {
            try { SetProcessDPIAware(); } catch { }
            Panel p = new Panel();
            p.ApplyMode(mode);
            p.Show();
            Application.DoEvents();
            p.ShowModeMenu();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();
            ContextMenuStrip m = p.ModeMenuForShot;
            if (m != null && m.Width > 0)
            {
                using (Bitmap bmp = new Bitmap(m.Width, m.Height))
                {
                    m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                Console.WriteLine("menushot ok " + m.Width + "x" + m.Height);
            }
            else Console.WriteLine("menushot: menu not ready");
            p.Close();
        }

        static void IconShot(string path)
        {
            int[] sizes = new int[] { 16, 24, 32, 48 };
            int gap = 14, pad = 14;
            int width = pad * 2;
            foreach (int sz in sizes) width += sz + gap;
            using (Bitmap canvas = new Bitmap(width, 78))
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(ColorTranslator.FromHtml("#E9EBEF"));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int x = pad;
                foreach (int sz in sizes)
                {
                    using (Bitmap one = Brand.Logo(sz))
                        if (one != null) g.DrawImage(one, x, 14, sz, sz);
                    x += sz + gap;
                }
                canvas.Save(path, ImageFormat.Png);
            }
        }
    }
}
