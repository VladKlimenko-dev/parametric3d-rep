using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Parametric3D.Core;

namespace Parametric3D.App.Viewport;

/// <summary>
/// k127: отчёты Windows Precision Touchpad для сдвига тремя пальцами (TouchpadPan). Окно подписывается на «сырой ввод»
/// цифровой панели (HID: страница 0x0D, использование 0x05), отчёт разбирается по описанию устройства (hid.dll):
/// число касаний (0x0D/0x54), в каждом наборе касания — номер (0x0D/0x51), прижат (0x0D/0x42), X/Y (0x01/0x30, 0x31);
/// координаты переводятся в мм по физическому размеру панели. Обычный ввод (курсор, прокрутка двумя пальцами) не
/// трогается. Жест трёх пальцев самой Windows (переключение окон) приложение отменить не может — его выключают в
/// Параметрах (Сенсорная панель → Жесты тремя пальцами → Прокрутки: «Ничего»).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinTouchpad
{
    public static long Reports, Errors;

    public static void Attach(Window w, TouchpadPan tracker)
    {
        try
        {
            var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            var dev = new RAWINPUTDEVICE[] { new() { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = 0, hwndTarget = hwnd } };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) { AppLog.Write($"touchpad: RegisterRawInputDevices failed {Marshal.GetLastWin32Error()}"); return; }
            var devices = new Dictionary<IntPtr, Device?>();
            Win32Properties.AddWndProcHookCallback(w, (IntPtr h, uint msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_INPUT)
                {
                    try { OnInput(lp, devices, tracker); }
                    catch (Exception ex) { if (Interlocked.Increment(ref Errors) < 5) AppLog.Write("touchpad: " + ex.Message); }
                }
                return IntPtr.Zero;
            });
            AppLog.Write("touchpad: raw input registered");
        }
        catch (Exception ex) { AppLog.Write("touchpad: " + ex.Message); }
    }

    const uint WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIM_TYPEHID = 2, RIDI_PREPARSEDDATA = 0x20000005;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    sealed class Contact { public ushort Link; public bool HasId; public double MmX = 1, MmY = 1; }
    sealed class Device
    {
        public IntPtr Preparsed;
        public readonly List<Contact> Contacts = new();
        public ushort CountLink; public bool HasCount;
    }

    static unsafe void OnInput(IntPtr hRaw, Dictionary<IntPtr, Device?> devices, TouchpadPan tracker)
    {
        uint hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>(), size = 0;
        GetRawInputData(hRaw, RID_INPUT, null, ref size, hdr);
        if (size == 0) return;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (GetRawInputData(hRaw, RID_INPUT, p, ref size, hdr) != size) return;
            var h = *(RAWINPUTHEADER*)p;
            if (h.dwType != RIM_TYPEHID) return;
            if (!devices.TryGetValue(h.hDevice, out var dev)) devices[h.hDevice] = dev = Describe(h.hDevice);
            if (dev == null) return;
            byte* hid = p + hdr;
            uint each = *(uint*)hid, count = *(uint*)(hid + 4);
            byte* rep = hid + 8;
            var list = new List<TouchContact>(dev.Contacts.Count);
            var usages = new ushort[16];
            for (uint r = 0; r < count; r++, rep += each)
            {
                Interlocked.Increment(ref Reports);
                int n = 0;
                if (dev.HasCount && HidP_GetUsageValue(0, 0x0D, dev.CountLink, 0x54, out uint cc, dev.Preparsed, rep, each) == HIDP_STATUS_SUCCESS) n = (int)cc;
                list.Clear();
                foreach (var c in dev.Contacts)
                {
                    if (HidP_GetUsageValue(0, 0x01, c.Link, 0x30, out uint x, dev.Preparsed, rep, each) != HIDP_STATUS_SUCCESS) continue;
                    if (HidP_GetUsageValue(0, 0x01, c.Link, 0x31, out uint y, dev.Preparsed, rep, each) != HIDP_STATUS_SUCCESS) continue;
                    uint id = c.Link;
                    if (c.HasId && HidP_GetUsageValue(0, 0x0D, c.Link, 0x51, out uint cid, dev.Preparsed, rep, each) == HIDP_STATUS_SUCCESS) id = cid;
                    uint ul = (uint)usages.Length; bool tip = false;
                    fixed (ushort* u = usages)
                        if (HidP_GetUsages(0, 0x0D, c.Link, u, ref ul, dev.Preparsed, rep, each) == HIDP_STATUS_SUCCESS)
                            for (int i = 0; i < ul; i++) if (usages[i] == 0x42) tip = true;
                    list.Add(new TouchContact((int)id, x * c.MmX, y * c.MmY, tip));
                }
                tracker.AddReport(n, list);
            }
        }
    }

    static unsafe Device? Describe(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var pp = Marshal.AllocHGlobal((int)size);
        if ((int)GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, pp, ref size) <= 0) { Marshal.FreeHGlobal(pp); return null; }
        if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(pp); return null; }
        ushort nv = caps.NumberInputValueCaps;
        var vc = new HIDP_VALUE_CAPS[nv];
        fixed (HIDP_VALUE_CAPS* v = vc) if (HidP_GetValueCaps(0, v, ref nv, pp) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(pp); return null; }
        var dev = new Device { Preparsed = pp };
        var byLink = new Dictionary<ushort, Contact>();
        Contact Get(ushort link) { if (!byLink.TryGetValue(link, out var c)) byLink[link] = c = new Contact { Link = link }; return c; }
        var xs = new HashSet<ushort>(); var ys = new HashSet<ushort>();
        for (int i = 0; i < nv; i++)
        {
            var c = vc[i]; ushort usage = c.UsageMin;
            if (c.UsagePage == 0x0D && usage == 0x54) { dev.HasCount = true; dev.CountLink = c.LinkCollection; }
            else if (c.UsagePage == 0x0D && usage == 0x51) Get(c.LinkCollection).HasId = true;
            else if (c.UsagePage == 0x01 && (usage == 0x30 || usage == 0x31))
            {
                double mm = MmPerUnit(c, usage == 0x30 ? 100 : 65);
                var ct = Get(c.LinkCollection);
                if (usage == 0x30) { ct.MmX = mm; xs.Add(c.LinkCollection); } else { ct.MmY = mm; ys.Add(c.LinkCollection); }
            }
        }
        foreach (var kv in byLink.OrderBy(k => k.Key)) if (xs.Contains(kv.Key) && ys.Contains(kv.Key)) dev.Contacts.Add(kv.Value);
        AppLog.Write($"touchpad: device {hDevice}: contacts {dev.Contacts.Count}, count usage {dev.HasCount}");
        if (dev.Contacts.Count == 0) { Marshal.FreeHGlobal(pp); return null; }
        return dev;
    }

    /// <summary>Мм на единицу координаты по физическому размеру (единицы HID: см или дюйм); иначе — панель fallbackMm.</summary>
    static double MmPerUnit(HIDP_VALUE_CAPS c, double fallbackMm)
    {
        double logical = (double)c.LogicalMax - c.LogicalMin;
        if (logical <= 0) return 1;
        double phys = (double)c.PhysicalMax - c.PhysicalMin;
        int e = (int)(c.UnitsExp & 0xF); if (e > 7) e -= 16;
        uint lin = c.Units & 0xF, len = (c.Units >> 4) & 0xF;
        double mm = 0;
        if (phys > 0 && len == 1 && lin == 1) mm = phys * Math.Pow(10, e) * 10;        // см
        else if (phys > 0 && len == 1 && lin == 3) mm = phys * Math.Pow(10, e) * 25.4; // дюйм
        if (!(mm > 10 && mm < 1000)) mm = fallbackMm;
        return mm / logical;
    }

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed_reserved17 Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [StructLayout(LayoutKind.Sequential, Size = 34)] struct fixed_reserved17 { }
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort UsageMin;   // Range.UsageMin / NotRange.Usage
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern unsafe uint GetRawInputData(IntPtr hRawInput, uint command, byte* data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern unsafe int HidP_GetValueCaps(int reportType, HIDP_VALUE_CAPS* caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")] static extern unsafe int HidP_GetUsageValue(int reportType, ushort usagePage, ushort link, ushort usage, out uint value, IntPtr preparsed, byte* report, uint length);
    [DllImport("hid.dll")] static extern unsafe int HidP_GetUsages(int reportType, ushort usagePage, ushort link, ushort* usages, ref uint length, IntPtr preparsed, byte* report, uint length2);
}
