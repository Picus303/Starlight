namespace Starlight.Protobuf.Registry;

/// <summary>
/// Default <see cref="IProtocolRegistryProvider"/>: indexes a fixed set of
/// registries by version string and by their first-packet CmdIds. Immutable and
/// thread-safe after construction.
/// </summary>
public sealed class ProtocolRegistryProvider : IProtocolRegistryProvider
{
    private readonly Dictionary<string, ProtocolRegistry> _byVersion;
    private readonly Dictionary<int, ProtocolRegistry> _byFirstPacket;

    public ProtocolRegistryProvider(IEnumerable<ProtocolRegistry> registries)
    {
        _byVersion = new Dictionary<string, ProtocolRegistry>(StringComparer.OrdinalIgnoreCase);
        _byFirstPacket = new Dictionary<int, ProtocolRegistry>();

        foreach (var registry in registries)
        {
            // Last registration wins per version; a duplicate version is a build
            // mistake, but staying deterministic beats throwing during startup scan.
            _byVersion[registry.Version] = registry;

            foreach (var cmdId in registry.KnownFirst)
            {
                // Collision across versions: prefer the newest (highest version number).
                if (_byFirstPacket.TryGetValue(cmdId, out var existing) &&
                    VersionRank(existing) >= VersionRank(registry))
                    continue;

                _byFirstPacket[cmdId] = registry;
            }
        }

        Registries = _byVersion.Values.ToArray();
    }

    public IReadOnlyCollection<ProtocolRegistry> Registries { get; }

    public ProtocolRegistry? ResolveByFirstPacket(int cmdId) =>
        _byFirstPacket.GetValueOrDefault(cmdId);

    public ProtocolRegistry? GetByVersion(string version) =>
        _byVersion.GetValueOrDefault(version);

    /// <summary>Numeric rank of a version (the trailing digits of e.g. <c>"V66"</c>); higher = newer.</summary>
    private static int VersionRank(ProtocolRegistry registry)
    {
        var v = registry.Version;
        var i = v.Length;
        while (i > 0 && char.IsDigit(v[i - 1])) i--;
        return i < v.Length && int.TryParse(v.AsSpan(i), out var n) ? n : 0;
    }
}
