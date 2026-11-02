using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

internal static class NativeInjectedGc
{
    /// <summary>
    /// 为注入器刚创建的类启用原生引用追踪。
    /// 当该类从其基类继承了一个 GC 描述符并且
    /// 该描述符必须被丢弃时返回 true。
    /// </summary>
    internal static unsafe bool PreserveReferences(Type type)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)pointer);
        if (!IL2CPP.il2cpp_class_has_references((IntPtr)klass.Parent) && klass.FieldCount == 0) return false;

        // 所安装的注入器会把它分配的类元数据零初始化，
        // 然后把基类的 GC 状态复制进去（gc_desc
        // 和 has_references）。原生分配会按基类的位图进行，
        // 而该位图只描述基类布局：注入器把本类自己的字段
        // 追加在基类实例大小之后，因此它们的引用槽位
        // 对带类型的 GC 遍历是不可见的。要从运行时所选的处理器中
        // 解析布局，而不是为某个 Unity 版本硬编码
        // 一个类偏移。
        var layout = klass.GetType().GetProperty("_", BindingFlags.NonPublic | BindingFlags.Instance)?.PropertyType.GetElementType()
            ?? throw new NotSupportedException("Cannot resolve injected class GC layout.");
        var flags = layout.GetField("_bitfield0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(layout.FullName, "_bitfield0");
        var mask = Convert.ToByte(Enum.Parse(flags.FieldType, "has_references"));
        var offset = Marshal.OffsetOf(layout, flags.Name).ToInt32();
        var descriptor = Marshal.OffsetOf(layout, "gc_desc").ToInt32();
        var droppedInheritedDescriptor = false;
        if (Marshal.ReadIntPtr(pointer, descriptor) != IntPtr.Zero)
        {
            // 此处存在的描述符绝不是本类自己的：注入器在同一次
            // 注册调用中从基类复制了它。丢弃它，
            // 而不是保留它或安装一个替代品。
            // 描述符为 null 时，has_references 会选择保守的
            // 追踪，它同时覆盖继承字段和注入字段，而
            // 一个按基类布局确定大小的位图只会漏掉槽位。
            Marshal.WriteIntPtr(pointer, descriptor, IntPtr.Zero);
            droppedInheritedDescriptor = true;
        }
        // 空描述符加上 has_references 请求保守追踪，
        // 覆盖继承与注入的字段，包括将来的赋值。
        Marshal.WriteByte(pointer, offset, (byte)(Marshal.ReadByte(pointer, offset) | mask));
        if (!IL2CPP.il2cpp_class_has_references(pointer))
            throw new InvalidOperationException($"Native reference tracing was not enabled for {type}.");
        return droppedInheritedDescriptor;
    }
}
