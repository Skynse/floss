using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace Floss.App.Input;

// Reads XI2 tablet events directly from a private X11 display connection,
// bypassing Avalonia's broken pen handling (phantom taps when hovering).
// Only active on Linux. Feeds samples through SampleReceived on the UI thread.
internal sealed unsafe class NativeTabletX11 : IDisposable
{
    public event Action<NativeTabletSample>? SampleReceived;

    private nint _display;
    private nint _rootWindow;
    private nint _appWindow;
    private int  _xiOpcode;
    private int  _penDeviceId = -1;
    private int  _pressureValuatorIndex = -1;
    private double _pressureMin, _pressureMax = 1.0;

    private Thread? _thread;
    private volatile bool _running;

    // --- P/Invoke ----------------------------------------------------------

    [DllImport("libX11")] static extern nint XOpenDisplay(nint display);
    [DllImport("libX11")] static extern int  XCloseDisplay(nint display);
    [DllImport("libX11")] static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11")] static extern int  XFlush(nint display);
    [DllImport("libX11")] static extern int  XPending(nint display);
    [DllImport("libX11")] static extern nint XNextEvent(nint display, XEvent* xev);
    [DllImport("libX11")] static extern bool XGetEventData(nint display, XGenericEventCookie* cookie);
    [DllImport("libX11")] static extern void XFreeEventData(nint display, XGenericEventCookie* cookie);
    [DllImport("libX11")] static extern bool XQueryExtension(nint display,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        out int major_opcode, out int first_event, out int first_error);

    [DllImport("libXi")] static extern int  XIQueryVersion(nint display, ref int major, ref int minor);
    [DllImport("libXi")] static extern nint XIQueryDevice(nint display, int deviceid, out int ndevices);
    [DllImport("libXi")] static extern void XIFreeDeviceInfo(nint info);
    [DllImport("libXi")] static extern int  XISelectEvents(nint display, nint window, XIEventMask* masks, int nmasks);

    // --- Structs -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct XIEventMask { public int Deviceid, MaskLen; public int* Mask; }

    [StructLayout(LayoutKind.Sequential)]
    struct XIDeviceInfo
    {
        public int    Deviceid;
        public nint   Name;        // char*
        public int    Use;         // XIMasterPointer=1, XISlavePointer=2, ...
        public int    Attachment;
        public int    Enabled;
        public int    NumClasses;
        public nint   Classes;     // XIAnyClassInfo**
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XIAnyClassInfo { public int Type; public int Sourceid; }

    [StructLayout(LayoutKind.Sequential)]
    struct XIValuatorClassInfo
    {
        public int    Type;     // XIValuatorClass = 3
        public int    Sourceid;
        public int    Number;
        public nint   Label;
        public double Min, Max, Value;
        public int    Resolution, Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XIValuatorState { public int MaskLen; public byte* Mask; public double* Values; }

    [StructLayout(LayoutKind.Sequential)]
    struct XIButtonState { public int MaskLen; public byte* Mask; }

    [StructLayout(LayoutKind.Sequential)]
    struct XIModifierState { public int @base, latched, locked, effective; }

    [StructLayout(LayoutKind.Sequential)]
    struct XIDeviceEvent
    {
        public int    type;
        public nuint  serial;
        public int    send_event;
        public nint   display;
        public int    extension;
        public int    evtype;
        public nint   time;
        public int    deviceid, sourceid, detail;
        public nint   root, event_window, child;
        public double root_x, root_y, event_x, event_y;
        public int    flags;
        public XIButtonState   buttons;
        public XIValuatorState valuators;
        public XIModifierState mods, group;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct XEvent
    {
        [FieldOffset(0)] public int                type;
        [FieldOffset(0)] public XGenericEventCookie GenericEventCookie;
        [FieldOffset(0)] fixed byte                _pad[192];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XGenericEventCookie
    {
        public int   type;
        public nint  serial;
        public int   send_event;
        public nint  display;
        public int   extension;
        public int   evtype;
        public uint  cookie;
        public void* data;
    }

    // XI2 event type constants
    const int XI_ButtonPress   = 4;
    const int XI_ButtonRelease = 5;
    const int XI_Motion        = 6;

    // XIDeviceClass types
    const int XIValuatorClass = 3;

    // XIDeviceUse
    const int XISlavePointer = 2;

    // --- Public API --------------------------------------------------------

    public static NativeTabletX11? TryCreate(nint appWindowXId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
        try
        {
            var inst = new NativeTabletX11();
            if (!inst.Init(appWindowXId)) return null;
            return inst;
        }
        catch { return null; }
    }

    private bool Init(nint appWindowXId)
    {
        _display = XOpenDisplay(0);
        if (_display == 0) return false;

        _rootWindow = XDefaultRootWindow(_display);
        _appWindow  = appWindowXId;

        // Verify XI2 is available
        if (!XQueryExtension(_display, "XInputExtension", out _xiOpcode, out _, out _))
            return false;

        int major = 2, minor = 0;
        if (XIQueryVersion(_display, ref major, ref minor) != 0) return false;

        if (!FindPenDevice()) return false;
        SelectEvents();

        _running = true;
        _thread  = new Thread(EventLoop) { IsBackground = true, Name = "NativeTabletX11" };
        _thread.Start();
        return true;
    }

    private bool FindPenDevice()
    {
        var info = (XIDeviceInfo*)XIQueryDevice(_display, 0 /* XIAllDevices */, out int count);
        if (info == null) return false;

        try
        {
            for (int i = 0; i < count; i++)
            {
                ref var dev = ref info[i];
                // Only look at slave pointer devices (actual physical devices, not master)
                if (dev.Use != XISlavePointer) continue;

                // Look for a pressure valuator to identify pen devices
                var classes = (nint*)dev.Classes;
                for (int c = 0; c < dev.NumClasses; c++)
                {
                    var cls = (XIAnyClassInfo*)classes[c];
                    if (cls->Type != XIValuatorClass) continue;

                    var vcls = (XIValuatorClassInfo*)cls;
                    // Pressure valuator is typically number 2 on pen devices;
                    // we identify it by having a non-trivial range and no label match needed —
                    // just take the first valuator with Min=0 and Max>0 on index 2.
                    if (vcls->Number == 2 && vcls->Max > vcls->Min)
                    {
                        _penDeviceId          = dev.Deviceid;
                        _pressureValuatorIndex = vcls->Number;
                        _pressureMin           = vcls->Min;
                        _pressureMax           = vcls->Max;
                        return true;
                    }
                }
            }
        }
        finally { XIFreeDeviceInfo((nint)info); }

        return false;
    }

    private void SelectEvents()
    {
        // Select XI_ButtonPress, XI_ButtonRelease, XI_Motion for our pen device on the app window.
        // Bit positions: Motion=6, ButtonPress=4, ButtonRelease=5
        int mask = (1 << XI_Motion) | (1 << XI_ButtonPress) | (1 << XI_ButtonRelease);
        var emask = new XIEventMask { Deviceid = _penDeviceId, MaskLen = 1, Mask = &mask };
        XISelectEvents(_display, _appWindow, &emask, 1);
        XFlush(_display);
    }

    private void EventLoop()
    {
        XEvent xev;
        while (_running)
        {
            if (XPending(_display) == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            XNextEvent(_display, &xev);
            if (xev.type != 35 /* GenericEvent */) continue;

            var cookie = &xev.GenericEventCookie;
            if (cookie->extension != _xiOpcode) continue;
            if (!XGetEventData(_display, cookie)) continue;

            try { ProcessEvent(cookie); }
            finally { XFreeEventData(_display, cookie); }
        }
    }

    private void ProcessEvent(XGenericEventCookie* cookie)
    {
        var ev = (XIDeviceEvent*)cookie->data;
        if (ev == null || ev->deviceid != _penDeviceId) return;

        var evtype = cookie->evtype;
        if (evtype != XI_ButtonPress && evtype != XI_ButtonRelease && evtype != XI_Motion)
            return;

        // Only handle tip button (button 1) for press/release
        if ((evtype == XI_ButtonPress || evtype == XI_ButtonRelease) && ev->detail != 1)
            return;

        var pressure = ReadPressure(ev);

        // THE fix: ignore tip-down events with zero pressure — these are proximity ghosts
        if (evtype == XI_ButtonPress && pressure == 0) return;

        var phase = evtype switch
        {
            XI_ButtonPress   => NativeTabletPhase.Down,
            XI_ButtonRelease => NativeTabletPhase.Up,
            _                => NativeTabletPhase.Move
        };

        var sample = new NativeTabletSample(ev->event_x, ev->event_y, pressure, phase);
        Dispatcher.UIThread.Post(() => SampleReceived?.Invoke(sample));
    }

    private double ReadPressure(XIDeviceEvent* ev)
    {
        var vs = &ev->valuators;
        if (vs->Values == null || vs->Mask == null) return 0;

        int bit = _pressureValuatorIndex;
        // Check if this valuator is present in the event mask
        if (bit / 8 >= vs->MaskLen) return 0;
        if ((vs->Mask[bit / 8] & (1 << (bit % 8))) == 0) return 0;

        // Count how many set bits come before our valuator to find its index in Values[]
        int idx = 0;
        for (int b = 0; b < bit; b++)
        {
            if (b / 8 < vs->MaskLen && (vs->Mask[b / 8] & (1 << (b % 8))) != 0)
                idx++;
        }

        var raw = vs->Values[idx];
        return Math.Clamp((raw - _pressureMin) / (_pressureMax - _pressureMin), 0.0, 1.0);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        if (_display != 0) { XCloseDisplay(_display); _display = 0; }
    }
}

public readonly record struct NativeTabletSample(
    double X,
    double Y,
    double Pressure,
    NativeTabletPhase Phase);

public enum NativeTabletPhase { Down, Move, Up }
