// TouchSwitch v2
// Three-finger swipe LEFT  -> next app in a fixed, circular order (1 -> 2 -> 3 -> 1)
// Three-finger swipe RIGHT -> previous app (1 -> 3 -> 2 -> 1)
// Keep your fingers down and keep swiping to keep switching.
// Order is by app launch time, so it stays stable (unlike Alt+Tab's most-recently-used order).
//
// Needs a Windows Precision Touchpad. Disable Windows' own three-finger swipe first:
// Settings > Bluetooth & devices > Touchpad > Three-finger gestures > Swipes = Nothing

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TouchSwitch;

record struct Contact(int Id, int X, int Y);

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var gesture = new SwitchGesture();
        var input = new TouchInput();
        input.Frame += gesture.OnFrame;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TouchSwitch",
            ContextMenuStrip = menu,
            Visible = true
        };

        Application.Run();
        tray.Visible = false;
    }
}

// ---------------------------------------------------------------- gesture logic

sealed class SwitchGesture
{
    // Touchpad units, which vary by hardware. Lower = more sensitive.
    const int FirstThreshold = 250;  // distance for the first switch of a gesture
    const int StepThreshold = 300;   // distance for each further switch while fingers stay down

    readonly System.Windows.Forms.Timer idle = new() { Interval = 120 };
    List<IntPtr> windows = new();
    int current;
    bool tracking, session;
    int anchorX;

    public SwitchGesture()
    {
        // If the touchpad stops reporting, treat it as "fingers lifted".
        idle.Tick += (_, _) => Reset();
    }

    public void OnFrame(List<Contact> contacts)
    {
        if (contacts.Count != 3)
        {
            Reset();
            return;
        }

        idle.Stop();
        idle.Start();

        int avgX = (int)contacts.Average(c => c.X);

        if (!tracking)
        {
            tracking = true;
            session = false;
            anchorX = avgX;
            return;
        }

        int dx = avgX - anchorX;
        if (Math.Abs(dx) < (session ? StepThreshold : FirstThreshold)) return;

        if (!session)
        {
            windows = WindowList.Get();
            if (windows.Count < 2)
            {
                anchorX = avgX;
                return;
            }
            current = WindowList.IndexOfForeground(windows);
            session = true;
        }

        int dir = dx < 0 ? +1 : -1; // swipe left = next, swipe right = previous
        int n = windows.Count;
        current = current < 0 ? 0 : ((current + dir) % n + n) % n;

        WindowList.Activate(windows[current]);
        anchorX = avgX; // re-anchor: keep swiping to switch again without lifting
    }

    void Reset()
    {
        idle.Stop();
        tracking = false;
        session = false;
    }
}

// ---------------------------------------------------------------- raw touchpad input

sealed class TouchInput : NativeWindow
{
    public event Action<List<Contact>>? Frame;

    const int WM_INPUT = 0x00FF;
    readonly Dictionary<IntPtr, IntPtr> preparsed = new();
    readonly List<Contact> frame = new();
    int remaining;

    public TouchInput()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

        var rid = new[]
        {
            new Native.RAWINPUTDEVICE
            {
                UsagePage = 0x0D,   // Digitizer
                Usage = 0x05,       // Touch pad
                Flags = 0x100,      // RIDEV_INPUTSINK
                Target = Handle
            }
        };
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Could not register for touchpad input.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) HandleRaw(m.LParam);
        base.WndProc(ref m);
    }

    void HandleRaw(IntPtr hRaw)
    {
        uint hdr = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        Native.GetRawInputData(hRaw, 0x10000003, IntPtr.Zero, ref size, hdr); // RID_INPUT
        if (size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRaw, 0x10000003, buf, ref size, hdr) != size) return;

            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
            if (header.Type != 2) return; // RIM_TYPEHID

            int sizeHid = Marshal.ReadInt32(buf, (int)hdr);
            int count = Marshal.ReadInt32(buf, (int)hdr + 4);
            IntPtr pp = GetPreparsed(header.Device);
            if (pp == IntPtr.Zero || sizeHid <= 0) return;

            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(buf + (int)hdr + 8 + i * sizeHid, report, 0, sizeHid);
                ParseReport(pp, report);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    IntPtr GetPreparsed(IntPtr device)
    {
        if (preparsed.TryGetValue(device, out var p)) return p;
        uint size = 0;
        Native.GetRawInputDeviceInfo(device, 0x20000005, IntPtr.Zero, ref size); // RIDI_PREPARSEDDATA
        if (size == 0) return IntPtr.Zero;
        p = Marshal.AllocHGlobal((int)size);
        Native.GetRawInputDeviceInfo(device, 0x20000005, p, ref size);
        preparsed[device] = p;
        return p;
    }

    static bool Get(IntPtr pp, byte[] r, ushort page, ushort link, ushort usage, out uint value) =>
        Native.HidP_GetUsageValue(0 /*input*/, page, link, usage, out value, pp, r, (uint)r.Length) == 0x110000;

    void ParseReport(IntPtr pp, byte[] r)
    {
        // Contact count is only non-zero in the first report of a frame.
        if (Get(pp, r, 0x0D, 0, 0x54, out uint count) && count > 0)
        {
            remaining = (int)count;
            frame.Clear();
        }
        else if (remaining == 0)
        {
            // No contacts: fingers lifted.
            Frame?.Invoke(new List<Contact>());
            return;
        }

        // Each finger lives in its own link collection (1, 2, 3, ...).
        for (ushort lc = 1; lc <= 10 && remaining > 0; lc++)
        {
            if (!Get(pp, r, 0x0D, lc, 0x51, out uint id) ||
                !Get(pp, r, 0x01, lc, 0x30, out uint x) ||
                !Get(pp, r, 0x01, lc, 0x31, out uint y)) break;

            frame.Add(new Contact((int)id, (int)x, (int)y));
            remaining--;
        }

        if (remaining == 0 && frame.Count > 0)
            Frame?.Invoke(new List<Contact>(frame));
    }
}

// ---------------------------------------------------------------- window list + switching

static class WindowList
{
    // Switchable windows in a stable order: by app launch time, then window handle.
    public static List<IntPtr> Get()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (IsSwitchable(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);

        return list
            .OrderBy(StartTime)
            .ThenBy(h => h.ToInt64())
            .ToList();
    }

    static DateTime StartTime(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return Process.GetProcessById((int)pid).StartTime;
        }
        catch
        {
            return DateTime.MaxValue; // protected process: sort to the end
        }
    }

    static bool IsSwitchable(IntPtr h)
    {
        if (!Native.IsWindowVisible(h)) return false;
        if (Native.GetWindow(h, 4) != IntPtr.Zero) return false; // has owner
        int ex = Native.GetWindowLong(h, -20);
        if ((ex & 0x80) != 0 && (ex & 0x40000) == 0) return false; // tool window
        if (Native.GetWindowTextLength(h) == 0) return false;
        Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4);
        if (cloaked != 0) return false;

        var cls = new StringBuilder(64);
        Native.GetClassName(h, cls, cls.Capacity);
        return cls.ToString() != "Progman";
    }

    public static int IndexOfForeground(List<IntPtr> list)
    {
        IntPtr fg = Native.GetAncestor(Native.GetForegroundWindow(), 3); // GA_ROOTOWNER
        return list.IndexOf(fg);
    }

    public static void Activate(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, 9); // SW_RESTORE
        // Tapping Alt lets us get past Windows' foreground-lock restriction.
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Native.SetForegroundWindow(h);
    }
}

// ---------------------------------------------------------------- Win32

static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceInfo(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLen);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}