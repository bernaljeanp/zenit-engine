**Install:** download `ZenitSetup-<version>.exe` below and run it; it updates an existing install in place. It asks
for administrator rights once, installs to `C:\Program Files\Zenit Engine`, starts the Engine and puts Zenit in the
tray. Uninstall from Settings > Apps.

> Not code-signed yet. Windows SmartScreen may say "Windows protected your PC" (**More info > Run anyway**), and on PCs
> with **Smart App Control** on, Windows blocks the installer; install from source there (see the README). Check the
> download with `sha256sum -c SHA256SUMS.txt`; every release is built from this repository by GitHub Actions.

**What's new in 0.1.3**

- Everyday profile by power source: **Balanced+ on the charger, Balanced on battery**, applied at sign-in and when you
  plug or unplug, with an on-screen notice. A profile you pick by hand stays until the next change of source; a game
  keeps its own profile.
- Installer: file times are recorded in UTC (installed files were dated hours in the future), and `install.ps1`
  replaces every file and keeps the setup's uninstaller.
- [Field notes](https://github.com/bernaljeanp/zenit-engine/blob/main/docs/field-notes.md): what was measured on the
  Alienware 16X Aurora that shaped all of this.

**0.1.2** — devices on legacy-style kernel drivers (Intel's IPF providers) are no longer listed as problems.
**0.1.1** — about 97 MB of RAM in the tray instead of 330 MB.
