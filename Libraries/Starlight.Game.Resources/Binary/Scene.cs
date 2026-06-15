using System.Text.Json.Serialization;

namespace Starlight.Game.Resources.Binary;

public sealed class ScenePointConfig {
    public Dictionary<string, PointData> Points { get; set; }
}

public sealed class PointData {
    public uint PointId { get; set; }
    public uint SceneId { get; set; }

    public uint AreaId { get; set; }
    public uint GadgetId { get; set; }
    
    public string MarkIconTypeName { get; set; }
    [JsonPropertyName("$type")]
    public string Type { get; set; }

    [JsonPropertyName("pos")] public Position PointPos { get; set; }
    [JsonPropertyName("tranPos")] public Position TeleportPos { get; set; }
    [JsonPropertyName("dungeonIds")] public List<uint> DungeonIds { get; set; } = [];
    [JsonPropertyName("tranSceneId")] public uint TranSceneId { get; set; }
}
