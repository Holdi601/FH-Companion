# FH Companion documentation

Notes on every part of the project: the app, the website and the leaderboard
scanner behind the car ratings.

## The app

- [Setting up on this PC](setup_pc.md) and [for an Xbox or a second PC](setup_xbox.md): the first start, step by step with pictures.
- [The app, tab by tab](app_guide.md): every tab and every overlay feature, and what each one is for.
- [Play overlay](play_overlay.md): the in-game panels -- what to drive on the three offered routes, and where the car you are in places per class -- and how the screen is read.
- [Time attack in the free world](free_roam_time_attack.md): the app's own clock outside a race.
- [Shipping the app](haptics_package.md): the one-command package, and what the build proves before it zips.
- [Submitting your own laps](lap_submissions.md) (German): the handshake, the signature, the anti-cheat checks.
- [Defender false positive](defender-false-positive.md) (German): why the version resource does not say "Forza".

## The leaderboard scanner and the website

Start here:

- [VM rank scanning](vm_rank_scanning.md): daily workflow for starting the VM, opening Forza, and scanning a leaderboard.
- [Troubleshooting](troubleshooting.md): common failures and what they mean.
- **`python scripts/resolve_name_ties.py`** — ambiguous screen names resolved from evidence: if a variant appears elsewhere under its own name, it is not the one meant. 23 of 39 decided, 22 of them confirming what the builder already chooses.
- **`python scripts/status.py`** — does everything still work? One call: the public address, the page, the package the updater would fetch, board coverage, the scan, the VM, and the free-world start lines.
- [Contributions](contributions.md): let friends scan boards on their own machine and send the rows back -- the package, the two signatures, the admin view.
- [Submitting your own laps](lap_submissions.md) (German): the handshake, what the signature covers, what the anti-cheat checks find and what it openly does not -- built and tested, not yet switched on.
- [The site on the GNAS](gnas_deployment.md): where the website actually runs now, how the pipeline moves data in both directions, and the `up -d` trap that silently serves stale code.
- [Datenschutz](datenschutz.md) (German): exactly what is processed -- 1.38 million gamertags from the public leaderboards, submitted laps, and the two places an IP address is written. Source for the privacy section on the site.
- [Background mode](background_mode.md): older notes about why the VM path exists and how the host/guest split works.

## Running the tests

```
python scripts/run_tests.py            everything that needs neither the game nor the VM
python scripts/run_tests.py --alle     also the three that drive the game
python scripts/run_tests.py --nur lap  only the lap-submission ones
```

Eleven tests run in about 25 seconds. The three that steer the game are **skipped
and reported as skipped** -- a red run whose red means "no VM today" teaches people
to ignore red.

## Scanning many boards

`scripts/category_sweep.sh` scans a whole category from the screen, class by class
over its routes, and reads each board with `scripts/ocr_board_sweep.py`:

```
bash scripts/category_sweep.sh "Street Racing" 0-14
CLASSES="D C B" bash scripts/category_sweep.sh "Touge" 0-4
```

Stop it with `touch data/runtime/overnight/STOP`.

## Two rules worth knowing before you start

**A Rivals leaderboard needs a posted personal time** for the selected track and
performance class. If no time exists, drive/post one first. The sweep handles
the remaining navigation automatically. How much of the
(mode, track, class) matrix this rules out has not been measured; the sweep
records unreachable boards so the answer falls out of a run.

**Re-stage the guest GPU driver after every host driver update.** GPU-PV requires
an exact host/guest match, and a mismatch shows up as Problem Code 43 in the
guest, software rendering, and -- observed on 2026-08-19 -- a hard freeze of the
*host* with no bugcheck. Check it with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sync_vm_gpu_pv_drivers.ps1 -WhatIfOnly
```
