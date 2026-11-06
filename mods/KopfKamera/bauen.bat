@echo off
rem Baut KopfKamera.dll und kopiert sie ins Default-Profil und ins Freunde-Paket.
cd /d "%~dp0"
dotnet build -c Release -nologo || goto :fehler
set PLUG=%APPDATA%\Thunderstore Mod Manager\DataFolder\REPO\profiles\Default\BepInEx\plugins\strehl-KopfKamera
if not exist "%PLUG%" mkdir "%PLUG%"
copy /y bin\Release\KopfKamera.dll "%PLUG%\" >nul
if not exist fuer_freunde mkdir fuer_freunde
copy /y bin\Release\KopfKamera.dll fuer_freunde\ >nul
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "Compress-Archive -Force -Path 'fuer_freunde\*' -DestinationPath 'KopfKamera_fuer_Freunde.zip'"
echo Fertig: Default-Profil, fuer_freunde\ und KopfKamera_fuer_Freunde.zip aktualisiert.
pause
exit /b 0
:fehler
echo Bauen fehlgeschlagen.
pause
exit /b 1
