using Newtonsoft.Json;

namespace Backrooms;

public sealed class BackroomsLabelDefinition
{
    public bool Enabled { get; set; } = true;
    public List<string> Patterns { get; set; } = ["Metadata/Terrain/Leagues/Mercenaries/TerrainFeatures/*"];
    public string Label { get; set; } = "Mercenary";

    // Read old labels.json files without making users recreate their targets.
    [JsonProperty("Pattern")]
    private string? LegacyPattern
    {
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) Patterns = [value];
        }
    }

    // RGBA values are kept as floats so labels.json stays easy to edit by hand.
    public float[] TextColor { get; set; } = [1f, 0.85f, 0.15f, 1f];
    public float[] BackgroundColor { get; set; } = [0f, 0f, 0f, 0.75f];

    [JsonIgnore]
    internal List<System.Text.RegularExpressions.Regex> Matchers { get; set; } = [];
}
