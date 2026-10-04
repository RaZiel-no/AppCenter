@echo off
setlocal

rem  App Center - update this user's apps, and close when done.
rem
rem  The same as "Update this user's apps" in App Center, without the question:
rem  the window opens on Manage, works through the updates of the apps
rem  installed for this user, and closes again. If App Center is already open,
rem  that window does the updating and stays open.
rem
rem  Apps installed for every user of the PC are left out, since Windows would
rem  ask for administrator permission before updating them and wait for an
rem  answer. That makes this the one to run with nobody there - from Task
rem  Scheduler, say. Use update_all.bat, or App Center itself, for the rest.
rem
rem  In Task Scheduler, set the task to run only when the user is logged on.
rem  Run whether the user is logged on or not, it has no desktop: the window
rem  cannot show, and the updates of Store and MSIX apps fail.
rem
rem  They are left out even when this runs as administrator. Run it without
rem  "Run as administrator" all the same: an elevated winget refuses some apps
rem  installed for one user only, so those would fail.
rem
rem  Exit code:  0  every update went through, or there was nothing to update
rem              1  at least one update did not go through
rem              2  nothing was started - including when App Center could not
rem                 tell which apps are installed for every user
rem              3  App Center closed before the updates finished

start "" /wait "%~dp0AppCenter.exe" --update-all --user --exit
set "CODE=%errorlevel%"

if "%CODE%"=="0" echo Updates done. Apps installed for every user of the PC were left out.
if "%CODE%"=="1" echo Some updates did not go through. Open App Center to see why.
if "%CODE%"=="2" echo Nothing was updated: App Center could not start the updates.
if "%CODE%"=="3" echo App Center closed before the updates finished. Open it to see which went in.

exit /b %CODE%
