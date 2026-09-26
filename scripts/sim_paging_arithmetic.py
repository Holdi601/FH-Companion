"""Mirror of the patched paging arithmetic, run against two board models.

Board models:
  prefetching  - the healthy first-pass board: the page after the loaded one is
                 already resident, so a read of last_maximum+1 succeeds without
                 the cursor going anywhere. This is the path that delivered
                 293,354 rows on 2026-08-21 and must not change.
  on-demand    - the re-seated deep board from the S1 pass-2 trace: a page is
                 fetched only once the cursor moves strictly past the bottom of
                 the deepest loaded page, and presses below that bottom are eaten.

Asserted: on the prefetching board the patch presses exactly the same keys as
the old code and never arms crossing; on the on-demand board the dead reads per
page drop from ~5 to ~0.
"""

PREFETCH_MARGIN = 5
MAX_DOWN_BURST = 60
STALL_NUDGE_AFTER = 4
MAX_STALL_ADVANCE = 50
CROSS_TO_FETCH_AFTER = 2
NO_PROGRESS_LIMIT = 8
PAGE = 50
DEAD_READ_S = 65.0
GOOD_READ_S = 8.0
KEY_S = 0.018


class Board:
    def __init__(self, kind, first_rank, board_end):
        self.kind = kind
        self.board_end = board_end
        self.bottom = first_rank + PAGE - 1 if kind == "prefetching" else first_rank - 1
        self.cursor = first_rank - 1
        if kind == "prefetching":
            self.bottom = min(board_end, first_rank + 2 * PAGE - 1)

    def press_down(self, count):
        for _ in range(count):
            if self.cursor >= self.board_end or self.cursor > self.bottom:
                return
            self.cursor += 1
            if self.kind == "prefetching":
                # keeps a page in hand ahead of the cursor
                while self.bottom < min(self.board_end, self.cursor + PAGE):
                    self.bottom = min(self.board_end, self.bottom + PAGE)
            elif self.cursor > self.bottom:
                self.bottom = min(self.board_end, self.bottom + PAGE)

    def read(self, rank):
        return rank <= self.bottom


def run(kind, patched, pages_wanted=8, start_rank=23501, board_end=40000):
    board = Board(kind, start_rank, board_end)
    current_rank = start_rank - 1
    last_maximum = start_rank - 1
    cross_to_fetch = False
    cross_proofs = 0
    seconds = 0.0
    keys = 0
    dead = 0
    pages = 0
    while pages < pages_wanted:
        expected = last_maximum + 1
        no_progress = 0
        advanced_for = 0
        while not board.read(expected):
            no_progress += 1
            dead += 1
            seconds += DEAD_READ_S
            if no_progress >= NO_PROGRESS_LIMIT:
                return dict(status="gave up at rank %d" % expected, pages=pages, dead=dead)
            if patched:
                cross = expected + PREFETCH_MARGIN
                if no_progress >= STALL_NUDGE_AFTER:
                    cross = min(
                        cross + (no_progress - STALL_NUDGE_AFTER + 1) * 5,
                        expected + PREFETCH_MARGIN + MAX_STALL_ADVANCE,
                    )
                if current_rank < cross:
                    advance = min(MAX_DOWN_BURST, cross - current_rank)
                    board.press_down(advance)
                    current_rank += advance
                    keys += advance
                    seconds += advance * KEY_S
                    advanced_for = expected
            elif no_progress >= STALL_NUDGE_AFTER:
                board.press_down(5)
                current_rank += 5
                keys += 5
                seconds += 5 * KEY_S
        pages += 1
        seconds += GOOD_READ_S
        if patched and advanced_for == expected and not cross_to_fetch:
            cross_proofs += 1
            if cross_proofs >= CROSS_TO_FETCH_AFTER:
                cross_to_fetch = True
        last_maximum = min(board.bottom, expected + PAGE - 1)
        park = last_maximum + PREFETCH_MARGIN if cross_to_fetch else last_maximum - PREFETCH_MARGIN
        jump = min(MAX_DOWN_BURST, max(1, park - current_rank))
        board.press_down(jump)
        current_rank += jump
        keys += jump
        seconds += jump * KEY_S
    ranks = pages * PAGE
    return dict(
        status="ok",
        pages=pages,
        dead=dead,
        keys=keys,
        minutes=round(seconds / 60, 2),
        ranks_per_min=round(ranks / (seconds / 60), 1),
        crossing=cross_to_fetch,
    )


results = {}
for kind in ("prefetching", "on-demand"):
    for patched in (False, True):
        r = run(kind, patched)
        results[(kind, patched)] = r
        print(f"{kind:13} {'patched' if patched else 'old    '} {r}")

print()
healthy_old = results[("prefetching", False)]
healthy_new = results[("prefetching", True)]
assert healthy_old["keys"] == healthy_new["keys"], "healthy board key count changed"
assert healthy_old["dead"] == healthy_new["dead"] == 0, "healthy board has dead reads"
assert healthy_new["crossing"] is False, "crossing armed on a prefetching board"
print("OK  prefetching board: identical keys, no dead reads, crossing never armed")

deep_old = results[("on-demand", False)]
deep_new = results[("on-demand", True)]
assert deep_new["dead"] < deep_old["dead"] / 4, "deep board dead reads not cut"
print(
    "OK  on-demand board: dead reads %d -> %d, %.1f -> %.1f ranks/min"
    % (deep_old["dead"], deep_new["dead"], deep_old["ranks_per_min"], deep_new["ranks_per_min"])
)
