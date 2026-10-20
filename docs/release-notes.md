**Install:** download `ZenitSetup-<version>.exe` below and run it; it updates an existing install in place. It asks
for administrator rights once, installs to `C:\Program Files\Zenit Engine`, starts the Engine and puts Zenit in the
tray. Uninstall from Settings > Apps.

> Not code-signed yet. Windows SmartScreen may say "Windows protected your PC" (**More info > Run anyway**), and on PCs
> with **Smart App Control** on, Windows blocks the installer; install from source there (see the README). Check the
> download with `sha256sum -c SHA256SUMS.txt`; every release is built from this repository by GitHub Actions.

**What's new in 0.1.4**

- **Battery charge limit** on Dell and Alienware laptops: stop charging at **80 %** or **60 %** (charging resumes 5
  points below), or keep Dell's Adaptive mode. Saved in the BIOS through Dell's BIOS settings interface, so it holds
  without Zenit running. Needs no BIOS password set.
- **Settings panel** (the Settings button, top right): the charge limit, profile by power source on or off, start
  with Windows, language (Windows, English, Spanish; switches in place) and a thermal profile for each installed game.
- The window fits the screen above the taskbar at high display scaling, and game titles such as "The Sims™ 4" show
  correctly.

**0.1.3** — Balanced+ on the charger and Balanced on battery, automatically; installer timestamps in UTC;
[field notes](https://github.com/bernaljeanp/zenit-engine/blob/main/docs/field-notes.md).
**0.1.2** — devices on legacy-style kernel drivers (Intel's IPF providers) are no longer listed as problems.
**0.1.1** — about 97 MB of RAM in the tray instead of 330 MB.
