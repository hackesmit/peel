// Feasibility spike: detect an Explorer drag with a low-level mouse hook, open a
// radial overlay on a Shift tap, and receive the drop on one of its segments.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    public static readonly string LogPath = @"C:\peel-dev\spike\log.txt";
    public static void Log(string s)
    {
        File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n");
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UNHANDLED " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Log("THREAD EX " + e.Exception);
        Log("spike start, session " + Process.GetCurrentProcess().SessionId);
        var ctx = new SpikeContext();
        Application.Run(ctx);
    }
}

class SpikeContext : ApplicationContext
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [StructLayout(LayoutKind.Sequential)] struct MSLL { public int x, y, data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBLL { public int vk, scan, flags, time; public IntPtr extra; }

    readonly HookProc mouseProc, keyProc;
    readonly IntPtr mouseHook, keyHook;
    bool down, dragging; int downX, downY;
    readonly Overlay overlay = new Overlay();
    readonly Control ui = new Control();
    readonly long hookTicksMax = 0;
    readonly Stopwatch sw = new Stopwatch();
    long worstHookUs; public static int mouseCalls, keyCalls;

    public SpikeContext()
    {
        ui.CreateControl();
        mouseProc = OnMouse; keyProc = OnKey;
        var mod = GetModuleHandle(null);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, mod, 0);
        Program.Log($"hooks mouse={mouseHook != IntPtr.Zero} key={keyHook != IntPtr.Zero}");
        var t = new Timer { Interval = 120000 };
        t.Tick += (s, e) => { Program.Log($"timeout exit, worst hook {worstHookUs}us"); ExitThread(); };
        t.Start();
        var hb = new Timer { Interval = 1000 };
        hb.Tick += (s, e) => Program.Log($"hb mouseCalls={mouseCalls} keyCalls={keyCalls}");
        hb.Start();
        overlay.Dropped += () => { Program.Log($"worst hook {worstHookUs}us"); };
    }

    IntPtr OnMouse(int code, IntPtr w, IntPtr l)
    {
        sw.Restart(); mouseCalls++;
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MSLL>(l);
            int msg = (int)w;
            if (msg == WM_LBUTTONDOWN) { down = true; dragging = false; downX = m.x; downY = m.y; }
            else if (msg == WM_LBUTTONUP) { down = false; if (dragging) Program.Log("drag end"); dragging = false; ui.BeginInvoke(new Action(overlay.HideSoon)); }
            else if (msg == WM_MOUSEMOVE && down && !dragging &&
                     (Math.Abs(m.x - downX) > GetSystemMetrics(68) || Math.Abs(m.y - downY) > GetSystemMetrics(69)))
            { dragging = true; Program.Log($"drag start at {downX},{downY}"); }
        }
        var r = CallNextHookEx(IntPtr.Zero, code, w, l);
        worstHookUs = Math.Max(worstHookUs, sw.ElapsedTicks * 1000000 / Stopwatch.Frequency);
        return r;
    }

    IntPtr OnKey(int code, IntPtr w, IntPtr l)
    {
        keyCalls++;
        if (code >= 0 && ((int)w == WM_KEYDOWN || (int)w == WM_SYSKEYDOWN))
        {
            var k = Marshal.PtrToStructure<KBLL>(l);
            bool shift = k.vk == 0xA0 || k.vk == 0xA1 || k.vk == 0x10;
            if (shift) Program.Log($"shift down, dragging={dragging}");
            if (shift && dragging)
            {
                var p = Cursor.Position;
                ui.BeginInvoke(new Action(() => overlay.ShowAt(p)));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }
}

class Overlay : Form
{
    static readonly string[] Labels = { "WEBP", "JPG", "PDF", "BMP" };
    int hot = -1;
    public event Action Dropped;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var c = base.CreateParams; c.ExStyle |= 0x08 | 0x80 | 0x08000000; return c; } // TOPMOST, TOOLWINDOW, NOACTIVATE
    }

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 320);
        BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
        TopMost = true; ShowInTaskbar = false; AllowDrop = true; DoubleBuffered = true;
        DragEnter += (s, e) => { Program.Log("overlay DragEnter formats=" + string.Join(",", e.Data.GetFormats())); Track(e); };
        DragOver += (s, e) => Track(e);
        DragLeave += (s, e) => { Program.Log("overlay DragLeave"); hot = -1; Invalidate(); };
        DragDrop += (s, e) =>
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            int seg = Segment(PointToClient(new Point(e.X, e.Y)));
            Program.Log($"DROP segment={(seg < 0 ? "cancel" : Labels[seg])} files={string.Join("|", files ?? new string[0])}");
            Dropped?.Invoke();
            Hide();
        };
    }

    public void ShowAt(Point p)
    {
        if (Visible) return;
        Location = new Point(p.X - Width / 2, p.Y - Height / 2);
        Show();
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        Program.Log($"overlay shown at {p.X},{p.Y}");
    }

    public void HideSoon()
    {
        // OLE delivers DragDrop after the button-up the hook sees, so wait before hiding.
        var t = new Timer { Interval = 500 };
        t.Tick += (s, e) => { t.Dispose(); if (Visible) Hide(); };
        t.Start();
    }

    int Segment(Point c)
    {
        double dx = c.X - Width / 2.0, dy = c.Y - Height / 2.0, r = Math.Sqrt(dx * dx + dy * dy);
        if (r < 50 || r > 160) return -1;
        double a = (Math.Atan2(dy, dx) * 180 / Math.PI + 360 + 45) % 360; // 0 = right
        return (int)(a / 90);
    }

    void Track(DragEventArgs e)
    {
        int seg = Segment(PointToClient(new Point(e.X, e.Y)));
        e.Effect = seg < 0 ? DragDropEffects.None : DragDropEffects.Copy;
        if (seg != hot) { hot = seg; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        for (int i = 0; i < 4; i++)
        {
            using var b = new SolidBrush(i == hot ? Color.FromArgb(255, 140, 0) : Color.FromArgb(40, 40, 48));
            g.FillPie(b, rect, i * 90 - 45, 90);
            g.DrawPie(Pens.White, rect, i * 90 - 45, 90);
        }
        g.FillEllipse(Brushes.Magenta, Width / 2 - 50, Height / 2 - 50, 100, 100);
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2; var pt = new PointF((float)(Width / 2 + Math.Cos(a) * 105 - 20), (float)(Height / 2 + Math.Sin(a) * 105 - 8));
            g.DrawString(Labels[i], new Font("Segoe UI", 10, FontStyle.Bold), Brushes.White, pt);
        }
    }
}
