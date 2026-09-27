@echo off
setlocal EnableDelayedExpansion

rem  Den periodischen Neubau der weitergebbaren ZIP im Aufgabenplaner eintragen.
rem  Einmal ausfuehren, danach laeuft es von selbst.
rem
rem      scripts\install_haptics_zip_task.cmd            alle 3 Stunden
rem      scripts\install_haptics_zip_task.cmd 6          alle 6 Stunden
rem      scripts\install_haptics_zip_task.cmd /entfernen
rem
rem  WARUM SO OFT, WO SICH DIE DATEN SELTEN AENDERN: der Lauf tut nichts, wenn nichts
rem  neu ist -- er vergleicht den Hash des Bestands mit dem im Paket und ist dann in
rem  einer Sekunde fertig. Also darf er oft schauen. Aendert sich der Bestand (ein
rem  Sweep), ist die ZIP innerhalb von drei Stunden aktuell, auch wenn der
rem  Seitenbau selbst gerade abgebrochen wurde.
rem
rem  KEINE erhoehten Rechte: die Aufgabe laeuft unter dem angemeldeten Benutzer.
rem  Damit ist sie an eine Anmeldung gebunden -- fuer einen Rechner, der zum Spielen
rem  benutzt wird, genau richtig, und es wird kein Kennwort abgefragt.

set "NAME=FH Companion ZIP"
set "TARGET=%~dp0refresh_haptics_zip.cmd"
set "HOURS=%~1"

if /i "%HOURS%"=="/entfernen" goto :remove
if /i "%HOURS%"=="/remove" goto :remove
if not defined HOURS set "HOURS=3"

echo.
echo   Aufgabe:   %NAME%
echo   startet:   %TARGET%
echo   Rhythmus:  alle %HOURS% Stunden, erstmals um 06:00
echo.

rem  UNSICHTBAR (seit 2026-09-27): ueber conhost --headless, sonst geht bei jedem Lauf
rem  ein schwarzes Fenster auf -- auch mitten im Spiel. Das Protokoll steht weiter in
rem  data\runtime\haptics_package.log.
schtasks /create /tn "%NAME%" /tr "conhost.exe --headless \"%TARGET%\"" /sc HOURLY /mo %HOURS% /st 06:00 /f
if errorlevel 1 (
  echo.
  echo   Das Eintragen ist gescheitert. Haeufigster Grund: eine Richtlinie verbietet
  echo   neue Aufgaben. Dann bleibt der Doppelklick auf refresh_haptics_zip.cmd.
  echo.
  pause
  exit /b 1
)

echo.
echo   Eingetragen. Sofort einmal laufen lassen:
echo       schtasks /run /tn "%NAME%"
echo   Nachsehen, was sie tat:
echo       data\runtime\haptics_package.log
echo   Wieder weg:
echo       scripts\install_haptics_zip_task.cmd /entfernen
echo.
exit /b 0

:remove
schtasks /delete /tn "%NAME%" /f
if errorlevel 1 (
  echo   Es war keine solche Aufgabe eingetragen.
  exit /b 1
)
echo   Entfernt.
exit /b 0
