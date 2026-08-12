# Install

## Play the mod (prebuilt DLLs)

1. **BepInEx 5** (win x64) unzipped into the game folder, so you get `Rogue Command\BepInEx\` next to `Rogue Command.exe`. Run the game once so BepInEx generates its folders.
2. Put these into `Rogue Command\BepInEx\plugins\`:
   - `TestMod.dll` and `rcmoverlay` (from [RCM-Manager](https://github.com/RCM-development/RCM-Manager), required by every RCM mod)
   - `RCM_Randomizer.dll`
   - optional, for turret combinations: `RCM_UnitsMixNMatch.dll` + `MixNMatchUnits.txt`
3. Start the game. `F5` opens the mod panel, the Randomizer widget shows mode, seed, card count and turret pairs.

Config is generated on first run at `BepInEx\config\RCM.plugins.randomizer.cfg` (mode, intensity, max stats per roll, luck, turret shuffle size ratio).

## Build from source

The projects reference each other by folder, so clone them as siblings:

```
<your dev folder>\
    RCM-Manager\          <- required, provides TestMod.dll + the publicized game assembly
    RCM-Randomizer\
    RCM-UnitsMixNMatch\   <- optional
```

```bash
git clone https://github.com/RCM-development/RCM-Manager.git
git clone https://github.com/RCM-development/RCM-Randomizer.git
dotnet build RCM-Randomizer/RCM_Randomizer.csproj -c Release
```

The game path is auto-detected (`C:\Program Files (x86)\Steam\...`, then `D:\SteamLibrary\...`). Anywhere else, pass it explicitly:

```bash
dotnet build RCM-Randomizer/RCM_Randomizer.csproj -c Release -p:GameDir="E:\Games\Rogue Command"
```

Output lands in `RCM-Randomizer\bin\Release\RCM_Randomizer.dll`. Copy that plus `RCM-Manager\bin\Release\TestMod.dll` and `RCM-Manager\res\rcmoverlay` into the game's `BepInEx\plugins`.

### Turret combinations

Seeded turret assignment needs the `DonorSelector` hook, currently on the **`donor-hook`** branch of RCM-UnitsMixNMatch:

```bash
git clone -b donor-hook https://github.com/RCM-development/RCM-UnitsMixNMatch.git
dotnet build RCM-UnitsMixNMatch/RCM_UnitsMixNMatch.csproj -c Release
```

Without it the randomizer still rolls stats and the panel reads `Turrets no donor hook` (or `no mix&match` if the mod isn't installed at all); mix&match keeps its own per-spawn random turrets.
