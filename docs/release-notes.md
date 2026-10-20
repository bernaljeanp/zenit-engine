**Install:** download `ZenitSetup-<version>.exe` below and run it; it updates an existing install in place. It asks
for administrator rights once, installs to `C:\Program Files\Zenit Engine`, starts the Engine and puts Zenit in the
tray. Uninstall from Settings > Apps.

> Not code-signed yet (a free certificate from SignPath Foundation has been requested). Windows SmartScreen may say
> "Windows protected your PC": choose **More info > Run anyway**. Check the download against `SHA256SUMS.txt`
> (`sha256sum -c SHA256SUMS.txt`); every release is built from this repository by GitHub Actions.

**What's new in 0.1.2**

- Driver check: devices Windows labels "Degraded" only because they run on a legacy-style kernel driver (Intel's SoC
  Thermal, PMT and PerfMon IPF providers on recent Intel laptops) are no longer listed at all. Their drivers run
  normally; there is nothing to fix.

**0.1.1**

- Much lighter in the tray: the window is created when you open it and freed when you close it (about 97 MB of RAM in
  the tray instead of 330 MB).
