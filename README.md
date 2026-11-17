# Backrooms

An ExileCore plugin that finds terrain tile names, paths, and Radar-style room names and puts configurable labels on the Path of Exile minimap.

## Features

- Plain text performs a case-insensitive partial match, such as `Ranger2.arm`.
- Matches both terrain tiles and `AreaGraphs` rooms; room labels are placed at the room center.
- Glob patterns such as `Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/*`.
- `*` matches any text and `?` matches one character.
- Raw case-insensitive regular expressions use the `regex:` prefix, such as
  `regex:TerrainFeatures/(Ranger|Marauder)[0-9]+\.arm$`.
- Any number of independent target definitions.
- Each label can contain multiple room or terrain patterns for easier grouping.
- Custom map label, text color, background color, enabled state, and padding.
- Labels are rescanned when the area changes or when a definition is edited.
- Settings are saved to `labels.json` in the installed plugin directory.
- A manual **Rescan current area** button and debug mode expose scan counts,
  retry state, matched terrain paths, and grid coordinates.
- Debug mode includes a ten-row scrollable room browser with one-click creation
  of exact room-label targets.
- Each target's enabled checkbox is available directly on its collapsed header.
- Labels can be reordered with **Up** and **Down** controls.

## Build

```powershell
$env:exapiPackage = 'C:\path\to\ExileApi'
$env:ExApiPluginOutputPath = 'C:\path\to\ExileApi\Plugins\Source\'
dotnet build .\Backrooms.csproj -c Release
```

The default target is the Mercenaries terrain-feature folder. Open **Backrooms** in ExileCore settings, rename the label or change its colors, and use **Add target** for more definitions.

Example `labels.json` entry:

```json
{
  "Enabled": true,
  "Patterns": [
    "Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/Marauder1.arm",
    "Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/Ranger2.arm"
  ],
  "Label": "Marauder",
  "TextColor": [1.0, 0.85, 0.15, 1.0],
  "BackgroundColor": [0.0, 0.0, 0.0, 0.75]
}
```
