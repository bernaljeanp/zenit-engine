# Field notes: an Alienware 16X Aurora, taken apart from the software side

Everything Zenit Engine does on Alienware laptops came out of debugging one machine: an **Alienware 16X Aurora
(AC16251)**, Core Ultra 9 275HX, RTX 5070, Windows 11 Home 26H2. These notes record what was measured and how, so the
same problems can be recognized, and fixed, on other laptops. Numbers are from that machine.

---

## 1. 104 °C doing nothing: a power plan

**Symptom.** CPU at 100-104 °C for days, fans loud, with almost no load.

**Cause.** The active power plan ("High performance") had the *minimum processor state* at 100 % on AC, so the CPU
never dropped its clocks. Switching to *Balanced* and restarting brought idle to 58-66 °C.

**How Zenit catches it.** It reads `powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN` (the AC value is the
second-to-last hex value; the setting names are localized, the hex values are not) and warns when it is 100 %.

---

## 2. 86 °C idle again: Alienware Command Center's process monitor

**Symptom.** Weeks later, idle temperatures of 80-103 °C with 3-15 % load. Task Manager showed nothing obvious.

**Finding the culprit.**

1. Per-process CPU from `Win32_PerfFormattedData_PerfProc_Process` (it includes protected processes that
   `Get-Process` cannot read) showed `WmiPrvSE.exe`, the WMI provider host, at **~180 % of one core**, all the time.
2. `MSFT_Providers` showed that host serving `CIMWin32` and `WhqlProvider`.
3. The WMI-Activity **Trace** log (analytic, off by default; enabled for 30 s with `wevtutil sl ... /e:true`) named the
   client: `AWCC.SCSubAgent`, with **~550 `IWbemServices::Connect` calls and ~550
   `SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId = N` queries every 30 s**.
4. Stopping only that agent for a minute: WMI dropped to 2 %, the CPU to 62 °C.

**Why it does that.** AWCC.SCSubAgent is a .NET 8 service. Its process-monitor plugin subscribes to
`Win32_ProcessStartTrace` and `Win32_ProcessStopTrace`, and for **every process start and every process stop** it opens
a new WMI connection and queries `Win32_Process` for that PID (even for processes that already exited). On a machine
that starts many short-lived processes (developer tools, git, build systems, browsers' helpers) that becomes a
continuous load on WMI, and on the CPU's thermals.

**What does not work.** Restarting the service or killing the WMI host: the load returns within seconds. Patching
is not an option either: the agent validates the Authenticode signature of each plugin assembly before loading it.
The agent loads plugins from a fixed list of file names, so renaming that plugin's DLL disables it (verified: WMI back
to 0 %, 58 °C), but an AWCC update restores it.

**Fix used here.** Uninstalling Alienware Command Center. Result: WMI 0-1 %, CPU 58-62 °C idle.

**What Zenit does differently.** Zenit's game detection lists processes every 3 s with `Process.GetProcesses()` and
classifies each new PID once, by image path against the installed games' folders (`QueryFullProcessImageName`), or by
package family for Xbox/GDK games. No WMI per process. Its idle-heat detector names the process behind a hot CPU at
low load, and for `WmiPrvSE` explains that a background utility is driving it.

---

## 3. Lighting without AWCC: the AW-ELC controller

Removing AWCC removes lighting control. The lights sit behind a USB HID device, **`VID_187C&PID_0551`, product string
"AW-ELC"**, vendor usage page `0xFF00`, 34-byte input and output reports. That is the generation
[alienfx-tools](https://github.com/T-Troll/alienfx-tools) (MIT) calls *API v4*; its SDK documents the bytes. Reports
go out with `HidD_SetOutputReport`, report ID 0; the controller state is read with `HidD_GetInputReport`, byte 2
(34 = busy).

| Bytes | Meaning |
|---|---|
| `03 21 00 <op> ff ff` | control: `04` drop the running set, `01` start a new one, `03` finish and play |
| `03 27 R G B 00 <n> <ids…>` | one color for up to 26 light IDs |
| `03 26 <100-brightness> 00 <n> <ids…>` | brightness for up to 28 light IDs |
| `03 23 01 00 <n> <ids…>` | select lights for an effect, looping |
| `03 24` + up to 3 × `<type> <time> <code> 00 <tempo> R G B` | effect phases (type 2 / code `0xCF` = morph) |

Zenit addresses IDs 0-25 in one command (IDs the controller lacks are ignored), and builds the **Aurora** effect as a
looping morph through green, teal, blue, violet and magenta. It does not need administrator rights.

---

## 4. The F6 / F7 / F9 keys

Without AWCC the Alienware function keys did nothing. A low-level keyboard hook showed why: the firmware reports them
as **virtual keys Windows does not use**.

| Key | Arrives as | Function |
|---|---|---|
| F6 | `VK_F17` (0x80), scan 0x68, plus a `root\wmi` `BIOSEvent` | Performance Boost |
| F7 | `VK_F20` (0x83), scan 0x6B | Stealth: dim white lights and Quiet profile (as Dell's firmware does) |
| F9 | `VK_F18` (0x81), scan 0x69 | keyboard lights: full, low, off |

Zenit's Engine registers the three with `RegisterHotKey` (no keyboard hook) and shows a short on-screen notice.

---

## 5. Thermal profiles: what they actually do

Profiles are switched through Dell's own AWCC WMI interface (`AWCCWmiMethodFunction`, `Thermal_Control`), the same one
Alienware Command Center uses; opcodes as documented by the Linux kernel's
[Dell AWCC WMI documentation](https://docs.kernel.org/wmi/devices/alienware-wmi.html).

Measured at idle, 20 s after each switch:

| Profile | Fans (CPU / GPU) | CPU |
|---|---|---|
| Balanced | **0 / 0 rpm** (fans stop) | 66-82 °C |
| Balanced+ | ~2,200 rpm | 64-74 °C |
| Cool | ~2,900 rpm | 65 °C |
| Quiet | ~2,200 rpm | 69 °C |
| Performance | rising to ~4,000 rpm | 66 °C |
| Balanced, **on battery** | 0 rpm | **49-53 °C** (CPU power is capped) |

So Zenit uses **Balanced+ on the charger and Balanced on battery**, switching when the charger is plugged or
unplugged; a game gets Performance (Balanced+ on battery) and the previous profile comes back when it closes.

---

## 6. "Degraded" Intel drivers that are not broken

Three Intel IPF extension providers (**SoC Thermal, PMT, PerfMon**, driver 3.4.1007.0) show as **Degraded** in
`Win32_PnPEntity` and `Get-PnpDevice`, with problem code 0, on the latest BIOS and IPF package. `CM_Get_DevNode_Status`
gives `0x0180300A`, against `0x0180200A` for the healthy providers: the extra bit is **`0x1000`, `DN_LEGACY_DRIVER`**
in `cfg.h`, "this device is using a legacy driver". Their kernel drivers (`PRI-Driver`, `IntelTACD`) are running.
Windows labels them Degraded because of how they are built, not because anything failed; nothing to fix. Zenit checks
that bit and leaves such devices out of its problem list.

---

## 7. Driver updates that go backwards

Installing everything the Windows Update driver catalog offered replaced Intel graphics **32.0.101.8991 with
32.0.101.8860**: an older version, offered because of a later date. Zenit now compares the version in the update's
title with the version every targeted device runs, and skips anything that is not newer. Dell's per-model catalog
(`CatalogIndexPC.cab`, signed by Dell; per-model cabs checked against the SHA-256 in the index; each package checked
against its catalog SHA-256 and Dell's Authenticode signature) is matched the same way, by hardware ID and version.

---

## 8. Distribution on Windows 11: Smart App Control

The installer (Inno Setup, built by GitHub Actions) installs to `C:\Program Files\Zenit Engine`. That matters: the
Engine runs elevated at sign-in, so it must not live where an unelevated program could replace it (early builds used
`%LOCALAPPDATA%`, which any user process can write).

With **Smart App Control** on (the default on clean Windows 11 installs), an unsigned installer is blocked outright,
with no "Run anyway". Code signing is required; the project is working on it.
