# Terrain Labels

An ExileCore plugin that finds terrain tile names/paths and puts configurable labels on the Path of Exile minimap.

## Features

- Exact targets such as `Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/Marauder1.arm`.
- Glob patterns such as `Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/*`.
- `*` matches any text and `?` matches one character; matching is case-insensitive.
- Any number of independent target definitions.
- Custom map label, text color, background color, enabled state, and padding.
- Labels are rescanned when the area changes or when a definition is edited.
- Settings are saved to `labels.json` in the installed plugin directory.

## Build

```powershell
$env:exapiPackage = 'C:\path\to\ExileApi'
$env:ExApiPluginOutputPath = 'C:\path\to\ExileApi\Plugins\Source\'
dotnet build .\TerrainLabels.csproj -c Release
```

The default target is the Mercenaries terrain-feature folder. Open **Terrain Labels** in ExileCore settings, rename the label or change its colors, and use **Add target** for more definitions.

Example `labels.json` entry:

```json
{
  "Enabled": true,
  "Pattern": "Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/Marauder1.arm",
  "Label": "Marauder",
  "TextColor": [1.0, 0.85, 0.15, 1.0],
  "BackgroundColor": [0.0, 0.0, 0.0, 0.75]
}
```
