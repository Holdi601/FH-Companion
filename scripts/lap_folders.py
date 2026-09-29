"""Kursordner im Rundenbestand: "Soni Circuit (course_2800_5000_to_2775_5000)".

Seit 2026-09-30 traegt der Ordner den Streckennamen vor der Kennung (die App
benennt ihn beim Start um, LapArchive.OrdnerBenennen). Die KENNUNG bleibt die
Identitaet -- in jeder Rundendatei ("Course"), bei der Einreichung, auf dem
Server. Wer vom Ordner zur Kennung will, nimmt kennung(); wer von der Kennung
zum Ordner, kurs_pfad(). Alte Ordner, die nur "course_..." heissen, gelten
weiter.
"""

from __future__ import annotations

import re
from pathlib import Path

_AM_ENDE = re.compile(r"\((course_[^()\s]+)\)\s*$")


def kennung(ordner_name: str) -> str | None:
    """Die Kennung zu einem Ordnernamen, benannt oder nicht; sonst None."""
    n = (ordner_name or "").strip()
    if n.lower().startswith("course_") and " " not in n:
        return n
    m = _AM_ENDE.search(n)
    return m.group(1) if m else None


def kurs_ordner(wurzel: Path) -> list[Path]:
    """Alle Kursordner unter der Wurzel."""
    if not wurzel.is_dir():
        return []
    return [d for d in wurzel.iterdir() if d.is_dir() and kennung(d.name)]


def kurs_pfad(wurzel: Path, schluessel: str) -> Path:
    """Der Ordner zu einer Kennung (oder einem Ordnernamen), so wie er heute heisst."""
    direkt = wurzel / schluessel
    if direkt.is_dir():
        return direkt
    gesucht = kennung(schluessel) or schluessel
    for d in kurs_ordner(wurzel):
        if (kennung(d.name) or "").lower() == gesucht.lower():
            return d
    return wurzel / gesucht
