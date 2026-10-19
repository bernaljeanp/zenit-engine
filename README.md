# Zenit Engine

[![Build](https://github.com/bernaljeanp/zenit-engine/actions/workflows/build.yml/badge.svg)](https://github.com/bernaljeanp/zenit-engine/actions/workflows/build.yml)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue.svg)](LICENSE)

**Drivers, temperatures and performance for gaming laptops, from official sources only.**
Free and open source. Early version, tested on Alienware laptops; more brands are next.

![Zenit Engine](docs/screenshot.png)

My Alienware 16X Aurora sat at **104 °C doing nothing** for two days, fans roaring, and the manufacturer's app
said nothing. Zenit Engine found the cause (a power plan that never let the CPU slow down, plus a stuck state)
and the laptop went back to **58-66 °C at idle**. That is the idea: tell you what is wrong, in plain words.

## What it does

- **Thermal profile per game.** Launch a game through Zenit and it switches to performance (balanced-performance
  on battery), or to the profile you picked for that game; quit and it restores what you had.
- **Live readings.** CPU and GPU temperature, fan speeds and the active profile, read from the laptop's own
  firmware interface.
- **Driver check, official sources only.** Lists devices Windows reports as failing (error codes, "Degraded"),
  vendor drivers for physical hardware older than three years, and the updates Microsoft's own Windows Update
  driver catalog offers for your exact hardware IDs. It never downloads or installs drivers by itself.
- **Works on any PC, best on Alienware.** Without a supported laptop it falls back to the Windows power mode.

## What it will never do

- Install drivers from its own mirror. Tools that do this have shipped wrong and generic drivers.
- Load a third-party kernel driver. On Alienware it talks to Dell's own AWCC WMI interface, the one Alienware
  Command Center uses, so anti-cheat and Windows security are unaffected.
- Touch games or anti-cheat, or send data anywhere. No telemetry, no ads, no bundled software.

## Supported laptops

| Brand | Status |
|---|---|
| Alienware (2020 and newer, AWCC WMI) | Working: profiles, fans, temperatures. Tested on a 16X Aurora (2025). |
| Dell G-series | Likely (same interface), untested |
| MSI, HP Omen/Victus, Acer | Planned. [Request your model](../../issues/new?template=laptop-support.yml). |
| Others | The Windows power mode fallback and the driver check work on any Windows PC |

## Install

Requires Windows 10 version 2004 or Windows 11, and the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

```powershell
git clone https://github.com/bernaljeanp/zenit-engine.git
cd zenit-engine
.\install.ps1          # asks for admin once
```

It publishes a self-contained build to `%LOCALAPPDATA%\Programs\ZenitEngine` and registers the Engine to start
at sign-in, and adds **Zenit Engine** to the Start menu. To remove it: `.\install.ps1 -Uninstall` (your per-game profiles in `%LOCALAPPDATA%\Zenit` are kept).

## Command line

Everything the app shows is also available as JSON:

```powershell
$z = "$env:LOCALAPPDATA\Programs\ZenitEngine\ZenitEngine.exe"
& $z --thermal                       # profiles, fans and temperatures as JSON
& $z --thermal performance           # switch the thermal profile
& $z --drivers                       # driver check (add --offline to skip Windows Update)
& $z                                 # installed games (Steam, Epic, GOG, Xbox, Ubisoft, Riot) as JSON
& $z --launch steam:730              # launch with the game's thermal profile; restores the previous one on exit
& $z --game-profile steam:730 quiet  # remember a profile for a game
```

## How it works

- `ZenitEngine --serve` is the Engine. A scheduled task starts it elevated at sign-in (no console window, no UAC
  prompt at each sign-in). It answers on the local named pipe `ZenitEngine`, which only the current user can open,
  so nothing else in the program needs admin rights.
- Thermal control lives in [`Thermal.cs`](src/ZenitEngine/Thermal.cs). The AWCC opcodes come from the Linux kernel's
  [Dell AWCC WMI documentation](https://docs.kernel.org/wmi/devices/alienware-wmi.html).
- The driver check lives in [`Drivers.cs`](src/ZenitEngine/Drivers.cs): a WMI device inventory plus the Windows
  Update Agent API, search only.
- `ZenitEngine --selftest` runs the parser and policy checks; CI runs it on every push.

## Contributing

The most useful contribution is a **brand connector**: the WMI or ACPI calls a laptop uses for its thermal
profiles and fans, documented and tested on real hardware. Start with a
[laptop support request](../../issues/new?template=laptop-support.yml). Security issues: see [SECURITY.md](SECURITY.md).

## Support the project

Zenit Engine is free. If it helped your laptop, you can support its development with the **Sponsor** button
on this page.

## License

[GPL-3.0](LICENSE). Not affiliated with Dell, Alienware, Lenovo, ASUS, HP, MSI, Acer, Microsoft or any other
brand named here; their names only identify the hardware and software it works with.
