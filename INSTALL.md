# Install

## Play the mod

1. Install **BepInEx 5** (Windows x64) into the game folder, so `Rogue Command\BepInEx\` sits next to `Rogue Command.exe`. Start the game once so BepInEx creates its folders, then close it.
2. Extract the release zip into the game folder. It contains `BepInEx\plugins\` with:
   - `RCM_GUI.dll` and `RCM_GUI_assets`: the RCM mod manager (RCM GUI), needed by every RCM mod
   - `RCM_Randomizer.dll`: this mod
   - `RCM_UnitsMixNMatch.dll` and `MixNMatchUnits.txt`: turret combinations (optional; without them the randomizer still rolls everything else)
3. **Updating from 0.9.6 or older:** delete `TestMod.dll` and `rcmoverlay` from `BepInEx\plugins`. They are the old name of the mod manager; next to the new one, BepInEx loads only one of the two and the mods may not start.
4. Start the game. `F6` opens the mod panel (set another key or a combination in `BepInEx\config\RCM.plugins.modmanager.cfg`, `Hotkeys.TogglePanel`); the Randomizer widget shows the mode, seed, card count and turret pairs, and has the Reroll button.

Settings are created on first start in `BepInEx\config\RCM.plugins.randomizer.cfg`. Every entry is commented; the defaults are the intended way to play.

### Uninstalling

Delete the files above from `BepInEx\plugins`. **Finish or abandon your current run first:** a save that owns a generated upgrade or hack needs the mod to define that card. To play stock without uninstalling, set the mode to `Off` in the RCM panel (F6) instead; that is always safe.

The seed files in the game's `Profiles` folder (`randomizerSeed_<n>.txt`) are harmless and can stay.

## Build from source

The projects reference each other by folder, so clone them as siblings:

```
<dev folder>\
    RCM-Manager\          <- required: RCM_GUI.dll and the publicized game assembly
    RCM-Randomizer\
    RCM-UnitsMixNMatch\   <- optional: turret combinations
```

```bash
git clone https://github.com/RCM-development/RCM-Manager.git
git clone https://github.com/RCM-development/RCM-Randomizer.git
git clone https://github.com/RCM-development/RCM-UnitsMixNMatch.git
dotnet build RCM-Randomizer/RCM_Randomizer.csproj -c Release
```

The game folder is auto-detected under the usual Steam library paths. Anywhere else, pass it:

```bash
dotnet build RCM-Randomizer/RCM_Randomizer.csproj -c Release -p:GameDir="E:\Games\Rogue Command"
```

Copy `RCM-Randomizer\bin\Release\RCM_Randomizer.dll`, `RCM-Manager\bin\Release\RCM_GUI.dll` and `RCM-Manager\res\RCM_GUI_assets` into the game's `BepInEx\plugins`, plus the mix&match DLL and `res\MixNMatchUnits.txt` if you built it.

### Release zip

```powershell
powershell -File RCM-Randomizer\tools\package.ps1
```

Does clean Release builds of all three projects, checks that the version in `RCM_Randomizer.csproj` matches the one in `Randomizer.cs`, and writes `<dev folder>\dist\RCM-Randomizer-<version>.zip`. Pass `-GameDir` if the game is not auto-detected.
