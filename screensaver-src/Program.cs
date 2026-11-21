// Retro Zeit – Windows screensaver that shows the Time Machine / Flipboard pages
// in a full-screen WebView2 (Edge) window.
//
// Screensaver command line contract:
//   /s            run full screen
//   /p <hwnd>     draw into the little preview box of the Windows dialog
//   /c[:hwnd]     show the settings dialog   (also used when started without args)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RetroZeit
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string mode = "/c", hwndArg = null;
            if (args.Length > 0)
            {
                string a = args[0].Trim().ToLowerInvariant().Replace('-', '/');
                if (a.Length >= 2) mode = a.Substring(0, 2);
                if (a.Length > 3 && a[2] == ':') hwndArg = a.Substring(3);
                if (hwndArg == null && args.Length > 1) hwndArg = args[1];
            }

            var cfg = Config.Load();
            switch (mode)
            {
                case "/s":
                    RunSaver(cfg);
                    break;
                case "/p":
                    if (hwndArg != null && long.TryParse(hwndArg, out long h) && Native.IsWindow(new IntPtr(h)))
                    {
                        var preview = new PreviewControl(new IntPtr(h));
                        preview.Start();
                        Application.Run();
                    }
                    break;
                default:
                    Application.Run(new SettingsForm(cfg));
                    break;
            }
        }

        static void RunSaver(Config cfg)
        {
            using (var mutex = new Mutex(true, "RetroZeitScreensaver_Run", out bool first))
            {
                if (!first) return;
                var forms = new List<Form>();
                foreach (var s in Screen.AllScreens)
                    forms.Add(s.Primary ? (Form)new SaverForm(s.Bounds, cfg) : new BlackForm(s.Bounds));
                Application.Run(new MultiFormContext(forms));
            }
            // make sure nothing (WebView2 browser processes, audio) outlives the screensaver
            Environment.Exit(0);
        }
    }

    // ------------------------------------------------------------------ config
    class Config
    {
        public string Folder = "";
        public string Page = "flipboard";      // flipboard | zeitmaschine | wechsel
        public int SwitchMinutes = 3;
        public int FlipSeconds = 15;
        public int TravelSeconds = 30;
        public bool Sound = true;

        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroZeit");
        static string FilePath => Path.Combine(Dir, "config.ini");

        public static string GuessFolder()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            foreach (var d in new[] { exeDir, Path.GetDirectoryName(exeDir) })
                if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "flipboard.html"))) return d;
            return "";
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i < 1) continue;
                        string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
                        switch (k)
                        {
                            case "folder": c.Folder = v; break;
                            case "page": c.Page = v; break;
                            case "switchminutes": int.TryParse(v, out c.SwitchMinutes); break;
                            case "flipseconds": int.TryParse(v, out c.FlipSeconds); break;
                            case "travelseconds": int.TryParse(v, out c.TravelSeconds); break;
                            case "sound": c.Sound = v == "1" || v.ToLowerInvariant() == "true"; break;
                        }
                    }
            }
            catch { }
            if (string.IsNullOrEmpty(c.Folder) || !Directory.Exists(c.Folder)) c.Folder = GuessFolder();
            c.SwitchMinutes = Math.Max(1, c.SwitchMinutes);
            c.FlipSeconds = Math.Max(5, c.FlipSeconds);
            c.TravelSeconds = Math.Max(12, c.TravelSeconds);
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "folder=" + Folder,
                "page=" + Page,
                "switchminutes=" + SwitchMinutes,
                "flipseconds=" + FlipSeconds,
                "travelseconds=" + TravelSeconds,
                "sound=" + (Sound ? "1" : "0"),
            });
        }

        public string Url(string page)
        {
            return page == "zeitmaschine"
                ? "https://retrozeit.local/index.html?ss=1&travel=" + TravelSeconds
                : "https://retrozeit.local/flipboard.html?ss=1&secs=" + FlipSeconds;
        }
    }

    // ------------------------------------------------------------------ win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

        public const int GWL_STYLE = -16;
        public const int WS_CHILD = 0x40000000;

        public static uint LastInput()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref li) ? li.dwTime : 0;
        }
    }

    // Ends the screensaver on any real user input (keyboard, click, mouse moved more than a few px).
    class InputWatcher
    {
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        readonly DateTime started = DateTime.Now;
        Point startPos;
        uint lastInput;
        bool armed;

        public event Action Triggered;

        public InputWatcher()
        {
            timer.Tick += (s, e) => Check();
            timer.Start();
        }

        void Check()
        {
            if (!armed)
            {
                // short grace period: ignore the input that triggered the screensaver start
                if ((DateTime.Now - started).TotalMilliseconds < 1200) return;
                startPos = Cursor.Position;
                lastInput = Native.LastInput();
                armed = true;
                return;
            }
            var p = Cursor.Position;
            int moved = Math.Abs(p.X - startPos.X) + Math.Abs(p.Y - startPos.Y);
            uint li = Native.LastInput();
            bool newInput = li != lastInput;
            if (moved > 14 || (newInput && moved == 0))
            {
                timer.Stop();
                Triggered?.Invoke();
            }
            else if (newInput)
            {
                lastInput = li; // tiny mouse jitter – ignore
            }
        }
    }

    class MultiFormContext : ApplicationContext
    {
        readonly List<Form> forms;
        bool quitting;

        public MultiFormContext(List<Form> forms)
        {
            this.forms = forms;
            var watcher = new InputWatcher();
            watcher.Triggered += Quit;
            foreach (var f in forms)
            {
                f.FormClosed += (s, e) => Quit();
                f.Show();
            }
            Cursor.Hide();
        }

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            foreach (var f in forms)
            {
                try
                {
                    foreach (Control c in f.Controls) (c as WebView2)?.Dispose();   // stops page + audio immediately
                    f.Hide();
                }
                catch { }
            }
            ExitThread();
            Environment.Exit(0);
        }
    }

    // ------------------------------------------------------------------ saver window
    class SaverForm : Form
    {
        readonly Config cfg;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
        readonly System.Windows.Forms.Timer switcher = new System.Windows.Forms.Timer();
        string current;

        const string ExitScript = @"
(function () {
  var sx = null, sy = null, t0 = Date.now();
  function bye() { try { window.chrome.webview.postMessage('exit'); } catch (e) {} }
  addEventListener('mousemove', function (e) {
    if (Date.now() - t0 < 1200 || sx === null) { sx = e.screenX; sy = e.screenY; return; }
    if (Math.abs(e.screenX - sx) + Math.abs(e.screenY - sy) > 14) bye();
  }, true);
  ['mousedown', 'keydown', 'wheel', 'touchstart'].forEach(function (n) {
    addEventListener(n, function () { if (Date.now() - t0 > 600) bye(); }, true);
  });
})();";

        public SaverForm(Rectangle bounds, Config cfg)
        {
            this.cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Controls.Add(web);
            Load += async (s, e) => await Init();
        }

        async System.Threading.Tasks.Task Init()
        {
            if (string.IsNullOrEmpty(cfg.Folder) || !File.Exists(Path.Combine(cfg.Folder, "flipboard.html")))
            {
                ShowError("Ordner mit den Seiten nicht gefunden.\nBitte in den Bildschirmschoner-Einstellungen festlegen.");
                return;
            }
            try
            {
                string udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroZeit", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf,
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.IsMuted = !cfg.Sound;
                core.SetVirtualHostNameToFolderMapping("retrozeit.local", cfg.Folder, CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += (s, e) => { if (e.TryGetWebMessageAsString() == "exit") Close(); };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ExitScript);

                current = cfg.Page == "zeitmaschine" ? "zeitmaschine" : "flipboard";
                core.Navigate(cfg.Url(current));
                web.Focus();

                if (cfg.Page == "wechsel")
                {
                    switcher.Interval = cfg.SwitchMinutes * 60 * 1000;
                    switcher.Tick += (s, e) =>
                    {
                        current = current == "flipboard" ? "zeitmaschine" : "flipboard";
                        core.Navigate(cfg.Url(current));
                    };
                    switcher.Start();
                }
            }
            catch (Exception ex)
            {
                ShowError("Die Edge-WebView2-Laufzeit konnte nicht gestartet werden.\n\n" + ex.Message);
            }
        }

        void ShowError(string text)
        {
            Controls.Remove(web);
            Controls.Add(new Label
            {
                Text = "RETRO ZEIT\n\n" + text,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(232, 130, 42),
                BackColor = Color.Black,
                Font = new Font("Segoe UI", 16f),
                TextAlign = ContentAlignment.MiddleCenter,
            });
        }

        protected override void OnKeyDown(KeyEventArgs e) { Close(); }
        protected override void OnMouseDown(MouseEventArgs e) { Close(); }
    }

    class BlackForm : Form
    {
        public BlackForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            TopMost = true;
            ShowInTaskbar = false;
        }
        protected override void OnKeyDown(KeyEventArgs e) { Close(); }
        protected override void OnMouseDown(MouseEventArgs e) { Close(); }
    }

    // ------------------------------------------------------------------ preview (small box in the Windows dialog)
    class PreviewControl : Control
    {
        readonly IntPtr parent;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 120 };
        int tick;

        public PreviewControl(IntPtr parent)
        {
            this.parent = parent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            timer.Tick += (s, e) =>
            {
                if (!Native.IsWindow(parent)) { timer.Stop(); Application.ExitThread(); return; }
                tick++; Invalidate();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                Native.GetClientRect(parent, out var r);
                cp.Parent = parent;
                cp.Style = Native.WS_CHILD | 0x10000000 /* WS_VISIBLE */ | 0x04000000 /* WS_CLIPSIBLINGS */;
                cp.ExStyle = 0;
                cp.X = 0; cp.Y = 0;
                cp.Width = r.Right - r.Left; cp.Height = r.Bottom - r.Top;
                return cp;
            }
        }

        public void Start()
        {
            CreateHandle();
            Native.GetClientRect(parent, out var r);
            Bounds = new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
            timer.Start();
        }

        static readonly Color[] Stripes =
        {
            Color.FromArgb(227, 169, 43), Color.FromArgb(232, 130, 42), Color.FromArgb(200, 80, 30),
            Color.FromArgb(143, 47, 23), Color.FromArgb(59, 36, 20),
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(74, 44, 24));
            int w = ClientSize.Width, h = ClientSize.Height, sh = Math.Max(2, h / 40);
            for (int i = 0; i < Stripes.Length; i++)
                using (var b = new SolidBrush(Stripes[i])) g.FillRectangle(b, 0, i * sh, w, sh);

            // little split-flap tiles spelling RETRO / ZEIT
            string[] rows = { "RETRO", "ZEIT" };
            int tw = Math.Max(8, w / 9), th = (int)(tw * 1.4), gap = Math.Max(1, tw / 10);
            int top = (h - (th * 2 + gap)) / 2 + sh * 2;
            using (var tile = new SolidBrush(Color.FromArgb(27, 23, 20)))
            using (var glyph = new SolidBrush(Color.FromArgb(243, 234, 216)))
            using (var font = new Font("Arial Narrow", th * .5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var line = new Pen(Color.Black, 1))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                for (int r = 0; r < rows.Length; r++)
                {
                    string word = rows[r];
                    int left = (w - (word.Length * (tw + gap) - gap)) / 2;
                    for (int c = 0; c < word.Length; c++)
                    {
                        var rect = new Rectangle(left + c * (tw + gap), top + r * (th + gap), tw, th);
                        g.FillRectangle(tile, rect);
                        // letters "flip in" one after another
                        int reveal = tick - (r * 5 + c) * 2;
                        char ch = reveal > 6 ? word[c] : (char)('A' + ((tick + c * 7 + r * 3) % 26));
                        g.DrawString(ch.ToString(), font, glyph, rect, fmt);
                        g.DrawLine(line, rect.Left, rect.Top + th / 2, rect.Right, rect.Top + th / 2);
                    }
                }
            }
            if (tick > 140) tick = 0;
        }
    }

    // ------------------------------------------------------------------ settings dialog
    class SettingsForm : Form
    {
        readonly Config cfg;
        readonly TextBox folder = new TextBox { Width = 330 };
        readonly ComboBox page = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        readonly NumericUpDown switchMin = new NumericUpDown { Minimum = 1, Maximum = 120, Width = 70 };
        readonly NumericUpDown flipSec = new NumericUpDown { Minimum = 5, Maximum = 600, Width = 70 };
        readonly NumericUpDown travelSec = new NumericUpDown { Minimum = 12, Maximum = 3600, Width = 70 };
        readonly CheckBox sound = new CheckBox { Text = "Ton an (Klack-Geräusch, Zeitreise-Sound)", AutoSize = true };

        static readonly Color Cream = Color.FromArgb(244, 228, 193);
        static readonly Color Brown = Color.FromArgb(59, 36, 20);
        static readonly Color Rust = Color.FromArgb(200, 80, 30);

        public SettingsForm(Config cfg)
        {
            this.cfg = cfg;
            Text = "Retro Zeit – Bildschirmschoner";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Cream;
            ForeColor = Brown;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 420);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Label
            {
                Text = "Retro Zeit",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold | FontStyle.Italic),
                ForeColor = Rust, AutoSize = true, Location = new Point(18, 16),
            };
            var stripes = new Panel { Location = new Point(0, 0), Size = new Size(520, 8) };
            stripes.Paint += (s, e) =>
            {
                Color[] cs = { Color.FromArgb(227, 169, 43), Color.FromArgb(232, 130, 42), Rust, Color.FromArgb(90, 54, 32) };
                int w = stripes.Width / cs.Length;
                for (int i = 0; i < cs.Length; i++) using (var b = new SolidBrush(cs[i])) e.Graphics.FillRectangle(b, i * w, 0, w + 1, 8);
            };

            page.Items.AddRange(new object[] { "Flipboard", "Zeitmaschine", "Abwechselnd" });
            page.SelectedIndex = cfg.Page == "zeitmaschine" ? 1 : cfg.Page == "wechsel" ? 2 : 0;
            folder.Text = cfg.Folder;
            switchMin.Value = Clamp(cfg.SwitchMinutes, switchMin);
            flipSec.Value = Clamp(cfg.FlipSeconds, flipSec);
            travelSec.Value = Clamp(cfg.TravelSeconds, travelSec);
            sound.Checked = cfg.Sound;

            int y = 70;
            AddRow("Anzeige:", page, ref y);
            AddRow("Wechseln alle (Minuten):", switchMin, ref y);
            AddRow("Flipboard: neue Nachricht alle (Sek.):", flipSec, ref y);
            AddRow("Zeitmaschine: Zeitreise alle (Sek.):", travelSec, ref y);
            sound.Location = new Point(20, y); Controls.Add(sound); y += 38;

            var lblFolder = new Label { Text = "Ordner mit den Seiten (index.html / flipboard.html):", AutoSize = true, Location = new Point(20, y) };
            Controls.Add(lblFolder); y += 24;
            folder.Location = new Point(20, y); Controls.Add(folder);
            var browse = MakeButton("…", 380, y - 2, 40);
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Ordner mit index.html und flipboard.html wählen" })
                    if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
            };
            y += 44;

            var texts = MakeButton("Flipboard-Texte bearbeiten…", 20, y, 220);
            texts.Click += (s, e) => EditTexts();
            var test = MakeButton("Testen", 250, y, 100);
            test.Click += (s, e) => { if (Apply()) Process.Start(Application.ExecutablePath, "/s"); };
            y += 48;

            var ok = MakeButton("OK", 300, y, 100, true);
            ok.Click += (s, e) => { if (Apply()) Close(); };
            var cancel = MakeButton("Abbrechen", 405, y, 100);
            cancel.Click += (s, e) => Close();
            AcceptButton = ok; CancelButton = cancel;

            var hint = new Label
            {
                Text = "Beenden: Maus bewegen oder Taste drücken.",
                AutoSize = true, ForeColor = Color.FromArgb(107, 74, 46), Location = new Point(20, y + 8),
            };
            Controls.AddRange(new Control[] { stripes, header, hint });
            page.SelectedIndexChanged += (s, e) => switchMin.Enabled = page.SelectedIndex == 2;
            switchMin.Enabled = page.SelectedIndex == 2;
            ClientSize = new Size(520, y + 50);
        }

        static decimal Clamp(int v, NumericUpDown n) => Math.Max(n.Minimum, Math.Min(n.Maximum, v));

        void AddRow(string label, Control c, ref int y)
        {
            Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(20, y + 4) });
            c.Location = new Point(290, y);
            Controls.Add(c);
            y += 36;
        }

        Button MakeButton(string text, int x, int y, int w, bool primary = false)
        {
            var b = new Button
            {
                Text = text, Location = new Point(x, y), Size = new Size(w, 32),
                FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(232, 130, 42) : Color.FromArgb(90, 54, 32),
                ForeColor = primary ? Color.FromArgb(42, 18, 6) : Cream, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            Controls.Add(b);
            return b;
        }

        bool Apply()
        {
            string f = folder.Text.Trim();
            if (!File.Exists(Path.Combine(f, "flipboard.html")) || !File.Exists(Path.Combine(f, "index.html")))
            {
                MessageBox.Show(this, "In diesem Ordner wurden index.html und flipboard.html nicht gefunden.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            cfg.Folder = f;
            cfg.Page = new[] { "flipboard", "zeitmaschine", "wechsel" }[page.SelectedIndex];
            cfg.SwitchMinutes = (int)switchMin.Value;
            cfg.FlipSeconds = (int)flipSec.Value;
            cfg.TravelSeconds = (int)travelSec.Value;
            cfg.Sound = sound.Checked;
            try { cfg.Save(); }
            catch (Exception ex) { MessageBox.Show(this, "Speichern fehlgeschlagen: " + ex.Message, Text); return false; }
            return true;
        }

        void EditTexts()
        {
            string f = Path.Combine(folder.Text.Trim(), "flipboard-texte.txt");
            if (!Directory.Exists(folder.Text.Trim())) { MessageBox.Show(this, "Bitte zuerst einen gültigen Ordner wählen.", Text); return; }
            if (!File.Exists(f)) File.WriteAllText(f, "TITEL: Groovy Board\r\n\r\nHALLO WELT\r\n---\n@CLOCK\r\n", System.Text.Encoding.UTF8);
            Process.Start("notepad.exe", "\"" + f + "\"");
        }
    }
}
