@echo off
rem  Startet den Aufseher fuer die Auswertungsseite -- ohne sichtbares Fenster.
rem  Vom Aufgabenplaner bei der Anmeldung aufgerufen, per Doppelklick genauso gut.
rem
rem  Einrichten:  scripts\install_site_task.cmd
rem  Protokoll:   data\runtime\site_keeper.log

cd /d "%~dp0.."
start "" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden ^
  -File "%~dp0keep_site_up.ps1"
exit /b 0
