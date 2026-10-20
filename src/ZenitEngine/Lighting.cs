using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Keyboard and chassis lighting on Alienware laptops without Alienware Command Center. The lights sit behind the
// "AW-ELC" USB HID controller (vendor 0x187C, 34-byte output reports), the generation alienfx-tools calls API v4.
// Byte layout from alienfx-tools by T-Troll (MIT, github.com/T-Troll/alienfx-tools, AlienFX-SDK/alienfx-controls.h):
//   03 21 00 <op> ff ff  control: 1 start a new set, 3 finish and play, 4 remove the running one
//   03 27 R G B 00 <n> <ids...>  one color for up to 26 light IDs
//   03 26 <100-brightness> 00 <n> <ids...>  brightness for up to 28 light IDs
// The controller answers HidD_GetInputReport with its state in byte 2 (34 = busy).
static class Lighting
{
    public record State(string Color, int Brightness);

    // ponytail: every ID the protocol can address in one command; IDs the controller doesn't have are ignored.
    // A per-zone map (keyboard, logo, rear) needs probing each ID on real hardware.
    static readonly byte[] AllLights = Enumerable.Range(0, 26).Select(i => (byte)i).ToArray();

    static string StateFile => Path.Combine(Catalog.DataDir, "lighting.json");

    public static State? Saved()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    // Color as "RRGGBB", brightness 0..100. Remembered so the Engine can put it back at sign-in.
    public static void Set(string color, int brightness)
    {
        if (color.Length != 6 || !int.TryParse(color, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            throw new ArgumentException("color must be RRGGBB");
        brightness = Math.Clamp(brightness, 0, 100);
        using var device = Open() ?? throw new PlatformNotSupportedException("no Alienware AW-ELC lighting controller found");
        var (r, g, b) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        WaitReady(device);
        Send(device, [0x03, 0x21, 0x00, 0x04, 0xff, 0xff]);
        Send(device, [0x03, 0x21, 0x00, 0x01, 0xff, 0xff]);
        Send(device, [0x03, 0x27, r, g, b, 0x00, (byte)AllLights.Length, .. AllLights]);
        Send(device, [0x03, 0x21, 0x00, 0x03, 0xff, 0xff]);
        WaitReady(device);
        Send(device, [0x03, 0x26, (byte)(100 - brightness), 0x00, (byte)AllLights.Length, .. AllLights]);

        Directory.CreateDirectory(Catalog.DataDir);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(color.ToUpperInvariant(), brightness)));
    }

    public static void Restore()
    {
        if (Saved() is { } s) Set(s.Color, s.Brightness);
    }

    const int ReportLength = 34;

    static void Send(SafeFileHandle device, byte[] command)
    {
        var buffer = new byte[ReportLength]; // byte 0 is the report ID, 0 on this controller
        command.CopyTo(buffer, 1);
        if (!HidD_SetOutputReport(device, buffer, buffer.Length))
            throw new IOException($"lighting controller refused a report (error {Marshal.GetLastWin32Error()})");
    }

    static void WaitReady(SafeFileHandle device)
    {
        var buffer = new byte[ReportLength];
        for (var i = 0; i < 50; i++)
        {
            if (!HidD_GetInputReport(device, buffer, buffer.Length) || buffer[2] != 34) return;
            Thread.Sleep(20);
        }
    }

    static SafeFileHandle? Open()
    {
        HidD_GetHidGuid(out var hid);
        if (CM_Get_Device_Interface_List_Size(out var size, ref hid, null, 0) != 0 || size <= 1) return null;
        var list = new char[size];
        if (CM_Get_Device_Interface_List(ref hid, null, list, size, 0) != 0) return null;
        foreach (var path in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!path.Contains("vid_187c", StringComparison.OrdinalIgnoreCase)) continue;
            var handle = CreateFile(path, 0xC0000000 /* read | write */, 3 /* share read | write */, 0, 3 /* open existing */, 0, 0);
            if (handle.IsInvalid) continue;
            if (OutputLength(handle) == ReportLength) return handle;
            handle.Dispose();
        }
        return null;
    }

    static int OutputLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var data)) return 0;
        try { return HidP_GetCaps(data, out var caps) == 0x110000 /* HIDP_STATUS_SUCCESS */ ? caps.OutputReportByteLength : 0; }
        finally { HidD_FreePreparsedData(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] report, int length);
    [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] report, int length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
}
