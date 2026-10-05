# BladeCtl 2.7.0

A small native app that replaces Razer Synapse on the **Razer Blade 15 Advanced (Mid 2021), RZ09-0409, VID 0x1532 / PID 0x0276** and drives the Razer accessories plugged into it. No services, no telemetry, no login, no auto-updater. One process, one window, one tray icon.

It talks to each device directly over its Razer HID control interface, using packet formats verified against OpenRazer, OpenRGB, razer-laptop-control and Synapse 4's own device modules on this exact hardware. Every write is followed by a read-back where the device has one.

> **Unofficial.** Not affiliated with, endorsed by or supported by Razer Inc. It has only been tested on the RZ09-0409 and the accessories listed below. It sends fan, power-mode and lighting commands to the laptop's embedded controller; the firmware's own thermal protection is never touched, but you use it at your own risk.

---

## Running it

After a build (see [Build](#build)) everything lives in `publish-fd\`: `BladeCtl.exe` (the app), `README.md` (this file), `analog-test.cmd` (30-second keystroke-origin test), `tools\BladeProbe.exe` (diagnostics), and, when asked for, `bladectl-dump.txt` (drop `dump-request.txt` next to the exe) and `kbd-watch.txt`.

`publish-fd\BladeCtl.exe` opens the control panel. The elevated logon task **BladeCtl** starts it hidden (`--tray`) five seconds after you sign in.

- **Blade view**: what the laptop reports on the left (temperatures, fan RPM per zone, power mode), what you ask for on the right (performance mode with Custom boost levels, fan floor or temperature target, lighting and lid logo, startup behaviour).
- **Devices view**: one card per Razer product on the system. Controllable ones get Off / Static / Spectrum / Breathing / Wave / Reactive / Starlight, a colour and a brightness; the keyboard also gets game mode and polling rate; the rest say why not. **Match the Blade** sends the laptop's lighting to every accessory at once.
- **Bottom strip** is the live log.
- **RESTORE AUTO FAN** is one click away; **Ctrl+Alt+F** does it from any app.
- Closing the window hides it to the tray. **Exit** is in the tray menu.

### Command line

```
BladeCtl.exe                      open the control panel
BladeCtl.exe --tray               start hidden (what the logon task uses)
BladeCtl.exe --status             device + app state
BladeCtl.exe --devices            every Razer product and whether it can be controlled
BladeCtl.exe --accessory <pid> <Off|Static|Spectrum|Breathing|Wave|Reactive|Starlight> [r g b] [brightness] [--logon]
BladeCtl.exe --keyboard <pid> gamemode on|off       Windows key lock, read back
BladeCtl.exe --keyboard <pid> polling 125|500|1000  polling rate, read back
BladeCtl.exe --cmd <line>         queue a command for the running instance (see "Command channel")
BladeCtl.exe --analog on|off      Huntsman analog engine; "off" also works with no instance running (emergency)
BladeCtl.exe --log [N]            last N log lines
BladeCtl.exe --selftest           round-trip power mode and brightness on the Blade, then restore
BladeCtl.exe --restore-auto       restore firmware fan control and exit
BladeCtl.exe --exit               ask the running BladeCtl to quit
BladeCtl.exe --capture [png] [--view blade|devices]   save a screenshot of the window
BladeCtl.exe --apply-boot-lighting --settings <file> --logdir <dir>   what the boot task runs
BladeCtl.exe --power-probe <seconds>   read-only Power card sampling (no instance needed): EC GETs only, NVML only
                                       while the NVIDIA GPU is already awake; writes power-probe-<stamp>.txt next to the exe
BladeCtl.exe --power-preview <png> [barrel|usbc|battery][+open] [width]   render the Power card from sample data (no hardware)
```

Settings: `%APPDATA%\BladeCtl\settings.json`. Log: `%APPDATA%\BladeCtl\bladectl.log`. Dropping an empty `dump-request.txt` next to the exe makes the running instance write `bladectl-dump.txt` and `bladectl-window.png` beside it within five seconds.

---

## What it controls

| Device | Controls | Read-back |
|---|---|---|
| Blade 15 Advanced (Mid 2021) | Power mode Balanced / Gaming / Creator / **Custom with CPU boost (Low–Boost) and GPU boost (Low–High)**; fan floor; hold-temperature loop; lighting Off / Static / Spectrum / Breathing / Wave / Reactive / Starlight with direction and speed; brightness; **lid logo Off / On / Blink** | mode, boost levels, fan, brightness and logo all read back; effects are write-only on this controller |
| Huntsman V2 Analog | seven effects, colour, brightness, **game mode (Windows key lock)**, **polling 125 / 500 / 1000 Hz** | everything reads back, effect included; stored in the keyboard's own memory |
| Thunderbolt 4 Dock Chroma | effects, colour, brightness | effect and brightness read back; onboard memory |
| Leviathan V2 X | effects, colour, brightness | brightness reads back; the effect register returns unrelated data, so effects show as *accepted*. HID feature report id 0x07. Audio works through Windows. |
| BlackShark V2 HyperSpeed | none | No lighting. EQ, mic and sidetone use an undocumented 64-byte protocol; audio works through Windows. |

Outcomes are **Confirmed**, **Accepted (cannot be read back)**, **Acked but device disagrees**, **Partly applied**, **Rejected** or **No device**.

### Things Synapse does not do

- **Hold temperature** for the fans (below).
- **Lighting before sign-in** (below).
- **Lights off while locked**, and optionally after 5 / 15 / 30 idle minutes, on the Blade and every accessory together; everything comes back at unlock or first input. Accessories are dimmed with the volatile flag, so their stored setting is never touched.
- **Match the Blade**: one click sends the laptop's effect, colour and brightness to the keyboard, dock and speaker.
- A live "device reports …" line under every control, so a setting Synapse or firmware silently reverted is visible.

### The Huntsman's analog keys

The per-key actuation point on this keyboard is **not a keyboard setting on this generation** ("analogV1" in Synapse's own code). Verified on 2026-09-05:

- The keyboard's single-key actuation register (class 0x02, id 0x11 / 0x91) acknowledges a write and then reads back the factory value again.
- Synapse's on-board profile dump for this keyboard stores zero for every key's actuation.
- The newer multi-key actuation, rapid-trigger and analog-report-mode commands (0x02 0x19 / 0x1A / 0x2A and their GETs) answer NOT_SUPPORTED.
- Synapse implements adjustable actuation, dual-step and rapid trigger in software: it switches the keyboard to driver mode, reads the analog stream (HID report 7 on interface 1: pairs of key id and depth 0–255), and its `mapping_engine.dll` plus the `RzDev_0266` kernel driver turn that into keystrokes.

So with Synapse gone every key actuates at the firmware's fixed ~1.6 mm. BladeCtl 2.3 brings the adjustment back with its own engine.

### The analog engine (2.3)

A switch on the Huntsman card. **Off** (default): the keyboard is in normal mode and its firmware types at the fixed depth. **On**: BladeCtl puts the keyboard in driver mode, reads the depth stream (HID input report 7: up to 11 keys per packet, each an analog key id and a depth 0–255 over the 4 mm travel, sent on change), decides per key when a press starts and ends, and injects the keystroke with SendInput using scan codes, so every application sees a normal keyboard. It also does the key repeat Windows would otherwise do for a real keyboard, using your Windows repeat delay and rate.

- **Actuation** 0.3–3.8 mm and **Reset** (release point, always shallower than actuation) as global sliders; changes apply on the next keypress and are saved a moment later.
- **Rapid trigger**: release as soon as the key rises the set distance from its deepest point, press again as soon as it sinks that distance, anywhere below the reset point.
- **Per-key overrides**, one line each: `W A S D = 1.0 / 0.7` (key names from the keycap: `Space`, `Shift`, `Right Shift`, `Ctrl`, `Alt`, `Enter`, `F5`, `Num 4`, `Left`…). Reset is optional and defaults to 0.3 mm above actuation.
- **Block the Windows key** while the engine runs (the firmware's game mode only applies in normal mode).

Safety: while the engine runs, typing on this keyboard depends on BladeCtl staying open. Normal mode is restored when you switch it off, on exit, and when Windows shows a secure desktop (lock screen, UAC prompt, Ctrl+Alt+Del), where injected input cannot go; there the firmware types at its fixed depth and the engine resumes on the way back.

**The driver-mode invariant (2.4.2).** An audit of the whole app on 2026-09-07 found that four
separate paths could still leave the keyboard in driver mode with nothing reading it — the state where it
cannot type at all. They are fixed as one mechanism rather than four patches: the engine now tracks "we
have written driver mode" independently of the reader thread, and every exit runs through a single
teardown that restores normal mode, retries until the device confirms it, and releases the stream, the
ViGEm pad, the dial guard's global mouse hook and the guardian.

What was wrong, each verified by reading the code and reproduced on the hardware:
- `AnalogEngine.Start` set driver mode and then returned on a failed read-back without undoing it, and did
  so before the guardian existed. A SET can land on the keyboard even when the read-back fails.
- `AnalogEngine.Stop` returned early whenever there was no reader thread, so after a failed start neither
  exit, nor Dispose, nor the user's own switch could restore the keyboard.
- `SyncAnalog` gated its recovery on `why != "settings"`, and both the UI switch and `--cmd analog off`
  pass exactly `"settings"` — so the documented rescue could never run in the one state that needed it.
  It now asks the device for its mode rather than trusting the cached scan.
- The guardian tested `mode != 3`, so an unreadable mode counted as "already fine" and it exited without
  restoring — failing in precisely the flaky-read case it exists for. It now acts unless normal mode is
  positively confirmed, and skips a redundant enumeration that cost 15 seconds of dead keyboard (now 8).

Verified on the hardware: a keyboard forced into driver mode by an outside process is rescued by
`--cmd analog off`; a normal on/off cycle still restores cleanly; and a deliberate runtime abort with the
engine running (`failfast-test`) is repaired by the guardian in 8 seconds.

**Guardian (2.4.1).** Exit handlers do not run when a process is killed or when the .NET runtime aborts it, and 2.4.0 proved the cost: a second engine start in one process hit a collected window-procedure callback, the runtime aborted BladeCtl at 14:12:01 on 2026-09-06, and the keyboard sat dead in driver mode until it was replugged. Since 2.4.1 the engine starts a second, tiny BladeCtl process (`--guard-analog <pid>`) that waits on the main one and puts the keyboard back in normal mode within seconds of the main process ending for any reason. Verified with a deliberate runtime abort (`failfast-test` in the command channel, test use only): normal mode was back four seconds later. The callback bug itself is fixed as well. Manual fallbacks remain: `BladeCtl.exe --analog off` (works with no instance running), starting BladeCtl again, or re-plugging the keyboard. `tools\BladeProbe.exe --analog-capture 30` records the raw stream for diagnosis; `analog-test.cmd` records where each keystroke comes from.

Synapse's key-id table for the board lives in `src/BladeCtl.Core/huntsman-analog-keys.json` and is embedded in the app (110 keys; period and apostrophe were missing from the first extraction and are in since 2.4).

### Controller mode (2.4)

A second switch under the engine. The four stick keys (default `W A S D` = up, left, down, right) drive the **left stick of a virtual Xbox 360 controller** by depth: centred until the dead-zone depth, fully deflected at the full-tilt depth, linear between. While it is on the stick keys stop typing letters unless *Stick keys also type* is on, so a game gets clean analog movement and everything else stays keyboard and mouse. Optional buttons: `Space=A, Shift=LS, Ctrl=B, Q=LB, E=RB, R=X, F=Y, G=LT, V=RT, Tab=Back, Esc=Start`.

The controller exists only while controller mode is on (proven: it appears on the bus when switched on and is gone when switched off). It rides on the ViGEm bus driver (open source, by Nefarius) that Synapse bundled; BladeCtl ships the matching client. Two things about Razer's leftovers:

- Razer's driver attaches a permanent, idle **phantom Xbox 360 controller** to the keyboard. Games see it all the time, and it holds XInput slot 1, so games that only read the first controller would ignore BladeCtl's. Controller mode retires that device node (a reversible PnP disable, elevated) the first time it starts; `razer-pad enable` in the command channel brings it back, and it disappears for good with the Razer uninstall.
- Games with mixed input switch their prompts to controller when the stick moves and back on the next mouse move; that is the game, not the keyboard.

### Dial and media keys in driver mode

In driver mode the keyboard reports its volume dial as mouse-wheel ticks (and the dial press as a middle click) on its own mouse interface; Synapse's engine used to translate them. BladeCtl's engine now does the same: Raw Input tells it which device a wheel tick came from, a low-level mouse hook swallows the scroll when it is the keyboard's, and a volume key goes out instead. Other mice are untouched. Play/pause keeps working on its own; a media key the engine does not recognise shows up in the status line as a raw key id, and one line in the overrides box wires it: `70 = MediaNext` (also `MediaPrev`, `MediaPlay`, `Mute`, `VolumeUp`, `VolumeDown`).

### Typo guard (2.4)

Learns how deep you usually press each key (from presses that counted, carried across sessions) and ignores presses that stay under a chosen share of that depth. Someone who bottoms out most keys and brushes one to 5% of its travel gets nothing from the brush. It only ever raises the actuation point above the slider, never lowers it, and per-key learning starts after three presses of that key (before that it uses the average across keys).

Windows Dynamic Lighting also sees this keyboard: interface 4 is a standard HID LampArray with 148 lamps. Run only one of BladeCtl and Dynamic Lighting for it.

---

## Lighting before sign-in

The Blade's keyboard controller does not store a colour for power-on, so it boots into spectrum cycling. BladeCtl registers a second task, **BladeCtl Boot Lighting**, that runs as SYSTEM on the boot trigger and applies your saved Blade lighting as soon as Windows is up, which is well before the sign-in screen. The keyboard and dock do not need this: they keep their onboard setting. Turn it off with **Apply lighting before sign-in** in the Startup & Razer card.

---

## Razer Synapse

Synapse relaunches through `HKCU\...\Run\RazerAppEngine`. BladeCtl starts before Explorer reads that key, removes the value, keeps watching it, and removes it again at sign-out. Force-closing a running Synapse was seen to drop an external Razer keyboard for a moment, so **Close Synapse if it is running at logon** is off by default; **Close Synapse now** does it on demand.

With Synapse alive the Blade's control interface drops reads intermittently, and Synapse re-asserts its own lighting over anything BladeCtl sends. Keep it closed.

---

## Hold temperature

The third fan mode targets a temperature instead of an RPM. Pick the sensor (CPU, GPU or whichever is hotter) and a target from 55 to 90 °C, then **Engage**. While the sensor is more than 1 °C over target the floor rises 200 to 600 RPM every 8 seconds, inside the 3500 to 5000 range; once it is 3 °C under, the floor eases down 200 RPM every 15 seconds, and after a minute at 3500 the fans go back to the firmware curve until the sensor climbs again. It pauses on battery, gives up if the sensor stops reading for 30 seconds, and the auto-revert (CPU 97 °C, GPU 90 °C) still sits above it. The target stays armed across sign-ins. Fans are cheaper than silicon, but a target below what the machine idles at simply pins the fans at 5000.

## Command channel

`command-request.txt` next to the exe is read by the running instance within five seconds, one command per line, and `BladeCtl.exe --cmd <line>` writes it for you:

```
power balanced|gaming|creator|custom [cpu 0-3] [gpu 0-2]
boost <cpu 0-3> <gpu 0-2>          CPU: Low Medium High Boost · GPU: Low Medium High
logo off|on|blink
lighting <effect> [r g b]
lights off|on
match-blade
analog on|off · analog joystick on|off · analog guard on|off
razer-pad query|disable|enable
fan-target <55-90> [Hottest|CPU|GPU]
fan-target off
restore-auto
battery-profile fullpower           the saved Razer mode until the next time AC is seen (session only)
power-learn reset                   forget the Power card's learned data
power-usbc confirmed|unconfirmed    record test B1 (the dock-only EC reading is USB-C): drops the "USB-C?" question mark
```

Each line is executed with the same read-back as the window and logged. Every one of them was exercised live on 2026-09-05 with the device confirming each step.

## Power card (2.7.0)

The first card on the Blade view answers "where are the watts coming from, and is the CPU or GPU being held back by
it?" so a slow game on USB-C or battery is not a guess.

- **Header**: the power source as the Razer EC reports it (class `0x07`, id `0x8C`, *Get Adapter Wattage Level*):
  "230 W charger", "USB-C · 65 W" or "Battery". Windows calls both chargers "AC". Until test B1 confirms the
  dock-only reading, USB-C is shown as "USB-C?" (header, chips and sentences) with a dotted outline;
  `power-usbc confirmed` on the command channel records the confirmation.
- **Verdict strip**: one sentence on whether the GPU or the CPU is limited and by what (battery, charger, heat or
  your own setting), using GPU Glance's states and wording so the two apps never disagree. At most one button:
  **Fans to 5000**, **Switch to Gaming** or **Full power until I plug in** (session only; the panel refresh and
  the stopped NVIDIA display service stay as they are). Buttons act only when clicked.
- **Flow**: charger → laptop → battery. Line thickness is watts; chevrons show direction; the battery shows %/h and
  time to full or time left. Under the laptop: CPU / GPU / rest of the laptop.
- **CPU and GPU rows**: watts now, the current limit, and a hollow triangle at what the same load gets on the
  230 W charger. While a row is held back by power, a hatched amber zone is the watts you are missing. The reason
  line shows under HELD BACK / HOT / AT LIMIT rows; every row has it in its tooltip.
- **Battery line**: GPU Glance's battery line (time to full or time left). Click it for the sparkline (battery watts
  over the last 10 minutes), battery health, **Reset learned…** and where each number comes from.

Sources, all read-only: battery `IOCTL_BATTERY_QUERY_STATUS` (2 s fresh; WMI lags 15-20 s and is never used),
Intel RAPL package energy and processor counters (PDH), Windows' max processor state and boost (powrprof), the
NVIDIA driver through NVML **only while the GPU is already awake** (a wake-free D-state read comes first, every
time), and the Razer EC (GETs only: `0x07/0x8C`, `0x0D/0x88` real fan speed, `0x00/0xB7` logged). The app in front is
measured in cores only; no process name is shown, logged or stored, and apps on your optional private list (below)
are skipped.

GPU Glance, mentioned above, is a separate companion tray app and is not part of this repository. BladeCtl works
without it: the shared wording is built in, and its files are only read when they exist.

**Private apps (optional).** `%APPDATA%\BladeCtl\private-apps.txt` lists apps whose CPU use the card should neither
show nor learn from. Each line is a hash rather than a name, so the file names nothing: run
`BladeCtl.exe --private-hash <name>` (for example the exe name without `.exe`) and paste the 16-hex-digit line it
prints. Lines starting with `#` are ignored. The list is re-read when the app in front changes. No file means nothing
is private; a file that exists but cannot be read makes every app private.

**Learned** values (shown as "learned Oct 3") live in `%APPDATA%\BladeCtl\power-learned.json`: the NVIDIA limit
per charger and Razer mode, CPU watts and MHz per load shape, charge power per 5% of battery, and the rest of the
laptop on battery, plus the last NVIDIA driver and the USB-C confirmation. Data older than 30 days is marked
"(older data)". **Reset learned…** in the battery details or
`power-learn reset` on the command channel forgets it. `%APPDATA%\BladeCtl\power-state.json` (charger, Razer mode,
battery profile) is written on change for GPU Glance. With the window closed the card only takes one battery /
CPU sample every 30 s for the history and learning; nothing is drawn.

## Thermal safety

- Manual floor hard-clamped to 3500–5000 RPM. There is no fan-off command.
- The firmware failsafe is never touched and can always spin faster than your floor.
- While a floor is engaged, BladeCtl restores firmware control at **CPU 97 °C or GPU 90 °C**, per sensor —
  **unless the fans are already at maximum**, in which case it holds them there and says so. Reverting exists
  for a floor that is *lower* than the firmware would run; at full speed it would only spin the fans down.
  Measured on this machine 2026-09-07: reverting at 97 °C dropped the fans from 4800 to 2900 RPM, i.e. the
  safety feature made the laptop hotter. When you are at max and still climbing, the lever is power, not air.
- Exiting with a floor engaged asks first. Switching to battery reverts the floor by default.

### Where the temperatures come from (fixed in 2.5.0)

Until 2.5.0 the CPU reading came from the first ACPI thermal zone. This chassis exposes exactly one zone,
`\_TZ.TZ00`, and it is a skin sensor pinned near 28 °C — so the app reported a constant 28 in all 108
logged heartbeats and **the CPU side of the thermal auto-revert could never fire.** The GPU side, from
`nvidia-smi`, was always real.

The laptop's own controller had the true value all along. Synapse's command table calls it *Get Thermal
Reading* (class `0x0D`, command `0x85`, 80-byte payload): `args[0]` is the sensor count, then one degrees-
Celsius byte per sensor, CPU first then GPU. Confirmed on this hardware on 2026-09-07 — the GPU byte
tracked `nvidia-smi` to within 1 °C across idle and load, and the CPU byte moved 89 → 98 °C under an
all-core load while the ACPI zone sat at 28. It also needs no administrator rights, so the old "CPU
temperature needs admin" caveat is gone from the UI.

That measurement carries a real finding: **this CPU idles at 89–91 °C and reaches 98 °C under load**, with
a Tj max of 100 °C. That is why the CPU limit is 97 °C and not 90 — a shared 90 °C limit against a truthful
sensor would trip permanently at idle. `nvidia-smi` failures now fall back to the controller's GPU byte, so
losing it no longer means an unwatched GPU.

---

## Build

Needs Windows 10/11 x64 and the .NET 8 SDK. The default build is framework-dependent (it needs the .NET 8 Desktop
Runtime to run); `-SelfContained` makes one that runs on a clean PC. `--install-autostart` registers the elevated
logon task, so run that once from an elevated prompt.

```
.\build-publish.ps1                         # framework-dependent build into publish-fd\
.\build-publish.ps1 -Out <fresh folder>     # publish while the live copy is running and locked
.\build-publish.ps1 -SelfContained          # self-contained build into publish\
```

To replace a running copy: `Stop-ScheduledTask BladeCtl`, swap the folder, `Start-ScheduledTask BladeCtl`. Use a fresh output folder each time; a folder whose exe was just deleted stays locked for a while.

```
BladeCtl/
  src/BladeCtl.Core/   RazerPacket, BladeDevice (raw HID feature reports), BladeController (Blade),
                       ChromaController (extended matrix accessories), RazerEnumerator, RazerCatalog,
                       RazerOutputDevice (output-report devices)
  src/BladeCtl.Core/Power/   Power card logic, no Win32: verdict (GPU Glance port), CPU row, supply, battery ETA,
                       learning, wording
  src/BladeCtl.Tray/   Program (CLI + WPF host), BladeCtlContext, MainWindow.xaml + MainViewModel + DeviceCardVm,
                       Theme.xaml, Gauge, DeviceMonitor, CommandRunner, AccessoryService, RazerGuard, Log, Settings
  src/BladeCtl.Tray/Power/   Power card sampler, battery IOCTL, PDH, powrprof, NVML, foreground CPU, card UI
  tests/BladeCtl.Power.Tests/   offline tests: `dotnet test tests\BladeCtl.Power.Tests` (fixtures in tests\fixtures)
  src/BladeProbe/      console tool: --enum, --explore (read-only register sweep), --hidsweep <pid> [--sweep],
                       --getsweep <pid> <tid> <class> <ds>, --rz <pid> <tid> <class> <cmd> <ds> [args] (one raw transaction),
                       --rgb-test, --leviathan2, --fan-test
  publish-fd/          the live build (+ tools\, README)
```

---

## Credits

Protocol knowledge comes from these projects. BladeCtl is a separate C# implementation and does not include their code:

- [OpenRazer](https://github.com/openrazer/openrazer): packet layout, CRC, Chroma effect and brightness commands, product ids
- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB): accessory transaction ids and the Leviathan V2 X output-report path
- [razer-laptop-control-no-dkms](https://github.com/Razer-Linux/razer-laptop-control-no-dkms) and [librazerblade](https://github.com/Meetem/librazerblade): fan, power mode, boost levels, logo and battery health commands
- [ViGEm](https://github.com/nefarius/ViGEmClient) (Nefarius.ViGEm.Client, MIT): the virtual controller in controller mode
- [HidSharp](http://www.zer7.com/software/hidsharp) (Apache-2.0): HID device access
- The Huntsman V2 Analog key-id table was read from Razer Synapse's local device data so the analog engine can talk to the keyboard without Synapse.

Razer, Blade, Chroma, Huntsman, Leviathan, BlackShark and Synapse are trademarks of Razer Inc.

## License

MIT. See [LICENSE](LICENSE).
