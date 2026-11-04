@echo off
rem Works around a startup crash: CMS21-Together stores its extra save slot (4) in the game's
rem "selectedProfile" PlayerPrefs value, which the vanilla game cannot load.
rem This resets it to slot 0 and then launches the game through Steam.
reg add "HKCU\Software\Red Dot Games\Car Mechanic Simulator 2021" /v selectedProfile_h2169348711 /t REG_DWORD /d 0 /f >nul
start "" steam://rungameid/1190000
