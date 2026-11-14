using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using Newtonsoft.Json;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Backrooms;

public sealed class BackroomsPlugin : BaseSettingsPlugin<BackroomsSettings>
{
    private static readonly int[] EmptyScanRetryDelaysMs = [500, 1_000, 2_000, 4_000];
    private sealed record Match(BackroomsLabelDefinition Definition, Vector2i GridPosition, string Source);

    private readonly object _sync = new();
    private List<BackroomsLabelDefinition> _definitions = [];
    private List<Match> _matches = [];
    private string _status = "Not scanned yet.";
    private bool _editorOpen = true;
    private long _nextScanAt;
    private int _emptyScanRetryIndex;
    private string _lastAreaId = "";
    private int _lastScannedTileCount;
    private int _lastScannedRoomCount;
    private List<string> _lastScannedRoomNames = [];
    private DateTimeOffset? _lastScanTime;
    private int _roomBrowserTargetIndex;

    private string LabelsPath => Path.Combine(DirectoryFullName, "labels.json");

    public BackroomsPlugin() => Name = "Backrooms";

    public override bool Initialise()
    {
        LoadDefinitions();
        // Initialise can run after the player has already entered an area, in which
        // case ExileCore will not send this plugin the preceding AreaChange event.
        _nextScanAt = 0;
        _emptyScanRetryIndex = 0;
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _lastAreaId = "";
        _nextScanAt = 0;
        _emptyScanRetryIndex = 0;
    }

    public override Job Tick()
    {
        if (!GameController.Game.IsInGameState && !GameController.Game.IsEscapeState) return null!;

        var now = Environment.TickCount64;
        var areaId = GameController.Area.CurrentArea?.Area?.Id ?? "";
        var areaChanged = !string.Equals(areaId, _lastAreaId, StringComparison.Ordinal);
        if (areaChanged)
        {
            _lastAreaId = areaId;
            _nextScanAt = 0;
            _emptyScanRetryIndex = 0;
        }

        if (now >= _nextScanAt)
        {
            ScanCurrentArea();

            // Terrain tiles can become available a few frames after AreaChange.
            // Retry a bounded number of times, then remain idle like Radar until
            // the next area change, configuration edit, or manual rescan.
            var hasMatches = false;
            lock (_sync) hasMatches = _matches.Count != 0;
            if (hasMatches || _emptyScanRetryIndex >= EmptyScanRetryDelaysMs.Length)
            {
                _nextScanAt = long.MaxValue;
                if (!hasMatches)
                    _status += " Automatic retries finished; use Rescan current area to try again.";
            }
            else
            {
                _nextScanAt = now + EmptyScanRetryDelaysMs[_emptyScanRetryIndex++];
            }
        }
        return null!;
    }

    public override void Render()
    {
        var visibleMap = GameController.Game.IngameState.IngameUi.Map.VisibleSubMap;
        if (visibleMap == VisibleSubMap.None ||
            visibleMap == VisibleSubMap.Small && !Settings.ShowOnSmallMap ||
            visibleMap == VisibleSubMap.Large && !Settings.ShowOnLargeMap)
            return;

        List<Match> snapshot;
        lock (_sync) snapshot = [.. _matches];

        foreach (var group in snapshot.GroupBy(x => (x.Definition, x.GridPosition)))
        {
            var match = group.First();
            var label = match.Definition.Label;
            if (Settings.ShowMatchCount && group.Count() > 1)
                label += $" ({group.Count()})";
            DrawLabel(label, MapGridToScreen(match.GridPosition), match.Definition);
        }
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        ImGui.Separator();
        ImGui.TextWrapped(_status);

        if (ImGui.Button("Rescan current area")) ScanCurrentArea();
        ImGui.SameLine();
        if (ImGui.Button("Reload labels.json"))
        {
            LoadDefinitions();
            ScanCurrentArea();
        }
        ImGui.SameLine();
        if (ImGui.Button("Add target"))
        {
            _definitions.Add(new BackroomsLabelDefinition());
            SaveDefinitions();
        }

        DrawDebugInformation();

        ImGui.Checkbox("Edit target labels", ref _editorOpen);
        if (!_editorOpen) return;

        var removeAt = -1;
        var moveFrom = -1;
        var moveTo = -1;
        for (var i = 0; i < _definitions.Count; i++)
        {
            var definition = _definitions[i];
            var definitionName = string.IsNullOrWhiteSpace(definition.Label) ? "(unnamed label)" : definition.Label;
            ImGui.PushID(i);
            var headerEnabled = definition.Enabled;
            if (ImGui.Checkbox("##enabled", ref headerEnabled))
            {
                definition.Enabled = headerEnabled;
                SaveDefinitions();
                ScanCurrentArea();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(headerEnabled ? "Disable this label" : "Enable this label");
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && i > 0) (moveFrom, moveTo) = (i, i - 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i < _definitions.Count - 1) (moveFrom, moveTo) = (i, i + 1);
            ImGui.SameLine();
            // ### keeps the widget ID stable while allowing the visible label name
            // to change. Using ## here made ImGui forget the expanded state after
            // every character typed into the Map label field.
            if (ImGui.TreeNodeEx($"{definitionName}###target"))
            {
                var label = definition.Label;
                var textColor = ToVector4(definition.TextColor, Vector4.One);
                var backgroundColor = ToVector4(definition.BackgroundColor, new Vector4(0, 0, 0, .75f));
                var changed = ImGui.InputText("Map label", ref label, 128);
                changed |= ImGui.ColorEdit4("Text color", ref textColor);
                changed |= ImGui.ColorEdit4("Background color", ref backgroundColor);
                if (changed)
                {
                    definition.Label = label;
                    definition.TextColor = FromVector4(textColor);
                    definition.BackgroundColor = FromVector4(backgroundColor);
                    CompileMatcher(definition);
                    SaveDefinitions();
                    ScanCurrentArea();
                }

                ImGui.SeparatorText("Room / terrain patterns");
                var removePatternAt = -1;
                for (var patternIndex = 0; patternIndex < definition.Patterns.Count; patternIndex++)
                {
                    ImGui.PushID(patternIndex);
                    var pattern = definition.Patterns[patternIndex];
                    ImGui.SetNextItemWidth(-70);
                    if (ImGui.InputText("##pattern", ref pattern, 512))
                    {
                        definition.Patterns[patternIndex] = pattern;
                        CompileMatcher(definition);
                        SaveDefinitions();
                        ScanCurrentArea();
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove")) removePatternAt = patternIndex;
                    ImGui.PopID();
                }
                if (removePatternAt >= 0)
                {
                    definition.Patterns.RemoveAt(removePatternAt);
                    CompileMatcher(definition);
                    SaveDefinitions();
                    ScanCurrentArea();
                }
                if (ImGui.SmallButton("Add pattern"))
                {
                    definition.Patterns.Add("");
                    CompileMatcher(definition);
                    SaveDefinitions();
                }
                if (ImGui.Button("Remove")) removeAt = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this target definition.");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        if (removeAt >= 0)
        {
            _definitions.RemoveAt(removeAt);
            SaveDefinitions();
            ScanCurrentArea();
        }
        else if (moveFrom >= 0)
        {
            (_definitions[moveFrom], _definitions[moveTo]) = (_definitions[moveTo], _definitions[moveFrom]);
            SaveDefinitions();
        }
    }

    private void ScanCurrentArea()
    {
        try
        {
            var enabled = _definitions.Where(x => x.Enabled && x.Matchers.Count != 0).ToList();
            var found = new List<Match>();
            var data = GameController.IngameState.Data;
            var tiles = data.Tiles.ToList();
            var rooms = data.AreaGraphs.SelectMany(x => x.Rooms).ToList();
            _lastScannedTileCount = tiles.Count;
            _lastScannedRoomCount = rooms.Count;
            _lastScannedRoomNames = rooms
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _lastScanTime = DateTimeOffset.Now;
            foreach (var tile in tiles)
            {
                var sources = new[] { tile.Name, tile.Path }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                foreach (var definition in enabled)
                {
                    if (definition.Matchers.Any(matcher => matcher.IsMatch(source)))
                        found.Add(new Match(definition, tile.Coordinate, source));
                }
            }

            // Radar's "Show rooms" feature matches AreaGraphs room names rather
            // than Data.Tiles. Room coordinates are tile units, so convert them
            // to map-grid units and put the custom label at the room center.
            foreach (var room in rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Name)) continue;
                foreach (var definition in enabled)
                {
                    if (!definition.Matchers.Any(matcher => matcher.IsMatch(room.Name))) continue;
                    var centerX = (room.MinCoord.X + room.MaxCoord.X) * PoeMapExtension.TileToGridConversion / 2;
                    var centerY = (room.MinCoord.Y + room.MaxCoord.Y) * PoeMapExtension.TileToGridConversion / 2;
                    found.Add(new Match(definition, new Vector2i(centerX, centerY), room.Name));
                }
            }

            // Tile name and path can contain the same value. Keep only one label at a coordinate.
            found = found
                .DistinctBy(x => (x.Definition, x.GridPosition))
                .ToList();
            lock (_sync) _matches = found;
            var matchedDefinitions = found.Select(x => x.Definition).Distinct().Count();
            _status = $"Scanned {tiles.Count} terrain tiles and {rooms.Count} rooms: {found.Count} label location(s) found for {matchedDefinitions}/{enabled.Count} enabled target(s).";
            if (Settings.DebugMode)
                DebugWindow.LogMsg($"[Backrooms] {_status}");
        }
        catch (Exception ex)
        {
            lock (_sync) _matches = [];
            _status = $"Scan failed: {ex.Message}";
            DebugWindow.LogError($"[Backrooms] {ex}");
        }
    }

    private void DrawDebugInformation()
    {
        if (!Settings.DebugMode) return;

        ImGui.Separator();
        ImGui.TextColored(new Vector4(.45f, .85f, 1f, 1f), "Backrooms debug");
        ImGui.TextUnformatted($"Area: {(_lastAreaId.Length == 0 ? "(not detected)" : _lastAreaId)}");
        ImGui.TextUnformatted($"Last scan: {(_lastScanTime?.ToString("T") ?? "never")}");
        ImGui.TextUnformatted($"Terrain tiles inspected: {_lastScannedTileCount}");
        ImGui.TextUnformatted($"Rooms inspected: {_lastScannedRoomCount}");
        var retryText = _nextScanAt == long.MaxValue
            ? "idle until area change/manual scan"
            : $"retry {_emptyScanRetryIndex}/{EmptyScanRetryDelaysMs.Length} pending";
        ImGui.TextUnformatted($"Scanner: {retryText}");

        List<Match> snapshot;
        lock (_sync) snapshot = [.. _matches];
        ImGui.TextUnformatted($"Cached label locations: {snapshot.Count}");
        DrawScannedRoomBrowser();
        if (!ImGui.TreeNode("Matched terrain sources")) return;
        if (snapshot.Count == 0)
        {
            ImGui.TextDisabled("No terrain tile names, paths, or room names matched the configured patterns.");
        }
        else
        {
            var shown = 0;
            foreach (var group in snapshot.GroupBy(x => x.Definition))
            {
                var displayName = string.IsNullOrWhiteSpace(group.Key.Label) ? "(unnamed label)" : group.Key.Label;
                ImGui.PushID(group.Key.GetHashCode());
                if (ImGui.TreeNode($"{displayName} ({group.Count()})"))
                {
                    foreach (var match in group)
                    {
                        if (shown++ >= 100)
                        {
                            ImGui.TextDisabled($"...and {snapshot.Count - 100} more");
                            break;
                        }
                        ImGui.BulletText($"Grid {match.GridPosition.X}, {match.GridPosition.Y}");
                        ImGui.TextWrapped(match.Source);
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
                if (shown >= 100) break;
            }
        }
        ImGui.TreePop();
    }

    private void DrawScannedRoomBrowser()
    {
        if (!ImGui.TreeNode($"Scanned rooms ({_lastScannedRoomNames.Count})")) return;

        _roomBrowserTargetIndex = Math.Clamp(_roomBrowserTargetIndex, 0, Math.Max(0, _definitions.Count));
        var destinationName = _roomBrowserTargetIndex < _definitions.Count
            ? _definitions[_roomBrowserTargetIndex].Label
            : "New label per room";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##room_destination", $"Add rooms to: {destinationName}"))
        {
            for (var definitionIndex = 0; definitionIndex < _definitions.Count; definitionIndex++)
            {
                var name = string.IsNullOrWhiteSpace(_definitions[definitionIndex].Label)
                    ? "(unnamed label)"
                    : _definitions[definitionIndex].Label;
                if (ImGui.Selectable($"{name}##destination_{definitionIndex}", _roomBrowserTargetIndex == definitionIndex))
                    _roomBrowserTargetIndex = definitionIndex;
            }
            if (ImGui.Selectable("New label per room", _roomBrowserTargetIndex == _definitions.Count))
                _roomBrowserTargetIndex = _definitions.Count;
            ImGui.EndCombo();
        }

        var roomNames = _lastScannedRoomNames.ToArray();
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        // Value 1 is the border flag in both the older Border and newer Borders
        // ImGui.NET enum spellings used by supported ExileCore builds.
        ImGui.BeginChild("scanned_rooms", new Vector2(0, rowHeight * 10f), (ImGuiChildFlags)1, ImGuiWindowFlags.HorizontalScrollbar);
        for (var i = 0; i < roomNames.Length; i++)
        {
            var roomName = roomNames[i];
            var destination = _roomBrowserTargetIndex < _definitions.Count ? _definitions[_roomBrowserTargetIndex] : null;
            var alreadyAdded = destination?.Patterns.Any(x => string.Equals(x, roomName, StringComparison.OrdinalIgnoreCase)) == true;
            ImGui.PushID(i);
            if (alreadyAdded)
            {
                ImGui.TextDisabled("Added");
            }
            else if (ImGui.SmallButton("Add"))
            {
                if (destination == null)
                {
                    destination = new BackroomsLabelDefinition
                    {
                        Patterns = [roomName],
                        Label = DefaultLabelForRoom(roomName),
                    };
                    _definitions.Add(destination);
                    _roomBrowserTargetIndex = _definitions.Count;
                }
                else
                {
                    destination.Patterns.Add(roomName);
                }
                CompileMatcher(destination);
                SaveDefinitions();
                ScanCurrentArea();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(roomName);
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.TreePop();
    }

    private static string DefaultLabelForRoom(string roomName)
    {
        var lastSegment = roomName.Split('/').LastOrDefault() ?? roomName;
        var extensionIndex = lastSegment.LastIndexOf('.');
        return extensionIndex > 0 ? lastSegment[..extensionIndex] : lastSegment;
    }

    private void LoadDefinitions()
    {
        try
        {
            if (!File.Exists(LabelsPath))
            {
                _definitions = [new BackroomsLabelDefinition()];
                SaveDefinitions();
            }
            else
            {
                _definitions = JsonConvert.DeserializeObject<List<BackroomsLabelDefinition>>(File.ReadAllText(LabelsPath)) ?? [];
            }
            foreach (var definition in _definitions) CompileMatcher(definition);
            _status = $"Loaded {_definitions.Count} target definition(s).";
        }
        catch (Exception ex)
        {
            _definitions = [];
            _status = $"Could not load labels.json: {ex.Message}";
            DebugWindow.LogError($"[Backrooms] {ex}");
        }
    }

    private void SaveDefinitions()
    {
        try
        {
            Directory.CreateDirectory(DirectoryFullName);
            File.WriteAllText(LabelsPath, JsonConvert.SerializeObject(_definitions, Formatting.Indented));
        }
        catch (Exception ex)
        {
            _status = $"Could not save labels.json: {ex.Message}";
            DebugWindow.LogError($"[Backrooms] {ex}");
        }
    }

    private static void CompileMatcher(BackroomsLabelDefinition definition)
    {
        definition.Matchers = [];
        foreach (var pattern in definition.Patterns.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                // Glob syntax: * matches any number of characters and ? matches one.
                var regex = "^" + Regex.Escape(pattern.Trim())
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                definition.Matchers.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
            catch
            {
                // Ignore only the invalid pattern; other patterns remain usable.
            }
        }
    }

    private Vector2 MapGridToScreen(Vector2 gridPosition)
    {
        var data = GameController.Game.IngameState.Data;
        var player = data.LocalPlayer;
        var playerGrid = new Vector2(player.PosNum.X, player.PosNum.Y) * PoeMapExtension.WorldToGridConversion;
        var playerTerrain = player.GetComponent<ExileCore.PoEMemory.Components.Positioned>()?.GridPosNum ?? playerGrid;
        var map = GameController.Game.IngameState.IngameUi.Map;
        var subMap = map.VisibleSubMap == VisibleSubMap.Small ? map.SmallMiniMap : map.LargeMap.AsObject<SubMap>();
        var playerScreen = Graphics.GridToMap(playerGrid, playerTerrain, VisibleSubMap.Any);
        var playerRender = player.GetComponent<ExileCore.PoEMemory.Components.Render>();
        var deltaZ = playerRender == null ? 0 : data.GetTerrainHeightAt(gridPosition) - playerRender.UnclampedHeight;
        return playerScreen + data.TranslateGridDeltaToMapDelta(subMap, gridPosition - playerGrid, deltaZ);
    }

    private void DrawLabel(string label, Vector2 center, BackroomsLabelDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        var textColor = ToColor(definition.TextColor, new Color(255, 255, 255, 255));
        var backgroundColor = ToColor(definition.BackgroundColor, new Color(0, 0, 0, 190));
        var textSize = Graphics.MeasureText(label);
        var textPosition = center - textSize / 2;
        var padding = Settings.Padding.Value;
        if (backgroundColor.A > 0)
            Graphics.DrawBox(textPosition - new Vector2(padding), textPosition + textSize + new Vector2(padding), backgroundColor);
        Graphics.DrawText(label, textPosition, textColor);
    }

    private static Vector4 ToVector4(float[]? value, Vector4 fallback) =>
        value is { Length: >= 4 } ? new Vector4(value[0], value[1], value[2], value[3]) : fallback;

    private static float[] FromVector4(Vector4 value) => [value.X, value.Y, value.Z, value.W];

    private static Color ToColor(float[]? value, Color fallback)
    {
        if (value is not { Length: >= 4 }) return fallback;
        static byte B(float x) => (byte)(Math.Clamp(x, 0f, 1f) * 255f);
        return new Color(B(value[0]), B(value[1]), B(value[2]), B(value[3]));
    }
}
