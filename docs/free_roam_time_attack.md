# Time attack in the free world

Forza does not run a clock outside a race. This app does.

Drive over one of your start lines in free roam and the delta strip starts
working exactly as it does in a Rivals event — your time against your own best on
that road, measured at the place you are standing, not after the same number of
seconds. Cross the same line again and the lap is stored.

| | |
| --- | --- |
| **Where it lives** | *Delta strip* tab → **Time attack in the free world** |
| **Set a line of your own** | `F10` while driving |
| **Lines you already have** | every course you have ever recorded a lap on |
| **Stored at** | `%LOCALAPPDATA%\FHCompanion\freeroam_lines.json` (yours) and the `course.json` of each course folder (the rest) |

## Why it needed building at all

In Horizon `IsRaceOn` stays at **1** in free roam — it is not a race flag, it is a
"telemetry is live" flag. What does stand still is `CurrentLap`: measured across
18,676 packets, it never moves outside an event.

Everything downstream hung on that field. The lap recorder waits for the lap
number to change and it never does; the delta divides by a clock that reads zero.
Before 2026-09-14 the strip said `free roam -- no timed run`, which was true and
was also the end of the conversation.

So the app keeps its own clock, and takes the start line from something it already
had.

## The three decisions that carry it

**The clock comes from the game, not from us.** Every packet carries
`TimestampMS`. Using our own wall clock instead would also measure the network
buffer, the display and the garbage collector — milliseconds per packet, and over
a lap easily a tenth of a second. That is precisely the size of the thing being
compared.

**The start lines were already there.** Every course you have driven has one: it
is the `startX/startZ` in its `course.json`, written the first time a lap was
recorded there. Drive a Rivals route once and you can practise it in free roam
forever after, with no setup. This includes **EventLab and other custom routes** —
a course is identified by *where its line is*, never by a name, so a route the
game will not name still gets its own folder, its own best time, and a time per
car and per PI class like any other.

**A crossing is the point of closest approach.** The obvious rule — "distance
under X metres, so that was a crossing" — is wrong in a way that matters: the edge
of a circle is reached earlier or later depending on how far to one side you pass,
so the time would depend on your line rather than on your driving. The closest
approach is the foot of the perpendicular through the anchor, which is what a
start/finish line *is* geometrically, and it does not care how wide you go.

Two radii, not one: **120 m** is where an approach is watched (the same threshold
the delta and the lap archive use for "same course"), **40 m** is where it counts
as a crossing. With a single radius you would have to choose between a coarse
archive and a parallel road starting your clock.

Resolution is one packet, about 17 ms at 60 Hz. No interpolation: the closest
approach sits at the bottom of a parabola, so the error there is second order —
at 250 km/h and 1.2 m between packets, milliseconds. Interpolating would be
precision the source does not have.

## The rules, in one paragraph

The clock starts when you cross a start line and stops when you cross **the same
one** again, going the same way. Crossing a different line starts a new run there.
Under 400 m is a turn in front of the line, not a lap. Over 60 km the run is
abandoned — that is a drive through the landscape, and the recording would
otherwise collect a sample every 5 m for an hour.

## Free-roam times are kept apart from race times

Deliberately, in the archive and in what the strip compares against. Out here
there are no track limits, no penalty for cutting, no reset. A time set across a
field would be a fine free-roam lap and a false record on a Rivals route — and it
would quietly devalue every delta on that road afterwards. They are still
comparable to each other: same line, same clock, same rules.

The flag is `RecordedLap.FreeRoam`; archived laps carry `_freeroam` in the
filename.

## Two things the tests caught

Both were found by the app's own `--self-test`, not in the game.

**One drive-by reported two crossings.** An approach counts as finished once you
are 25 m further away — but at that moment you are still well inside the 120 m
watch zone, so the watch started again immediately, and since the distance kept
growing that second "approach" also finished, with a minimum of 37 m: just inside
the 40 m acceptance. For the clock this is fatal, because the second report
arrives a fraction of a second after the first and resets the run that just
started. A line is now locked until the zone has been left once.

**Trimming the tail made a 2,000 m circle 1,970 m long.** A crossing is only
certain once you are moving away again, so the first version cut the samples
recorded after that point. The mistake: the recording *begins* late by the same
amount, for the same reason. The odometer already runs from line to line.
Trimming removed the end and left the missing beginning in place. The *time* was
never affected — it comes from the two closest-approach instants, i.e. from the
line itself at both ends.

## Measured on 140 real laps

The synthetic test in `--self-test` drives a perfect circle: even speed, even
steps, the line exactly on the racing line. It proves the arithmetic and nothing
about real telemetry.

So the app can replay what it already recorded:

```powershell
"FH Companion.exe" --free-roam-replay "%LOCALAPPDATA%/FHCompanion/laps"
```

Every archived lap carries its samples — a point every 5 m, with world coordinates
and times. The command turns them back into telemetry with the game clock held at
zero, puts a start line at the lap's own beginning, and measures it again.

**139 of 140 laps came back within 0.1% on length and 0.2% on time.** Real corners,
real speed changes, a racing line that moves a few metres every lap.

### The one that did not, and why it is not a bug

A 6,818 m sprint came back as 3,211 m. The road passes its own start line twice on
the way: at 3,190 m it comes within **26.0 m**, at 3,999 m within **20.3 m**. Those
are measured minima, not near-misses — the car really does drive over the line.

No tolerance fixes this. Tightening the acceptance radius to 25 m would reject the
first pass and still accept the second. The only thing that separates "this is the
finish" from "this is the road coming back past the start" is knowing how long the
route was meant to be, and length guesses have failed in this codebase three times
in one night already.

So the rule stays as it is, and it is stated plainly instead: **if your route comes
back past its own start line, the clock stops there.** The lever is `F10` — put your
line somewhere the route does not revisit.

## Giving the courses a name

A start line taken from the archive is called `course_1300_275`, because that is
where it is. That is a correct identifier and a poor label — and since the free
world puts it on screen, it is now a label.

```powershell
python scripts/name_courses.py --karten          # draw each course
python scripts/name_courses.py --setzen course_1300_275 "Hokubu Sprint"
python scripts/name_courses.py --zeiten           # what you have driven, and your best
```

`--karten` writes a `course_map.svg` into every course folder: every recorded lap
of that course drawn to scale, with the start line ringed. Open it, recognise the
road, name it. It takes a few seconds per course.

Once a course has a name, its folder carries it: the app renames
`course_2800_5000_to_2775_5000` to `Soni Circuit (course_2800_5000_to_2775_5000)` at
its next start, and a new course gets the named folder straight away when the lap
knows its route. The coordinate key in brackets stays the identity: it is what
every lap file stores as `Course`, what goes to the website, and what
`--setzen` accepts, and a name set with `--setzen` is never changed by the app.

Runs that stopped before the finish live in `laps\unfinished`, not among the
courses. The game reports a restart, a pause or leaving the race exactly like a
finish, so every restart used to become its own "course" ending wherever the car
was: 31 of 70 folders in one archive were such pieces, 16 of them from
Shimanoyama Sprint alone. A run counts as unfinished when it starts on the start
line of a longer known course, stays on that course's road, and stops well
before its finish. It gets no route name, is no personal record and is never
submitted. Its folder is named after the course it belongs to, for example
`unfinished\Shimanoyama Sprint (course_-6350_-3750_to_-7075_-3350)`. Existing
pieces are moved there at start-up.

A course named automatically also changes its name when its own laps clearly
disagree: at least three laps name another route, three times as many as name
the current one. That is how `course_100_4375_to_450_3575` went from "Venus
Sprint" to "Shikisai Sprint".

Two automatic routes to those names were tried and both fail on the data:

- **By route length.** The catalogue knows 89 routes with a length. Against the 32
  recorded courses, not one becomes unique; 29 of 32 have more than four
  candidates and one has 21. Lengths between 1 and 9 km simply crowd.
- **By your own leaderboard time.** Sharper — a lap time to the millisecond is as
  good as unique. But your gamertag does not appear in the scans: they take the
  top ranks, and your time sits below them.

What remains is recognising the shape, which a person does in an instant.

## Going back into a race

When the game clock starts running the recorder switches back. It also resets its
lap number to "first look" — without that it would sit unarmed until the next lap
change, and in a point-to-point sprint there is never one. That would lose the
whole race.
