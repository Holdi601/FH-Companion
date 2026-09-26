@echo off
setlocal EnableDelayedExpansion

rem  Die Auswertungsseite dauerhaft am Leben halten: Aufgabe bei der Anmeldung.
rem
rem      scripts\install_site_task.cmd              eintragen
rem      scripts\install_site_task.cmd /entfernen   wieder weg
rem
rem  WARUM BEI DER ANMELDUNG UND NICHT ALLE FUENF MINUTEN: ein Fuenf-Minuten-Takt
rem  liesse jedes Mal ein Konsolenfenster aufblitzen, mitten im Spiel. Stattdessen
rem  startet die Anmeldung EINEN stillen Aufseher, der selbst alle fuenf Minuten
rem  nachsieht (scripts\keep_site_up.ps1).
rem
rem  KEINE erhoehten Rechte, wie bei der Haptik-Aufgabe: sie laeuft unter dem
rem  angemeldeten Benutzer. Ein eingefrorener Rechner bleibt also bis zur naechsten
rem  Anmeldung ohne Seite -- dafuer wird kein Kennwort abgefragt.

set "NAME=Forza Analytics Site"
set "TARGET=%~dp0keep_site_up.cmd"

if /i "%~1"=="/entfernen" goto :remove
if /i "%~1"=="/remove" goto :remove

echo.
echo   Aufgabe:   %NAME%
echo   startet:   %TARGET%
echo   Ausloeser: bei der Anmeldung, danach alle 5 Minuten ein Blick
echo.

schtasks /create /tn "%NAME%" /tr "\"%TARGET%\"" /sc ONLOGON /f
if errorlevel 1 (
  echo.
  echo   Das Eintragen ist gescheitert. Dann bleibt der Doppelklick auf
  echo   scripts\keep_site_up.cmd nach jedem Neustart.
  echo.
  exit /b 1
)

echo.
echo   Eingetragen. Sofort starten:   schtasks /run /tn "%NAME%"
echo   Nachsehen:                     data\runtime\site_keeper.log
echo   Wieder weg:                    scripts\install_site_task.cmd /entfernen
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
