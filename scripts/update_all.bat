@echo off
setlocal

rem  App Center - update every app, and close when done.
rem
rem  The same as "Update all" in App Center, without the question: the window
rem  opens on Manage, works through the updates and closes again. If App Center
rem  is already open, that window does the updating and stays open.
rem
rem  Windows asks for administrator permission before updating an app that is
rem  installed for every user of the PC, and the updates wait until someone
rem  answers. To leave those apps out and let it run on its own - from Task
rem  Scheduler, say - use update_all_user.bat instead.
rem
rem  Exit code:  0  every update went through, or there was nothing to update
rem              1  at least one update did not go through
rem              2  nothing was started
rem              3  App Center closed before the updates finished

start "" /wait "%~dp0AppCenter.exe" --update-all --exit
set "CODE=%errorlevel%"

if "%CODE%"=="0" echo Updates done.
if "%CODE%"=="1" echo Some updates did not go through. Open App Center to see why.
if "%CODE%"=="2" echo Nothing was updated: App Center could not start the updates.
if "%CODE%"=="3" echo App Center closed before the updates finished. Open it to see which went in.

exit /b %CODE%
