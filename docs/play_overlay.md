# Play overlay

Two panels drawn over the running game, from the same records as the website.
They live in the **FH Companion** app, on its *Rivals overlay* tab — one
program, one telemetry socket, one settings file.

| Panel | Answers | Trigger (default) |
| --- | --- | --- |
| **Right** — what to drive | The Event Sign Up screen is offering three routes in one class. Which cars, in what order, over exactly those routes? | `F8`, or the gamepad **View/Back** button |
| **Left** — your car | Which car am I in, and where does it place in every category and class it has laps in? | `F6`, or the gamepad **left-stick click** |

`F7` switches between the points order and the time-sum order, `F9` pins whichever
panel is up. Every key and pad button is set on the tab, and stored in
`config/overlay.json`.

```powershell
cd haptics\ForzaHaptics.Tester
dotnet build
.\bin\Debug\net9.0-windows10.0.19041.0\win-x64\"FH Companion.exe" --overlay
```

`--overlay` opens straight on the tab and starts watching; without it, press
**Start overlay**.

## Why it is in the haptics app and not beside it

Because it cannot be beside it. Forza sends its telemetry to exactly one endpoint,
and a UDP port takes exactly one listener — measured, not assumed: with the
haptics tool already bound to 5300 a second process fails with `WinError 10013`,
and the other way round the haptics tool fails with `10048`. Two programs meant
running one of them.

The Python overlay (`scripts/forza_overlay.py`) still exists and still works, and
it can relay the stream on (`telemetry_relay_port`) so both can run. It is the
reference implementation now, not the one to use while playing.

## Two requirements

**Borderless windowed.** An exclusive-fullscreen swap chain draws over every other
window, so no overlay of any kind can appear on top of it. This is not a limitation
of this tool.

**Data Out on**, for the left panel: Forza → Settings → HUD and Gameplay → Data Out
`On`, IP `127.0.0.1`, and the port the app's telemetry tab is set to (5300 by
default). The app starts that listener by itself, the haptics and the overlay read
the same packets from it, and starting the overlay starts it if it is stopped.

**There is one port and one place to set it** — the box on the telemetry tab. The
overlay has no port setting of its own on purpose: two settings for one socket can
disagree, and the one that loses is always the one just changed. The Rivals tab
shows the live state ("the app is listening on UDP 5300 — this panel reads that
same stream, there is no second port").

## How the panels behave

Nothing is on screen until a button asks for it, and it takes itself away after
`show_seconds` (30 by default). That holds when the read **failed** too: a press
that cannot find the routes puts up what it did read and what usually fixes it, and
that message expires on the same timer rather than sitting there.

The right panel also shows itself when a route offer is recognised on its own
(*Show the route panel by itself*), on the same timer. It never interrupts the left
panel, which was asked for by hand.

Only ever one ranking is listed. Points and the time sum answer different questions
— points reward beating the field on every route, the time sum rewards raw total
pace — and a glance mid-menu can only hold one order. The left panel follows the
same setting, so both sides of the screen answer the same question.

## How it reads the screen

Two masked regions, given as **fractions** of the frame so one setting fits 1080p,
1440p and 4K:

```
region_routes = [0.16, 0.165, 0.62, 0.62]   # the 01/02/03 list, green header included
region_class  = [0.76, 0.15,  0.90, 0.28]   # the lone badge in the card's corner
```

Everything else is never shown to the recogniser. That is not only speed: the
card's lower half prints the **featured car's** own class and PI (`C 484`), and on
a *Spec Racing* event that is the spec car, not the restriction. A whole-frame read
finds both, cannot tell them apart, and answers the wrong question with total
confidence. The test suite checks exactly that case — badge says `A`, card says
`C 484`, the answer must be `A`.

Inside the routes region, every line is matched against the dataset's closed
vocabulary of 23 route names, so no row geometry is assumed and a patch that moves
the cards costs nothing.

### The badge is not read by OCR alone

Windows' recogniser will not return a single character. Measured on this machine: a
lone `C` gives nothing, `CC` gives nothing, `CCC` reads; `S1S1S1` gives nothing
while `S1S1S1S1S1` reads — and the same five-fold `S1` rebuilt from the game's own
glyph still gives nothing, because its strokes are thinner than a rendered font's.

So the badge takes two runs at it. The glyph is cut out of its badge — as the
pixels that are none of the region's few flat background colours — and repeated
five times into a word the engine will read, with the repeat undone afterwards
(`SISISISISI` → `SI` → `S1`, letter-for-digit corrected). If that still comes back
empty, the glyph is matched by **shape** against the eight tokens a badge can
possibly be (`A B C D R X S1 S2`), which needs no engine and cannot return a class
that does not exist.

### The same button takes it away

F8 and F6 toggle: press one with its panel up and the panel goes. It is the button
already under the finger, and hunting for a second key with a panel over the screen
is worse than the problem.

For the right panel that also has to survive `auto_show`, which otherwise puts it
straight back on the next read a second later and makes the button look broken. So a
press-away is remembered against the SCREEN it dismissed: that screen stays quiet,
and a different one shows again on its own.

### Walking the field

A panel shows the rows its own height can hold -- 44 of a 474-car ranking on a 4K
screen, which reads as a ranking that ends at 44. The panel now takes the WHOLE list
and shows a window into it: **Page Down** and **Page Up** walk it while a panel is
up, the heading keeps its place, and the right of that heading says where the window
is (`45-88 / 474`). Pressing F8 or F6 opens a panel at the top again, and every press
restarts the thirty seconds -- someone paging down a list has not finished with it.

The keys are `hotkey_scroll_down_vk` / `hotkey_scroll_up_vk`; set either to 0 to give
the key back to the game. They act only while a panel is up.

The panel's title carries the race type beside the class -- `Class S1 - Cross-Country`
-- and it is worked out from the ROUTES, not read off the screen: a route name belongs
to exactly one category in the dataset, while the card's own word for it is artwork
over a photograph. Routes that disagree leave the race type off rather than guess it.
The same answer is passed into the ranking, so two categories sharing a route name
cannot mix.

### The badge sits on a photograph, not on a colour

The badge is found by finding its PLATE first: a small solid square of one strongly
coloured paint, filling its own bounding box, roughly square, with something written
on it. The glyph is then whatever is not that paint, inside the square.

It used to work the other way round -- take the few flat background colours the
region is made of and call everything else the glyph. That holds on a menu and fails
completely on the Event Sign Up card, because the card is a PHOTOGRAPH: a crowd,
green banners, yellow barriers, no flats anywhere. The mask lit up across the whole
picture, the box came out larger than a glyph can be, and the step returned nothing.
Measured in play on 2026-09-09: all three routes read, `class=- []` -- not a bad
guess, no source at all. The flats method is kept as the fallback for the menus it
was measured on.

`data/screen_fixtures/event_signup_b.png` is that exact frame, and
`scripts/test_screen_reader.py` now reads it. Every synthetic check in that suite
draws the badge on flat colour, which is why they all stayed green while the app
failed in the player's hands.

### The panel must not photograph itself

The right panel covers the badge's corner: `region_class` starts at 0.76 of the
screen width and the panel takes the rightmost 0.26. The panel is shown BEFORE the
read is triggered, so without help every read photographs the panel instead of the
game and the class comes back `not found` — which is exactly what a press looked
like before this was fixed.

Each panel therefore asks Windows to keep it out of every capture
(`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`) as its handle is created: it
stays on screen for the player and disappears from the screenshot. Measured on this
machine: a magenta test window contributes 126 sampled pixels to a `CopyFromScreen`
without it and 0 with it. Where Windows refuses the request, the controller falls
back to hiding the panel around the grab — correct, but with a visible blink, which
is why it is the fallback and not the rule.

### When a screen is missed

The tab has **Save mask preview**: it writes a capture with both regions drawn on
it, which is how a mask that has drifted is spotted. **Read the screen now** prints
what the last read found. Every read AND every button press the overlay sees also
appends a line to
`%TEMP%orza-overlay
eads.log` — timestamp, whether it counted as an offer, the
class with the source that decided it, and the routes. That log is the only way to
check a press after the fact: the panels are held out of screenshots, so a capture
cannot show what the panel said. From the Python side:

```powershell
python scripts/screen_reader.py --shot -v            # the live screen
python scripts/screen_reader.py --boxes out.png      # draw the regions on a capture
python scripts/screen_reader.py --image frame.png    # a saved capture
```

## Which car am I in

Telemetry gives a car **ordinal**. The dataset knows cars by the leaderboard's own
`car_id`. There is good reason to think those are the same number — the codename
catalogue is "a contiguous array indexed by carId", and an index into the game's car
array is what an ordinal is — but it is **not proved**: on the 46 ids where the
codename catalogue and the lap-time-joined names both exist, they disagree 40 times,
so one of those two mappings is wrong and it is not yet known which.

So the left panel uses the direct match but **labels it** ("Name assumed from the
ordinal — not confirmed") and always prints the ordinal beside it. If the name is
not the car under you, that is the answer to the open question. A pair learned from
the screen counts as evidence instead, and is remembered in
`config/fh6_car_ordinals.json`.

## Only over a running game

A hotkey or pad button does nothing while Forza is not running, and a panel that is
up when the game goes away is taken down -- including a pinned one, which would
otherwise sit over the desktop, always on top, with no visible way to close it. The
panels read the game's screen and answer about the race being offered, so outside the
game they have nothing to say.

The tab's status line says `idle - waiting for forzahorizon6.exe` while it waits.
`overlay_require_forza: false` in `config/overlay.json` turns the guard off, for
checking panel layout without starting the game.

## Where the numbers come from

The scoring exists three times: the page's JavaScript (the source of truth), the
Python advisor, and the C# advisor the overlay uses. Three implementations of a
changing rule drift silently, so both ports are pinned to the page:

```powershell
python scripts/test_rivals_advisor.py   # page JS == python == C#, 7 scenarios
python scripts/test_screen_reader.py    # both readers, on the same synthetic screens
```

`test_rivals_advisor.py` runs the **shipped page's own script block** over the same
payload (`scripts/dump_site_class_table.js`) and compares every car's points, time
sum, presence, thin count and full ordering including ties. The C# side answers
through `--dump-class-table`; the C# reader answers through `--read-image`. Run both
after any change to `build_analytics_site.py`.

## What it cannot tell you yet

The dataset holds **Road Racing only**. Street Racing, Touge and the rest have no
boards scanned, so the left panel lists them as "not scanned yet" rather than
implying the car has no standing there.

Board depth caps what a place means: most boards are read to roughly the first
20,000 entries. A low place usually means *few surviving laps*, not a slow car — the
footnote on the panel says so, for the same reason the website does.
