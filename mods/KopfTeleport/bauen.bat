@echo off
rem Baut KopfTeleport.dll und kopiert sie ins Default-Profil und ins Freunde-Paket.
rem Die alte HeadTeleport.dll (kaputt, gleiche Taste F7) wird dabei zu .old umbenannt.
cd /d "%~dp0"
dotnet build -c Release -nologo || goto :fehler
set PROF=%APPDATA%\Thunderstore Mod Manager\DataFolder\REPO\profiles\Default\BepInEx\plugins
set PLUG=%PROF%\strehl-KopfTeleport
if not exist "%PLUG%" mkdir "%PLUG%"
copy /y bin\Release\KopfTeleport.dll "%PLUG%\" >nul
if exist "%PROF%\strehl-HeadTeleport\HeadTeleport.dll" (
  move /y "%PROF%\strehl-HeadTeleport\HeadTeleport.dll" "%PROF%\strehl-HeadTeleport\HeadTeleport.dll.old" >nul
  echo Alte HeadTeleport.dll zu HeadTeleport.dll.old umbenannt.
)
if not exist fuer_freunde mkdir fuer_freunde
copy /y bin\Release\KopfTeleport.dll fuer_freunde\ >nul
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "Compress-Archive -Force -Path 'fuer_freunde\*' -DestinationPath 'KopfTeleport_fuer_Freunde.zip'"
echo Fertig: Default-Profil, fuer_freunde\ und KopfTeleport_fuer_Freunde.zip aktualisiert.
pause
exit /b 0
:fehler
echo Bauen fehlgeschlagen.
pause
exit /b 1
