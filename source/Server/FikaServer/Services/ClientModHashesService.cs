using SPTarkov.DI.Annotations;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class ClientModHashesService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _hashes = new();

    public bool MatchesOrAdd(string pluginId, uint hash)
    {
        // 第一个并发请求会建立基线。其余每个
        // 请求都必须与同一个值比较，包括插入失败的那个请求。
        return _hashes.GetOrAdd(pluginId, hash) == hash;
    }

    public int GetLength()
    {
        return _hashes.Count;
    }

    public bool Exists(string pluginId)
    {
        return _hashes.ContainsKey(pluginId);
    }

    public uint GetHash(string pluginId)
    {
        return _hashes[pluginId];
    }

    public void AddHash(string pluginId, uint hash)
    {
        if (!_hashes.TryAdd(pluginId, hash))
            throw new ArgumentException("A hash is already registered for " + pluginId, nameof(pluginId));
    }
}
