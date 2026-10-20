using System.Runtime.InteropServices;

// The Alienware function keys that Alienware Command Center used to handle. The keyboard firmware reports them as
// the unused virtual keys F17-F20 (captured on a 16X Aurora); nothing in Windows acts on those, so without AWCC they
// did nothing. RegisterHotKey on the Engine's own thread: no keyboard hook, nothing sees other keystrokes.
//   F6 -> F17  Performance Boost: performance profile, again for the previous one
//   F7 -> F20  Stealth: dim white lights and quiet profile, as Dell's firmware does; again to put both back
//   F9 -> F18  Keyboard lights: full, low, off
static class Hotkeys
{
    const int Boost = 1, Stealth = 2, Lights = 3;
    const uint VK_F17 = 0x80, VK_F18 = 0x81, VK_F20 = 0x83, MOD_NOREPEAT = 0x4000, WM_HOTKEY = 0x0312;

    static string? beforeBoost, beforeStealth;

    // What the last key did, for the app's on-screen notice. The app creates the event; the Engine signals it.
    public record Notice(int Seq, string Title, string Detail);
    static Notice last = new(0, "", "");
    public static Notice Last => Volatile.Read(ref last);

    static void Notify(string title, string detail)
    {
        Volatile.Write(ref last, new Notice(last.Seq + 1, title, detail));
        try { using var signal = EventWaitHandle.OpenExisting(@"Local\ZenitEngineNotice"); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { } // the app isn't running
    }

    static string Name(string profile) => profile switch
    {
        "balanced-performance" => "Balanced+", "low-power" => "Battery saver",
        _ => profile.Length > 0 ? char.ToUpperInvariant(profile[0]) + profile[1..] : profile,
    };

    public static void Start() => new Thread(Run) { IsBackground = true, Name = "Hotkeys" }.Start();

    static void Run()
    {
        RegisterHotKey(0, Boost, MOD_NOREPEAT, VK_F17);
        RegisterHotKey(0, Stealth, MOD_NOREPEAT, VK_F20);
        RegisterHotKey(0, Lights, MOD_NOREPEAT, VK_F18);
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message != WM_HOTKEY) continue;
            try { Handle((int)msg.wParam); }
            catch (Exception e) when (e is not OutOfMemoryException) { } // no thermal interface or lighting controller: the key just does nothing
        }
    }

    static void Handle(int key)
    {
        switch (key)
        {
            case Boost:
                if (beforeBoost is null) { beforeBoost = Thermal.Current(); Thermal.Set("performance"); Notify("Performance Boost", "On · fans and clocks up"); }
                else { Thermal.Set(beforeBoost); Notify("Performance Boost", $"Off · back to {Name(beforeBoost)}"); beforeBoost = null; }
                break;
            case Stealth:
                var saved = Lighting.Saved() ?? new Lighting.State("FFFFFF", 100);
                if (beforeStealth is null)
                {
                    beforeStealth = Thermal.Current();
                    Lighting.Set("FFFFFF", 15, remember: false);
                    Thermal.Set("quiet");
                    Notify("Stealth", "On · dim lights, Quiet profile");
                }
                else
                {
                    Lighting.Set(saved.Color, saved.Brightness);
                    Thermal.Set(beforeStealth);
                    Notify("Stealth", $"Off · {Name(beforeStealth)} and your lights back");
                    beforeStealth = null;
                }
                break;
            case Lights:
                var now = Lighting.Saved() ?? new Lighting.State("FFFFFF", 100);
                var level = Lighting.NextLevel(now.Brightness);
                Lighting.Set(now.Color, level);
                Notify("Keyboard lights", level switch { 0 => "Off", < 100 => "Low", _ => "Full" });
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32")] static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32")] static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
}
