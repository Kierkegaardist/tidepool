using System.Runtime.InteropServices;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>Input while the menu is closed, on its own thread so a hung host never lags the mouse:
/// left clicks and moves on the bare desktop go to the page under them (as real mouse messages, the way Lively does it),
/// and the DualSense PS button toggles the menu.</summary>
sealed class Input
{
    /// <summary>What the hook needs per screen; the UI thread swaps in a new array, the hook thread only reads it.</summary>
    public record Target(Rectangle bounds, IntPtr scene, bool live);
    public volatile Target[] Targets = Array.Empty<Target>();
    public volatile bool MenuOpen;
    readonly Action onGuide;
    readonly Action<string[]> onPad;   // while the menu is open: the keys the pad holds down, sent when they change
    MsgWindow? padWindow;   // receives the pad's HID reports (Raw Input), also while another app has focus
    readonly Dictionary<IntPtr, IntPtr> render = new();   // scene -> its Chrome_RenderWidgetHostHWND
    LowLevelMouseProc? proc;   // kept alive: the hook holds only a native pointer to it
    uint threadId;

    public Input(Action onGuide, Action<string[]> onPad)
    {
        this.onGuide = onGuide; this.onPad = onPad;
        var t = new Thread(Run) { IsBackground = true, Name = "input" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public void Stop() { if (threadId != 0) PostThreadMessage(threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero); }

    void Run()
    {
        threadId = GetCurrentThreadId();
        proc = Hook;
        var hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Write("WARNING: mouse hook failed; desktop clicks will not reach the page");
        padWindow = new MsgWindow("ConsoleDesktopPad", m => { if (m.Msg == 0x00FF /*WM_INPUT*/) OnPad(m.LParam); });
        var rid = new RAWINPUTDEVICE { usagePage = 1, usage = 5 /*game pad*/, flags = 0x100 /*RIDEV_INPUTSINK*/, target = padWindow.Handle };
        if (!RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) Log.Write("WARNING: pad input failed; the PS button will not open the menu");
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    IntPtr held;   // the scene a forwarded press went to: it gets the moves and the release too, wherever they happen
    IntPtr Hook(int code, IntPtr msg, IntPtr info)
    {
        if (MenuOpen) held = IntPtr.Zero;   // lifted: the scene gets real mouse messages now
        else if (code >= 0 && (int)msg is 0x0200 or 0x0201 or 0x0202 or 0x020A /*MOVE, LBUTTONDOWN, LBUTTONUP, WHEEL*/)
        {
            var pt = Marshal.PtrToStructure<POINT>(info);
            if (held != IntPtr.Zero && (int)msg is 0x0200 or 0x0202) { Forward(held, (uint)msg, pt); if ((int)msg == 0x0202) held = IntPtr.Zero; }   // a release over a window must still end the page's press, or the row drags along with the bare pointer
            else
            {
                var t = Targets.FirstOrDefault(x => x.live && x.bounds.Contains(pt.X, pt.Y));
                if (t != null && OnDesktop(pt)) { if ((int)msg == 0x0201) held = t.scene; Forward(t.scene, (uint)msg, pt, (int)msg == 0x020A ? Marshal.ReadInt32(info, 8) >> 16 : 0); }   // MSLLHOOKSTRUCT.mouseData: wheel delta in the high word
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, msg, info);
    }

    /// <summary>The pointer is over the bare desktop (Explorer's desktop windows or our own scene), not over an app.</summary>
    static bool OnDesktop(POINT pt)
    {
        var w = WindowFromPoint(pt);
        return ClassOf(w) is "SHELLDLL_DefView" or "SysListView32" or "WorkerW" or "Progman" || ClassOf(GetAncestor(w, 2 /*GA_ROOT*/)) is "WorkerW" or "Progman";
    }

    void Forward(IntPtr scene, uint msg, POINT pt, int wheel = 0)
    {
        if (!render.TryGetValue(scene, out var r) || !IsWindow(r))
        {
            r = IntPtr.Zero;
            EnumChildWindows(scene, (h, _) => { if (ClassOf(h) == "Chrome_RenderWidgetHostHWND") { r = h; return false; } return true; }, IntPtr.Zero);
            render[scene] = r;
        }
        if (r == IntPtr.Zero) return;
        if (msg == 0x020A) { PostMessage(r, msg, (IntPtr)(wheel << 16), (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF))); return; }   // the wheel keeps screen coordinates
        ScreenToClient(r, ref pt);
        var down = msg == 0x0201 || (msg == 0x0200 && held != IntPtr.Zero) ? 1 /*MK_LBUTTON*/ : 0;   // moves while pressed say so: the page ends a drag on a move without the button
        PostMessage(r, msg, (IntPtr)down, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
    }

    // DualSense PS button, from the pad's own HID input report (Sony 054C, DualSense 0CE6 / Edge 0DF2). PS = bit 0 of the third button byte:
    // USB report 0x01 (64 bytes) at [10], Bluetooth full report 0x31 at [11], Bluetooth basic report 0x01 (until a game switches the pad to full) at [7].
    readonly Dictionary<IntPtr, bool> isDualSense = new();
    readonly byte[] buf = new byte[1024];
    readonly Dictionary<IntPtr, bool> psWas = new();   // per connection: a pad on USB and Bluetooth at once reports the press twice
    long lastToggle;
    void OnPad(IntPtr handle)
    {
        uint size = (uint)buf.Length, header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        var got = GetRawInputData(handle, 0x10000003 /*RID_INPUT*/, buf, ref size, header);
        if (got == uint.MaxValue || got < header + 8) return;
        var h = MemoryMarshal.Read<RAWINPUTHEADER>(buf);
        if (!isDualSense.TryGetValue(h.device, out var ds)) isDualSense[h.device] = ds = DualSense(h.device);
        if (!ds) return;
        int len = BitConverter.ToInt32(buf, (int)header), at = (int)header + 8;   // RAWHID: dwSizeHid, dwCount, then the report
        if (seenReport.Add((h.device, buf[at]))) Log.Write($"pad report 0x{buf[at]:X2}, {len} bytes: {Convert.ToHexString(buf, at, Math.Min(len, 16))}");
        // Layouts: USB 0x01 (64 bytes) sticks at [1], buttons at [8..10]; Bluetooth 0x31 one byte later; Bluetooth basic 0x01 sticks at [1], buttons at [5..7].
        // Windows pads every Bluetooth report to the pad's largest (78 bytes), so only an exact 64 means the USB layout.
        int stick = buf[at] == 0x31 ? 2 : buf[at] == 0x01 ? 1 : -1, btn = buf[at] == 0x31 ? 9 : len == 64 ? 8 : 5;
        if (stick < 0 || btn + 2 >= len) return;
        var r = buf.AsSpan(at, len);
        var down = (r[btn + 2] & 1) != 0;
        if (down && !psWas.GetValueOrDefault(h.device) && Environment.TickCount64 - lastToggle > 300) { lastToggle = Environment.TickCount64; onGuide(); }
        psWas[h.device] = down;
        if (!MenuOpen) { lastKeys.Remove(h.device); return; }
        var k = new List<string>();
        int hat = r[btn] & 0x0F, face = r[btn], sh = r[btn + 1], x = r[stick] - 128, y = r[stick + 1] - 128;
        if (hat is 7 or 0 or 1 || (y < -64 && -y > Math.Abs(x))) k.Add("ArrowUp");
        if (hat is 3 or 4 or 5 || (y > 64 && y > Math.Abs(x))) k.Add("ArrowDown");
        if (hat is 5 or 6 or 7 || (x < -64 && -x >= Math.Abs(y))) k.Add("ArrowLeft");
        if (hat is 1 or 2 or 3 || (x > 64 && x >= Math.Abs(y))) k.Add("ArrowRight");
        foreach (var (bit, key) in new[] { (0x20, "Enter"), (0x40, "Escape"), (0x10, "Delete"), (0x80, "F2") }) if ((face & bit) != 0) k.Add(key);   // ✕ ○ □ △
        foreach (var (bit, key) in new[] { (0x01, "PageUp"), (0x02, "PageDown"), (0x04, "["), (0x08, "]"), (0x10, "Tab"), (0x20, "F3") }) if ((sh & bit) != 0) k.Add(key);   // L1 R1 L2 R2 Create Options
        var j = string.Join(",", k);
        if (lastKeys.GetValueOrDefault(h.device) == j) return;
        lastKeys[h.device] = j; onPad(k.ToArray());
    }
    readonly Dictionary<IntPtr, string> lastKeys = new();
    readonly HashSet<(IntPtr, byte)> seenReport = new();

    static bool DualSense(IntPtr device)
    {
        var info = new byte[32]; BitConverter.GetBytes(32).CopyTo(info, 0);   // cbSize must be exactly sizeof(RID_DEVICE_INFO)
        uint size = (uint)info.Length;
        if ((int)GetRawInputDeviceInfo(device, 0x2000000b /*RIDI_DEVICEINFO*/, info, ref size) <= 0) { Log.Write("pad: device info unreadable, ignored"); return false; }
        uint vid = BitConverter.ToUInt32(info, 8), pid = BitConverter.ToUInt32(info, 12);
        var yes = vid == 0x054C && pid is 0x0CE6 or 0x0DF2;
        Log.Write($"pad {vid:X4}:{pid:X4} {(yes ? "DualSense: PS button opens the menu" : "not a DualSense, ignored")}");
        return yes;
    }

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usagePage, usage; public uint flags; public IntPtr target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint type, size; public IntPtr device, wParam; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, byte[] data, ref uint size, uint header);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, byte[] data, ref uint size);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint time; public POINT pt; }
    delegate IntPtr LowLevelMouseProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc f, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
}
