# FH Companion -- the Windows app

The app itself (`ForzaHaptics.Tester`) and the two probes it grew out of: mapping
Forza Horizon 6 UDP telemetry to controller haptics.

## First hardware probe

The probe uses the official SDL 3 gamepad API. Put the x64 `SDL3.dll` from SDL
3.4.10 or newer in:

`ForzaHaptics.Probe/native/SDL3.dll`

List controllers without sending haptics:

```powershell
dotnet run --project .\ForzaHaptics.Probe -- --list
```

Run a short, low-intensity opt-in test:

```powershell
dotnet run --project .\ForzaHaptics.Probe -- --rumble 0 0.20 0.20 300
```

Arguments are controller index, low-frequency intensity, high-frequency
intensity, and duration in milliseconds.

## Telemetry probe

In FH6, set Data Out to `On`, IP address to `127.0.0.1`, port to `5300`, and
packet format to either `Sled` or `Dash`. Then run:

```powershell
dotnet run --project .\ForzaTelemetry.Probe -- 5300 30
```

The probe reads Forza's four normalized combined-slip values. For each side of
the car, the current prototype defines grip as:

`1 - clamp(max(abs(front slip), abs(rear slip)), 0, 1)`

This deliberately keeps the telemetry-to-grip stage separate from the
grip-to-haptics curve that the editor will control.

## Handing it to someone else

```powershell
python scripts/build_haptics_package.py
```

Builds `dist/fh-companion/` plus a zip beside it: the .NET runtime, SDL3.dll,
the settings, and the current lap records all inside one folder, so the recipient
installs nothing and needs no network to get numbers. The packager then starts the
binary it just built -- from another directory, with no server -- and refuses to zip
unless the app itself reports the shipped dataset, scores a class table out of it, and
opens its window. See [docs/haptics_package.md](../docs/haptics_package.md).

## Integrated application

Build and launch the main Windows application:

```powershell
dotnet run --project .\ForzaHaptics.Tester
```

The feature list of the app as it is today is in the repository
[README](../README.md) and in [docs/app_guide.md](../docs/app_guide.md). What
follows is the history of the haptics part, which the app grew out of.

The haptics part contains:

- A permanent vibration-test tab with `0%` through `100%`, both/left/right
  output, a ten-second test, and an immediate Stop button.
- A live telemetry tab that listens for FH6 UDP Data Out and displays the four
  combined-slip values plus calculated left/right grip.
- An `All telemetry + outputs` inspector with all 88 fields from the official
  FH6 324-byte packet, derived wheel groups, filtering, and the current native
  controller output state.
- A visual `Blueprint editor` that replaces the previous fixed mappings.
  Telemetry source nodes, wheel presets, custom group nodes, any number of
  multi-point Bézier nodes, and controller output nodes can be wired together.
- A constant test-source node that can drive an output at an exact percentage
  without Forza running, for diagnosing individual controller actuators.
- A responsive blueprint workspace with resizable/collapsible palette and
  properties panels, focus-canvas mode, zoom controls, fit-to-nodes, scrolling,
  and an expandable virtual canvas.
- Graph output nodes select left/right grip or trackpad, rumble or beeping,
  continuous or pulsed operation, input mixing, strength/frequency modulation,
  carrier-frequency boundaries, strength boundaries, pulse rate, and duty
  cycle.
- Output nodes can target the Steam Controller's four native actuators or SDL
  gamepads such as Xbox, PlayStation, and 8BitDo controllers. Standard
  gamepads expose low/high body motors; compatible Xbox controllers may also
  expose left/right trigger rumble.
- DualSense and DualSense Edge controllers also have a native HID target
  available over USB or Bluetooth. Its output nodes expose the low/high body
  haptics plus independent L2, R2, or combined adaptive triggers.
- Adaptive-trigger modes include resistance, rising tension/slope, a
  weapon-style resistance wall and release click, trigger vibration with
  mapped frequency, and an explicitly marked experimental bow-snap effect.
  Start/end travel, secondary resistance, snap force, strength, and vibration
  frequency can all be driven from graph values.
- The header contains one active-controller selector. Vibration tests, newly
  created output nodes, graph routing, and the 50 Hz rumble firewall all follow
  that selection. Other connected controllers are left untouched.
- Generic `Frequency mix` output maps the requested frequency range into an
  equal-power crossfade between the standard low- and high-frequency motors.
- Graph profiles can be saved and loaded as `.fhgraph.json`.
- While connected, the application reasserts all four native output channels
  at 50 Hz. Channels not owned by the graph are explicitly forced to zero so
  game/Steam rumble is overwritten by the telemetry-driven output. Standard
  gamepads (XInput) and the DualSense get a report only when the values change,
  plus one per second: over Bluetooth those reports share airtime with the
  controller's input. With Xbox Remote Play (controller on this PC) they are
  reasserted at 50 Hz again, because the Xbox app forwards the console's rumble
  to the same controller; input goes through the stream there anyway.
- `Always on Top` pins the window above the game and other applications.

Closing the window immediately sends a zero-force command and stops the UDP
listener.

The controller does not expose a Windows audio playback device. Effects such as
the Wilhelm-scream demonstration are synthesized by driving its four haptic
actuators at changing frequencies and gains. This application therefore maps
grip to a second audible haptic curve rather than sending normal PCM audio to a
speaker.

The application uses the controller's native four-channel HID output reports:

- channel 0: right grip rumble
- channel 1: left grip rumble
- channel 2: left trackpad haptic
- channel 3: right trackpad haptic

## Only while the game is running

Two parts of this app reach past their own window: the 50 Hz firewall, which
overwrites whatever the selected controller is doing, and the overlay, which puts
panels on top of everything. Both are what you want during a race and neither is
what you want otherwise -- a controller still buzzing in another game, or a panel
left hanging over the desktop, is the app misbehaving.

So both ask `GameWatch` first (the process, polled every two seconds), and the
haptics additionally require telemetry to be arriving. That second condition catches
the more damaging case: with Forza running but Data Out off, the graph has no input,
so the firewall would assert zeros at 50 Hz and silence the game's *own* rumble.
It also hands the controller back in menus and on a pause, where the game stops
sending.

On the way into idle the outputs are zeroed **once** and then left alone --
re-zeroing every tick would be the very overwriting this avoids. The graph keeps
being evaluated, so the editor and the telemetry inspector stay live; only the
output stops. The header line names the reason, because otherwise a deliberate
silence and a broken controller look the same:

```
Haptic/trigger firewall active            working
idle - waiting for forzahorizon6.exe      the game is not running
idle - no telemetry on UDP 5300           Data Out is off
```

The vibration test and the tone test are never gated: someone is sitting there and
just pressed a button. To switch the guards off entirely -- driving an actuator from
a constant node with the game closed -- set `haptics_require_forza`,
`haptics_require_telemetry` or `overlay_require_forza` to `false` in
`config/overlay.json`.

## Blueprint signal flow

The graph evaluates scalar values normalized to `0..1`:

- Telemetry nodes select any official or derived FH6 field and define raw
  minimum/maximum normalization, absolute value, and inversion.
- Group nodes accept any number of connections and combine them using maximum,
  minimum, average, or clamped sum.
- Constant nodes emit an exact `0..1` test value even when no telemetry is
  arriving.
- Curve nodes contain a reusable multi-segment cubic Bézier curve.
- Output nodes turn the result into rumble or a pulsed beep on one controller
  actuator.

Connections are made by dragging from the blue port on a node's right side to
the yellow port on another node's left side.

An output node can accept several connections. Its `Multiple input
connections` setting chooses Maximum, Minimum, Average, or clamped Sum. If
several separate output nodes target the same physical actuator, the strongest
live output wins for that update.

`Continuous tone / vibration` keeps the actuator energized continuously; its
carrier frequency controls the audible pitch. `Pulsed beep` gates that carrier
on and off, so its pulse-rate setting intentionally produces a Morse-like
rhythm. An output can map its input to strength only, frequency only, or both,
using independent values at 0% and 100% input.

On standard Xbox-style rumble APIs, Windows does not provide an exclusive
rumble-owner switch. The application therefore uses the same authoritative
overwrite strategy as the native Steam Controller path, refreshing desired
motor states and zeroing unused motors every 20 ms. Kernel-level isolation
would require hiding the physical controller and forwarding all of its inputs
through a virtual-controller driver.

The native DualSense path writes Sony's enhanced 48-byte USB or 78-byte
Bluetooth output reports directly. Bluetooth reports include the required
CRC32. While the DualSense is selected, the 50 Hz firewall owns both body
haptics and L2/R2 effects; unused triggers receive the official off/reset
effect.

For a direct hardware test, add a constant source and a controller output,
choose the actuator/frequency/maximum strength, connect the two nodes, and then
enable `Graph output enabled`. Disable it or deactivate the constant node to
stop the test.

**Out of the box** (since 2026-09-26) the app drives a built-in graph, and
`Graph output enabled` starts on and remembers its state (`haptics_graph_enabled`
in `config/overlay.json`):

- grip loss on the left wheels -> channel 1 (Steam: left grip), right wheels ->
  channel 0 (right grip), 20 Hz rumble. The curve stays quiet up to about 60 %
  grip loss and rises steeply towards the limit.
- any wheel locking under braking -> channel 2 (Steam: left pad; DualSense and
  Xbox-style pads: both motors), a 500 Hz buzz pulsed 30 times a second.
- channels 0-2 exist on every controller type, and choosing a controller keeps
  a node's channel as long as that controller has it.
- no fresh telemetry means silence: all telemetry nodes read 0, and the default
  graph maps 0 to nothing.

A graph you load or save yourself replaces the built-in one and is loaded again
at the next start.

The built-in wheel presets include left/right/front/rear grip and wheel-lock
groups. Custom groups can combine arbitrary signals such as surface rumble,
suspension, brake input, acceleration, or individual tires.
