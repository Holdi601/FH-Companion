@echo off
setlocal EnableDelayedExpansion

rem  Die weitergebbare ZIP auf Stand bringen. Fuer den Aufgabenplaner gedacht, per
rem  Doppelklick genauso brauchbar.
rem
rem  WARUM EINE .CMD UND NICHT DIREKT PYTHON IM AUFGABENPLANER: der Planer startet
rem  ohne das PATH einer Anmeldesitzung, ohne Arbeitsverzeichnis und ohne Fenster, in
rem  dem eine Fehlermeldung stehen koennte. Hier wird beides gesetzt und alles in ein
rem  Protokoll geschrieben, das man Wochen spaeter noch lesen kann.
rem
rem  Entscheiden tut das Python-Skript selbst (--auto): voller Bau nur nach einer
rem  Code-Aenderung, sonst Datentausch, und gar nichts, wenn der Bestand schon drin
rem  ist. Zwei Laeufe gleichzeitig kann es nicht geben, dafuer sorgt dort die Sperre.
rem
rem  Einrichten:  scripts\install_haptics_zip_task.cmd
rem  Protokoll:   data\runtime\haptics_package.log

cd /d "%~dp0.."

set "LOG=data\runtime\haptics_package.log"
if not exist "data\runtime" mkdir "data\runtime"

set "PY="
for /f "delims=" %%V in ('py -3 -c "print(1)" 2^>nul') do if "%%V"=="1" set "PY=py -3"
if not defined PY (
  for /f "delims=" %%V in ('python -c "print(1)" 2^>nul') do if "%%V"=="1" set "PY=python"
)

if not defined PY (
  call :stamp "ABBRUCH: kein Python gefunden (weder py -3 noch python)"
  exit /b 1
)

call :stamp "Lauf beginnt (%PY%)"
%PY% scripts\build_haptics_package.py --auto %* >> "%LOG%" 2>&1
set "CODE=%ERRORLEVEL%"

if "%CODE%"=="0" (
  call :stamp "fertig"
) else (
  call :stamp "FEHLGESCHLAGEN, Code %CODE% -- siehe die Zeilen darueber"
)
exit /b %CODE%

:stamp
echo [%DATE% %TIME:~0,8%] %~1>> "%LOG%"
exit /b 0
