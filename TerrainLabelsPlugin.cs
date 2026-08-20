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

namespace TerrainLabels;

public sealed class TerrainLabelsPlugin : BaseSettingsPlugin<TerrainLabelsSettings>
{
    private sealed record Match(TerrainLabelDefinition Definition, Vector2i GridPosition, string Source);

    private readonly object _sync = new();
    private List<TerrainLabelDefinition> _definitions = [];
    private List<Match> _matches = [];
    private string _status = "Not scanned yet.";
    private bool _editorOpen = true;

    private string LabelsPath => Path.Combine(DirectoryFullName, "labels.json");

    public TerrainLabelsPlugin() => Name = "Terrain Labels";

    public override bool Initialise()
    {
        LoadDefinitions();
        return true;
    }

    public override void AreaChange(AreaInstance area) => ScanCurrentArea();

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
            _definitions.Add(new TerrainLabelDefinition());
            SaveDefinitions();
        }

        ImGui.Checkbox("Edit target labels", ref _editorOpen);
        if (!_editorOpen) return;

        var removeAt = -1;
        for (var i = 0; i < _definitions.Count; i++)
        {
            var definition = _definitions[i];
            ImGui.PushID(i);
            if (ImGui.TreeNodeEx($"{definition.Label}##target", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var enabled = definition.Enabled;
                var pattern = definition.Pattern;
                var label = definition.Label;
                var textColor = ToVector4(definition.TextColor, Vector4.One);
                var backgroundColor = ToVector4(definition.BackgroundColor, new Vector4(0, 0, 0, .75f));
                var changed = ImGui.Checkbox("Enabled", ref enabled);
                changed |= ImGui.InputText("Target pattern", ref pattern, 512);
                changed |= ImGui.InputText("Map label", ref label, 128);
                changed |= ImGui.ColorEdit4("Text color", ref textColor);
                changed |= ImGui.ColorEdit4("Background color", ref backgroundColor);
                if (changed)
                {
                    definition.Enabled = enabled;
                    definition.Pattern = pattern;
                    definition.Label = label;
                    definition.TextColor = FromVector4(textColor);
                    definition.BackgroundColor = FromVector4(backgroundColor);
                    CompileMatcher(definition);
                    SaveDefinitions();
                    ScanCurrentArea();
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
    }

    private void ScanCurrentArea()
    {
        try
        {
            var enabled = _definitions.Where(x => x.Enabled && x.Matcher != null).ToList();
            var found = new List<Match>();
            foreach (var tile in GameController.IngameState.Data.Tiles)
            {
                var sources = new[] { tile.Name, tile.Path }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var source in sources)
                foreach (var definition in enabled)
                {
                    if (definition.Matcher!.IsMatch(source))
                        found.Add(new Match(definition, tile.Coordinate, source));
                }
            }

            // Tile name and path can contain the same value. Keep only one label at a coordinate.
            found = found
                .DistinctBy(x => (x.Definition, x.GridPosition))
                .ToList();
            lock (_sync) _matches = found;
            _status = $"{found.Count} label location(s) found for {enabled.Count} enabled target(s).";
        }
        catch (Exception ex)
        {
            lock (_sync) _matches = [];
            _status = $"Scan failed: {ex.Message}";
            DebugWindow.LogError($"[Terrain Labels] {ex}");
        }
    }

    private void LoadDefinitions()
    {
        try
        {
            if (!File.Exists(LabelsPath))
            {
                _definitions = [new TerrainLabelDefinition()];
                SaveDefinitions();
            }
            else
            {
                _definitions = JsonConvert.DeserializeObject<List<TerrainLabelDefinition>>(File.ReadAllText(LabelsPath)) ?? [];
            }
            foreach (var definition in _definitions) CompileMatcher(definition);
            _status = $"Loaded {_definitions.Count} target definition(s).";
        }
        catch (Exception ex)
        {
            _definitions = [];
            _status = $"Could not load labels.json: {ex.Message}";
            DebugWindow.LogError($"[Terrain Labels] {ex}");
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
            DebugWindow.LogError($"[Terrain Labels] {ex}");
        }
    }

    private static void CompileMatcher(TerrainLabelDefinition definition)
    {
        try
        {
            // Glob syntax: * matches any number of characters and ? matches one.
            var regex = "^" + Regex.Escape(definition.Pattern.Trim())
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            definition.Matcher = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
        catch
        {
            definition.Matcher = null;
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

    private void DrawLabel(string label, Vector2 center, TerrainLabelDefinition definition)
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
