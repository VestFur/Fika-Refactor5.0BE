namespace Fika.Core;

/// <summary>
/// Fika 自己持有的服务位于托管域。Comfort.Singleton 的原生
/// 泛型无法以托管接口或注入的 Fika 类来闭合。
/// 游戏自己持有的服务继续使用 Comfort.Common.Singleton。
/// </summary>
public static class FikaSingleton<T> where T : class
{
    public static T Instance { get; set; }
    public static bool Instantiated => Instance != null;

    public static void Create(T instance) => Instance = instance;

    // 保持 Comfort 单例原有的生命周期契约。
    public static void Release(T instance) => Instance = null;

    public static bool TryRelease(T instance)
    {
        if (instance == null || !ReferenceEquals(Instance, instance)) return false;
        Instance = null;
        return true;
    }
}
