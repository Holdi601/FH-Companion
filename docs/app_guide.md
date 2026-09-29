# FH Companion, tab by tab

What each tab of the app is for and what it shows. The internals are in the
linked notes.

## Before the first start

Step by step with pictures: [Setting up on this PC](setup_pc.md) ·
[Setting up on an Xbox or a second PC](setup_xbox.md).

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

## Start with Forza

The button **Start with Forza** at the top right of the window puts the app in
your Windows startup folder. From sign-in it waits invisibly in the notification
area. When Forza starts, the app opens minimized, and when the game ends it goes
back to waiting. Closing the window keeps it waiting; right-click the icon and
choose **Quit** to end it. Click the button again to switch this off.

## Rivals overlay

Starts and stops the overlay over the game, and holds what the overlay needs:

- **Start overlay / Stop overlay.** The app remembers whether it was running and
  starts it again next time.
- **Your gamertag** *(optional)*. It marks your own laps and is the name your
  submitted laps appear under. Without one they are still submitted and appear
  under a temporary player name. A gamertag entered later replaces that name on
  all your laps, including the earlier ones. Emptying the field keeps the last
  name.
- **Submit my laps when they beat the leaderboard.** On by default. Only a lap
  that is faster than the best leaderboard time of the same car, route and class
  is sent. See [lap_submissions.md](lap_submissions.md).
- **Waiting laps.** A lap that cannot be sent right away waits on your disk, one
  per car, route and class, and only the fastest is kept. That covers
  submission being off, offline mode, or no answer from the server.
  It goes out once all of that is fine again, even weeks later, after one more
  check against that day's leaderboard. The line under the gamertag counts them,
  and **Discard waiting laps** deletes them.
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

  The label under the delta always starts with the selected reference, so a
  wrong selection is visible at once. In the PI-class and any-lap modes it also
  names the reference car, with its class in brackets when that is not yours,
  for example "personal best, any car · Nissan Skyline (R)". The automatic
  source takes your own laps first and the Rivals times otherwise.
- **Show the time to beat for the website's leaderboard.** On by default. A
  gold line under the delta, for example "to beat: 1:24.012 -- website best,
  this car". It is your car's best valid time on the website for this route
  and class, or your own submitted time if that is faster. Beat it and the lap
  is submitted and celebrated. If the car is not on that board yet, any lap
  counts. The line appears once the route is known: from the lap you just
  finished in this race, from the sign-up screen (the next route of a series,
  or the only route offered), or from your own laps on the same start line.
- **Input strip.** Throttle, brake, clutch, steering and gear.
- **Live map.** The lap drawn as you drive, with line thickness and smoothing.
  Jumps appear as gaps.
- **Tyre overview.** Off until you switch it on under Elements. It shows while
  you drive, in races and in free roam, and hides in menus and pauses.
  - **Colour:** tyre temperature, from blue (cold) through green to red (hot).
    The number is in °C, or in °F if you choose so.
  - **Percentage in the tyre:** grip left before it slides (0% = sliding).
  - **Outline:** thin while the tyre holds, yellow at the limit, red and
    glowing when it slides. The tyre also tilts with its slip angle.
  - **Tag above the tyre:** LOCK (locked under braking), SPIN (wheelspin),
    SLIDE (sliding sideways).
  - **Bar below:** slip ratio. Red to the left means braking, orange to the
    right means wheelspin. The marks at the sides are the grip limit.
  - **Bar beside:** suspension travel with percent and centimetres; it turns
    red when the suspension bottoms out.
  - **Icons:** a water drop in a puddle, red-and-white stripes on a kerb, waves
    on bumpy ground.
- **Course maps on the Event Sign Up screen.**
  - **Source:** your own laps or the Rivals maps. The Rivals maps appear as the
    game's picture or as a smooth traced line.
  - **Layout:** side by side or stacked, with their own thickness and smoothing.
  - **Display time:** how long they stay up (0 = until the race starts).
  - **Championships:** the routes are coloured done, now and next. When you
    join a Horizon Play series midway, the sign-up screen marks the route that
    is already running ("In Progress") and the one you join ("Up Next"); the
    app counts the running one as done, recommends cars only for the races you
    still drive, and names your laps after the route that is up next.
- **Car note.** Whether the note in the car menu also shows the applied tune's
  name and description.
- **Celebrate when a lap beats the website's time.** On by default. A card with
  your time, the gap to the website's best, route, car and class appears near
  the top of the screen, with confetti and a short sound, for about five
  seconds. It never takes the focus and cannot be clicked. **Play a sound with
  it** turns the sound off on its own, and **Try it** shows the celebration
  once. A lap no faster than one you already sent or that is already waiting
  is not celebrated again.
- **Say thanks when your lap adds a new car to the leaderboard.** On by
  default. When the server accepts a lap of a car that was not on that route
  and class board, a calmer notice in teal appears: "New car on the
  leaderboard!" with your time, a "NEW" chip, the route, car and class, a
  little confetti and a soft chime. It appears only after the server accepted
  the lap, and only for the first lap of that car there. A lap sent later from
  the waiting queue shows it only while Forza runs. **Try it** shows it once.
- **Personal records.** Your own laps, not the website, decide. Each lap is
  compared with your laps on the same course, in the same class and with the
  same start (standing or flying). A lap shows at most one card, the strongest:
  - **Personal best in a class on a course** (green, on by default): faster
    than any of your laps there in that class.
  - **A car's first time on your list** (blue, on by default): the first lap of
    that car on this course in this class, with its place on your list.
  - **Personal best with a car** (green, on by default): faster than your
    earlier laps with that car there, for every car, class and course.
  - **Your first lap in a class on a course** (off by default).
  - **Separate records per mode** (on): Rivals, Horizon Play, races and free
    roam keep their own records. Laps from before the mode was recorded count
    for every mode.
  - The card is a fifth smaller than the website celebration and stays for
    under four seconds.
  - **No celebration while you drive.** Every card waits until the car stands
    still, the race is over or a menu shows; the most important one shows first
    (website record, then new car, then personal). If the same lap also beats the website, only the website
    celebration shows. The short sound and **Try it** are next to the switches.
- **Archive every lap for heatmaps.**
- **Layout editor.** Drag every block where you want it, resize with the mouse
  wheel, and optionally show it on the real screen while you set it up.
  - Each block has its own section on the right: its switch, anchor, size,
    settings and colours. Clicking a block in the picture jumps to its section.
  - Drag the divider to widen the right side. With room, the sections sit in
    several columns, so nothing needs scrolling. The width is remembered.
- **Recording and streaming.** The overlays are kept out of screen recordings,
  because the app reads the screen itself and must not read its own panels.
  - **Open the recording window:** a normal window that draws the same overlays
    on a key colour (green, magenta or black). In OBS, add a Window Capture of
    it above the game capture and a Color Key filter in the same colour. The
    window may sit on another screen or behind the game, but not minimised.
  - **Show overlays over the game:** switch it off and nothing at all is drawn
    over Forza, so the overlays cannot affect its frames. The recording window,
    for example on a second screen, still shows them.

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

## Car collection

Which cars you are still missing, and how to get each one. The list holds every
car on the official FH6 car list (forza.net/fh6cars), and the ways to get them
come from the Forza Wiki:

- Autoshow price and Wheelspin;
- Festival Playlist rewards, with the season they last came in;
- Aftermarket dealers, with the wiki's map code and price;
- Barn Finds and treasure cars, with the region;
- Car Mastery, the Collection Journal, campaign wristbands, gifts and loyalty rewards;
- DLC packs such as the Car Pass;
- the Auction House.

The download server rebuilds the list once a day. The tab fetches it from there at
most every twelve hours, and the package brings a copy for offline use. The app
itself never contacts forza.net or the wiki. Double-click a car, or use **Open on
the wiki**, to read its wiki page in your browser.

**Which cars you own:**

- **On this PC:** press **Read my garage** while the game runs. The app reads the
  garage from the game's memory, as the tuning inspector does, and from then on the
  garage decides. The **Car notes** garage button does the same.
- **My Cars in the game:** every car highlighted in **My Cars** counts as yours. The
  app reads the name on the tile and finds the car on the official list by name, so
  this also works for cars whose id the app does not know yet. Scroll through My
  Cars once with the game in front, or on the Xbox with the picture source on. Only
  the screen titled "My Cars" counts; the Autoshow looks the same but proves nothing.
- **Not sure:** some cars have no known id yet, because no scanned leaderboard has
  named them. If your garage holds cars the app cannot place, those cars show as
  **not sure** (amber) instead of missing. Highlight them in My Cars or tick them.
- **Xbox / 2nd PC:** there is no memory to read. Cars you drive with the app and
  cars highlighted in My Cars count by themselves; tick the others you own.
- **A tick always wins.** Untick a car the garage or a drive marked as yours, for
  example a loaner from an event, and it counts as missing.

Filter by name or type, by the way to get a car, or by class, and choose missing,
owned or all cars. Click a column header to sort, for example by Autoshow price.

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
PlayStation and 8BitDo pads. The controller is only driven while Forza runs and
sends telemetry.

It works without any setup: a built-in graph turns tyre grip loss into rumble on
the side that slides and a locking wheel into a short buzz. Untick
`Graph output enabled` in the Blueprint editor to switch it off; the app
remembers the choice.

**Battery:** vibration costs power. A wireless controller will likely run out of
battery noticeably faster while the haptics are on. The stronger and faster
(higher frequency) you set the vibration in the Blueprint editor, the more power
it draws.

## Console or second PC

For playing on an Xbox, or on another PC, with FH Companion on this computer.
Step by step with pictures: [Setting up on an Xbox or a second PC](setup_xbox.md).

1. At the top of the window, next to **The game runs on:**, click **Xbox / 2nd
   PC**. The app asks once and restarts in that mode. **This PC** switches back.
2. On the Xbox or the gaming PC, set Forza's **Data Out** to the address shown in
   the **Live grip telemetry** tab, with the port from the same tab. Only real
   network cards are listed; virtual ones (Hyper-V, WSL, VPN) are left out.
3. The **dashboard** window opens by itself. Double-click it or press F11 for
   full screen, and Esc to leave full screen.

What works and what does not:

- **Works from telemetry alone:** delta and ghost countdown, input traces, live
  map, tyre overview, lap recording, My times, personal records and the
  celebrations.
- **Off:** the tuning inspector and tunes (they read the game's memory and
  save), the vibration test, the Blueprint editor and all controller haptics.
  The controller is on the other device.
- **Needs the game's picture:** the Event Sign Up maps, the car recommendations,
  the car note from My Cars, and detecting Rivals and Horizon Play. Choose one of
  six ways under **Game picture (optional)**; a preview underneath shows what
  the app sees, and a change applies at once:
  - **Capture card:** the Xbox's HDMI goes through the card (Elgato and the like)
    to the TV. The app lists the video devices of this PC.
  - **OBS:** in OBS, right-click the preview and choose **Windowed Projector
    (Program)**. The app finds that window by itself. (OBS's Virtual Camera is a
    DirectShow device, which Windows' own video API does not list; if a future
    OBS registers it properly, the app takes it instead.)
  - **Xbox Remote Play:** start Remote Play in the Xbox app or at xbox.com/play;
    the app suggests the Xbox window. It is captured through Windows Graphics
    Capture, so it may sit behind other windows, but it must be on a screen and
    not minimized. Windows draws a yellow frame around a window while an app
    captures it. The list offers every other window on this PC too: any window
    that shows the game works.
  - **Discord:** the Xbox's stream in a Discord voice channel, watched on this
    PC (popped out or full screen). The app finds the Discord window by itself.
    Your own stream needs a second Discord account on the PC, because your
    account's call moves to the Xbox.
  - **Stream in the browser:** the stream on Twitch, YouTube, Kick, Trovo,
    Facebook, TikTok or Rumble, open in a browser on this PC (full screen or
    theater mode). The app finds the browser window whose active tab names one
    of them; the topmost wins.
  - **Stream address** (SRT, RTMP, RTSP, HLS): read through ffmpeg. The app finds
    ffmpeg on the PATH or from winget; without it, run `winget install
    Gyan.FFmpeg` once. A stream is a few seconds late, which is fine for menus.

  In a window, the game's 16:9 picture is cut out of any black or plain bars
  around it, but only when what remains is 16:9 — a flat sky is not a bar.

  With Remote Play or the OBS projector you play looking at a window on this
  screen, so **Where the HUD shows** offers **Over the game window, like playing
  on the PC** besides the dashboard. The HUD then sits over that window's 16:9
  picture, only while the window is in front, and is placed in the Lap delta HUD
  tab as on the PC. The screen reading still captures the window itself, which
  never contains the overlays. A stream in Discord, in the browser or from an
  address runs behind the game, so there the HUD stays in the dashboard: it
  follows the live telemetry and would run ahead of the picture.
- **Without a game picture,** set **Which mode you are playing** so your laps
  carry the right mode. Only Rivals and Horizon Play laps count on the website.

## Updating

The app asks on start whether a newer version exists, and the update button does
it at once. It verifies the publisher's signature, replaces its own files and
restarts. Your settings folder is kept.
