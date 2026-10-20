First public release of Zenit Engine, a free and open-source companion for gaming laptops.

**Install:** download `ZenitSetup-<version>.exe` below and run it. It asks for administrator rights once, installs to
`C:\Program Files\Zenit Engine`, starts the Engine and puts Zenit in the tray. Uninstall from Settings > Apps.

> This build is not code-signed yet (a free certificate from SignPath Foundation has been requested). Windows
> SmartScreen may say "Windows protected your PC": choose **More info > Run anyway**. You can check the download
> against `SHA256SUMS.txt`, and every release is built from this repository by GitHub Actions.

**What's in it**

- Live CPU and GPU temperatures, fan speeds and a plain-language diagnosis, including the process behind a hot CPU at
  low load.
- Thermal profiles, switched automatically when a game starts from any launcher.
- Driver updates from official sources only: the Windows Update driver catalog and, on Dell and Alienware, Dell's
  signed catalog. Newer versions only, restore point first.
- Alienware: AlienFX keyboard lighting (colors, brightness, Aurora) and the F6 / F7 / F9 function keys, without
  Alienware Command Center.
- English and Spanish, picked from the Windows display language.

**Requirements:** Windows 10 version 2004 or Windows 11, 64-bit. Thermal control, fans and lighting are tested on an
Alienware 16X Aurora; the driver check and the Windows power-mode fallback work on any PC.
