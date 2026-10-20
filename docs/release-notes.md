**Install:** download `ZenitSetup-<version>.exe` below and run it; it updates an existing install in place. It asks
for administrator rights once, installs to `C:\Program Files\Zenit Engine`, starts the Engine and puts Zenit in the
tray. Uninstall from Settings > Apps.

> Not code-signed yet (a free certificate from SignPath Foundation has been requested). Windows SmartScreen may say
> "Windows protected your PC": choose **More info > Run anyway**. Check the download against `SHA256SUMS.txt`; every
> release is built from this repository by GitHub Actions.

**What's new in 0.1.1**

- Much lighter in the tray: the window is created when you open it and freed when you close it. In the tray Zenit now
  uses about 97 MB of RAM instead of 330 MB, and about 40 MB after closing the window.
- Driver check: devices Windows shows as "Degraded" only because they run on a legacy-style kernel driver (Intel's
  SoC Thermal, PMT and PerfMon IPF providers) are now reported as information with "nothing to fix", not as a fault.
