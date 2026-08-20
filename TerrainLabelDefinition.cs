using Newtonsoft.Json;

namespace TerrainLabels;

public sealed class TerrainLabelDefinition
{
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/*";
    public string Label { get; set; } = "Mercenary";

    // RGBA values are kept as floats so labels.json stays easy to edit by hand.
    public float[] TextColor { get; set; } = [1f, 0.85f, 0.15f, 1f];
    public float[] BackgroundColor { get; set; } = [0f, 0f, 0f, 0.75f];

    [JsonIgnore]
    internal System.Text.RegularExpressions.Regex? Matcher { get; set; }
}
