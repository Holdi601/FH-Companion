# FH Companion, tab by tab

What each tab of the app is for and what it shows. The internals are in the
linked notes.

## Before the first start

In Forza Horizon 6, under **Settings › HUD and Gameplay**, set:

| Setting | Value |
|---|---|
| Data Out | On |
| Data Out IP | 127.0.0.1 |
| Data Out Port | 5300 |
| Data Out Format | Dash |

Without this the game sends no telemetry, and every number stays at 0.

On the first start the app explains what it does on your PC and what leaves it,
and asks you to confirm. The link "What this program does" at the top of the
window shows that text again.

## Rivals overlay

Starts and stops the overlay over the game, and holds what the overlay needs:

- **Start overlay / Stop overlay.** The app remembers whether it was running and
  starts it again next time.
- **Your gamertag.** It is used to mark your own laps and to submit laps.
- **Submit my laps when they beat the leaderboard.** On by default. Only a lap
  that is faster than the best leaderboard time of the same car, route and class
  is sent. See [lap_submissions.md](lap_submissions.md).
- **Car ratings and updates.** Whether the ratings are current, and a button
  that checks for a newer version of the app.

The two panels of the overlay:

| Panel | Shows | Toggle |
|---|---|---|
| What to drive | The best cars over the three routes offered on the Event Sign Up screen, in that class | `F8` or the gamepad's View button |
| Your car | Where the car you are in places in every category and class | `F6` or a left-stick click |

Details: [play_overlay.md](play_overlay.md).

## Lap delta HUD

Everything drawn over the game while you drive, and where it sits:

- **Delta reference.** The lap you are compared against:
  - the same car with the same tune;
  - the same car;
  - the same PI class;
  - the same car and class;
  - any lap.

  In the PI-class and any-lap modes the delta names the reference car. The
  automatic source takes your own laps first and the Rivals times otherwise.
- **Input strip.** Throttle, brake, clutch, steering and gear.
- **Live map.** The lap drawn as you drive, with line thickness and smoothing.
  Jumps appear as gaps.
- **Course maps on the Event Sign Up screen.**
  - **Source:** your own laps or the Rivals maps. The Rivals maps appear as the
    game's picture or as a smooth traced line.
  - **Layout:** side by side or stacked, with their own thickness and smoothing.
  - **Display time:** how long they stay up (0 = until the race starts).
  - **Championships:** the routes are coloured done, now and next.
- **Car note.** Whether the note in the car menu also shows the applied tune's
  name and description.
- **Archive every lap for heatmaps.**
- **Layout editor.** Drag every block where you want it, resize with the mouse
  wheel, and optionally show it on the real screen while you set it up.

## Tuning inspector

The tune on the car you are driving: every fitted part with its name, step and
price, every slider position, and where the tune came from. It is read from the
garage database the game keeps in memory, read-only, and only when you press the
button. Details: [tuning_inspector.md](tuning_inspector.md).

## Tunes

The tunes you have downloaded, read from the game's save folder (read-only):

- **Count against the limit.** How many you store, measured against the limit you
  set (default 1000). The overlay warns when you get close.
- **Check which tunes are on a car.** Reads the garage once from memory, so the
  app knows which tunes are in use.
- **Views:**
  - every tune;
  - the cars with the most unused tunes;
  - the tuners behind your best laps;
  - the tuners whose tunes you keep most often;
  - the deletion plan.
- **Time frame.** Limits every view to tunes saved between two dates.
- **Deletion plan.** The unused tunes in the time frame, grouped by car, with
  their tuners summed up.
- **Deleting in the game** *(in progress, not in the released download yet)*. The
  app works through the plan in the game's own menus.
  - A test run goes through everything without deleting anything.
  - A tune that is on a car is never deleted.
  - Alt+Tab or the Pause key stops it.

## Car notes

Your own notes per car or per model. The note appears in the overlay when the
car is highlighted in the game's My Cars menu. The tab lists your cars and can
filter them, and "In the game now" jumps to the highlighted car. It can also
import your garage from the running game.

## My times

Your recorded laps: the best per car and course, and standings by points and by
total time. Laps are stored on your disk in `%LOCALAPPDATA%\FHCompanion\laps`.

## Free roam

Forza runs no clock outside a race; the app does. Every route you have driven
already has a start and finish line, and `F10` sets one of your own. Details:
[free_roam_time_attack.md](free_roam_time_attack.md).

## Controller haptics

- **Vibration test.** Try the exact force on each actuator.
- **Live grip telemetry.** Tyre slip per corner and wheel lock under braking.
- **All telemetry + outputs.** Every value the game sends, and what the haptics
  make of it.
- **Blueprint editor.** A node graph from telemetry to actuators, with Bézier
  curves for each mapping.

It supports the Steam Controller, DualSense (including adaptive triggers), Xbox,
PlayStation and 8BitDo pads. The controller is only driven while Forza runs.

## Updating

The app asks on start whether a newer version exists, and the update button does
it at once. It verifies the publisher's signature, replaces its own files and
restarts. Your settings folder is kept.
