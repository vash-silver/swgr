@echo off
rem Build SWGR tools with the C# compiler that ships with Windows (.NET Framework 4.x). No other installs needed.
rem   build.cmd          the tray app bin\swgr-tools.exe (everything most people need) plus bin\swgr-parts.exe
rem   build.cmd all      also every standalone panel and the memread inspector (developer checks)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find %CSC%
  echo It comes with the .NET Framework 4.x, which Windows 10 and 11 include.
  exit /b 1
)
set OPT=-nologo -platform:x64 -optimize
set WIN=%OPT% -target:winexe
pushd "%~dp0tools"
if not exist ..\bin mkdir ..\bin

call :cc %WIN% -main:SwgrTools -win32icon:swgr-tools.ico -out:../bin/swgr-tools.exe swgr-tools.cs overlay.cs parts.cs ui.cs parts-overlay.cs mob-details.cs xp-tracker.cs dps-meter.cs threat.cs credits.cs kill-sound.cs kill-streak.cs mobs-panel.cs flytext.cs || goto :fail
call :cc %OPT% -out:../bin/swgr-parts.exe parts.cs || goto :fail

if /i not "%~1"=="all" goto :done
call :cc %OPT% -out:../bin/memread.exe memread.cs || goto :fail
call :cc %WIN% -out:../bin/swgr-overlay.exe overlay.cs ui.cs || goto :fail
call :cc %WIN% -main:PartsOverlay -out:../bin/swgr-parts-overlay.exe parts.cs ui.cs parts-overlay.cs || goto :fail
call :cc %WIN% -main:MobDetails -out:../bin/swgr-mob.exe parts.cs ui.cs mob-details.cs || goto :fail
call :cc %WIN% -main:XpTracker -out:../bin/swgr-xp.exe parts.cs ui.cs mob-details.cs xp-tracker.cs || goto :fail
call :cc %WIN% -main:DpsMeter -out:../bin/swgr-dps.exe ui.cs dps-meter.cs || goto :fail
call :cc %WIN% -main:ThreatPanel -out:../bin/swgr-threat.exe overlay.cs ui.cs threat.cs || goto :fail
call :cc %WIN% -main:CreditsTracker -out:../bin/swgr-credits.exe parts.cs ui.cs credits.cs || goto :fail
call :cc %WIN% -main:KillSound -out:../bin/swgr-killsound.exe overlay.cs ui.cs kill-sound.cs || goto :fail
call :cc %WIN% -main:KillStreak -out:../bin/swgr-streak.exe ui.cs kill-streak.cs || goto :fail
call :cc %WIN% -main:NearbyMobs -out:../bin/swgr-mobs.exe overlay.cs ui.cs mobs-panel.cs || goto :fail
call :cc %WIN% -main:FlyText -out:../bin/swgr-flytext.exe ui.cs dps-meter.cs flytext.cs || goto :fail

:done
popd
echo Built into bin\. Run bin\swgr-tools.exe (it sits in the system tray).
exit /b 0

:fail
popd
echo Build failed.
exit /b 1

:cc
"%CSC%" %*
exit /b %ERRORLEVEL%
