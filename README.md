# Zaopin Battery Tray

Shows the battery level of a Zaopin wireless mouse (tested with the Z4D) in the
Windows notification area, and opens the Zaopin Control Hub as an offline
desktop window.

## Features

- Tray icon with the battery percentage and a fill bar: green above 50%,
  yellow down to 21%, red at 20% and below, blue while charging, grey with the
  last known level while the mouse sleeps.
- Low-battery notification at 20%.
- Estimated time left, learned from how fast the battery drains while the
  mouse is awake.
- **Open Control Hub** (double-click the icon): the Zaopin web configurator in
  its own Edge app window, running from an offline copy on this PC.
- **Update offline copy**: re-downloads the Control Hub from controlhub.top.
- **Start with Windows** toggle.

## Build

No SDK needed — it uses the C# compiler that ships with Windows (.NET Framework 4.x):

```
build.cmd
```

This produces `ZaopinBatteryTray.exe` next to the source. A prebuilt exe is
attached to the GitHub releases.

## How it works

- **Battery:** the 2.4 GHz receiver (CompX chipset, USB VID `3554`, Z4D PID
  `F527`) exposes a vendor HID collection (usage page `FF02`, 17-byte reports).
  Sending report `08 04 00…` with a checksum byte making all bytes sum to `0x55`
  returns the battery percentage in byte 6, a charging flag in byte 7 and the
  voltage in millivolts in bytes 8–9. Polled once a minute, and paused while the
  Control Hub is open since it uses the same channel.
- **Control Hub:** the hub is a WebHID web app. The tray app downloads it to
  `%LOCALAPPDATA%\ZaopinBatteryTray\HubCache` and serves it from
  `http://127.0.0.1:47811/` (loopback only). Edge opens it in app mode with its
  own profile in `%LOCALAPPDATA%\ZaopinBatteryTray\HubProfile`. Files missing
  from the copy are fetched and saved on first use while online. The Control
  Hub itself is Zaopin's software and isn't included in this repository.

## Command-line checks

```
ZaopinBatteryTray.exe --test      # print one battery reading
ZaopinBatteryTray.exe --download  # download the offline Control Hub
ZaopinBatteryTray.exe --serve     # serve the offline hub without the tray icon
```

Output shows when run from a console (or build a console copy with `/target:exe`).
