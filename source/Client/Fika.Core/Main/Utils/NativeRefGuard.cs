using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// 在 il2cpp/Unity 引用被交给原生代码之前对其做校验。
///
/// 空的托管引用测试起来很便宜,但一个*伪造的*引用并不便宜:Il2CppInterop 会把
/// 序列化 prefab 字段、bundle 资源或失效对象槽位里恰好存在的任何东西
/// 包装起来,随后第一次原生成员访问就会在 GameAssembly 内部触发故障
/// (47888 份崩溃转储中,0xC0000005 通过 "mov rax,[rcx]" 读取 0xA),而不是
/// 抛出一个托管异常。
///
/// 本类此前的版本只检查值*看起来*像不像一个指针
/// (过小与未对齐的值会被拒绝)。22:15 的那次运行证明这还不够:
/// 到达故障点的值是 0xA,但某个字段也可能持有一个完全对齐的
/// 指针,它指向根本不是 il2cpp 对象的内存,此时写入就会破坏
/// 那里原本存在的任何东西。
///
/// 因此这里每一次检查都先向操作系统询问(「这个地址是否已提交
/// 且可读?」),然后才读取对象头、类指针和类
/// 名。在 VirtualQuery 对该值所在页作出回答之前不会解引用任何东西,因此
/// 伪造的引用会在从未被解引用的情况下被拒绝。
/// </summary>
internal static class NativeRefGuard
{
    // 真正的原生对象位于远高于此的地址,且总是按指针对齐。
    private const long MinimumNativePointer = 0x10000;
    private const long AlignmentMask = 0x7;

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint ReadableProtections = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    // 本运行时使用的 Il2CppClass 布局:image、gc_desc、name、namespaze、... parent。
    private const int KlassNameOffset = 0x10;
    private const int KlassNamespaceOffset = 0x18;
    private const int KlassParentOffset = 0x48;
    private const int MaxTypeNameLength = 128;
    private const int MaxHierarchyDepth = 10;

    /// <summary>
    /// 由 <see cref="VerifyTypeProbe"/> 设置。为 False 表示无法在一个类型已知的引用上
    /// 确认类名布局,因此此时只要求引用
    /// 可读——一个坏掉的探针不能让正常工作的 UI 停摆。
    /// </summary>
    private static bool _typeProbeTrusted;

    /// <summary>由 <see cref="VerifyTypeProbe"/> 设置。为 False 表示父链不可用。</summary>
    private static bool _hierarchyTrusted;

    private static bool _fallbackReported;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, IntPtr length);

    /// <summary>当该指针可能是一个真正的原生对象时为 true(廉价的可能性判断)。</summary>
    internal static bool IsUsable(IntPtr pointer)
    {
        long value = pointer.ToInt64();
        return value >= MinimumNativePointer && (value & AlignmentMask) == 0;
    }

    /// <summary>当包装对象存在且其指针可信时为 true。</summary>
    internal static bool IsUsable(Il2CppObjectBase instance)
    {
        if (instance is null)
        {
            return false;
        }

        try
        {
            return IsUsable(instance.Pointer);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 当整个 [pointer, pointer+length) 区间都已提交且可读时为 true。
    /// 这由内存管理器回答,并且绝不亲自解引用该地址。
    /// </summary>
    internal static bool IsProvablyReadable(IntPtr pointer, int length)
    {
        long start = pointer.ToInt64();
        if (start < MinimumNativePointer || length <= 0)
        {
            return false;
        }

        return IsReadableAddress(start) && IsReadableAddress(start + length - 1);
    }

    private static bool IsReadableAddress(long address)
    {
        try
        {
            if (VirtualQuery(new IntPtr(address), out var information,
                    new IntPtr(Marshal.SizeOf<MemoryBasicInformation>())) == IntPtr.Zero)
            {
                return false;
            }

            if (information.State != MemCommit)
            {
                return false;
            }

            if ((information.Protect & (PageNoAccess | PageGuard)) != 0)
            {
                return false;
            }

            return (information.Protect & ReadableProtections) != 0;
        }
        catch (Exception)
        {
            // 没有 kernel32(或加载器不友善):调用方沿用可能性判断的结果。
            return false;
        }
    }

    /// <summary>
    /// 读取一个对象的 il2cpp 类名。每一跳都先被证明可读,因此本方法
    /// 可以在有效性仍未知的引用上调用。
    /// </summary>
    internal static string TryReadTypeName(IntPtr instance)
    {
        IntPtr klass = TryReadPointer(instance);
        if (klass == IntPtr.Zero || !IsProvablyReadable(klass, KlassNamespaceOffset + 8))
        {
            return null;
        }

        string name = TryReadTypeNameOfKlass(klass);
        if (name is null)
        {
            return null;
        }

        IntPtr namespacePointer = TryReadPointer(klass + KlassNamespaceOffset);
        string namespaze = namespacePointer == IntPtr.Zero ? null : TryReadCString(namespacePointer);

        if (string.IsNullOrEmpty(namespaze) || !IsTypeNameLike(namespaze))
        {
            return name;
        }

        return namespaze + "." + name;
    }

    private static string TryReadTypeNameOfKlass(IntPtr klass)
    {
        IntPtr namePointer = TryReadPointer(klass + KlassNameOffset);
        if (namePointer == IntPtr.Zero)
        {
            return null;
        }

        string name = TryReadCString(namePointer);
        return name is not null && IsTypeNameLike(name) ? name : null;
    }

    /// <summary>
    /// 遍历一个对象的类层级以查找某个类名。名称匹配时返回 true,
    /// 层级可读但不包含该名称时返回 false,
    /// 而当无法安全遍历时返回 null(放在 <paramref name="hierarchyReadable"/> 中)——
    /// 如何处理由调用方决定。
    /// </summary>
    internal static bool TypeNameIsOrDerivesFrom(IntPtr instance, string expectedTypeName,
        out string actualName, out bool hierarchyReadable)
    {
        actualName = TryReadTypeName(instance);
        hierarchyReadable = false;

        IntPtr klass = TryReadPointer(instance);
        if (klass == IntPtr.Zero || !IsProvablyReadable(klass, KlassParentOffset + 8))
        {
            return false;
        }

        hierarchyReadable = true;
        var chain = new StringBuilder();
        for (int depth = 0; depth < MaxHierarchyDepth && klass != IntPtr.Zero; depth++)
        {
            string name = TryReadTypeNameOfKlass(klass);
            if (name is null)
            {
                // 链条在这一层断了;到目前为止读到的内容仍然是证据。
                hierarchyReadable = depth > 0;
                break;
            }

            if (chain.Length > 0)
            {
                chain.Append(" -> ");
            }

            chain.Append(name);
            if (string.Equals(name, expectedTypeName, StringComparison.Ordinal))
            {
                actualName = chain.ToString();
                return true;
            }

            IntPtr parent = TryReadPointer(klass + KlassParentOffset);
            if (parent == klass || (parent != IntPtr.Zero && !IsProvablyReadable(parent, KlassParentOffset + 8)))
            {
                break;
            }

            klass = parent;
        }

        if (actualName is null)
        {
            actualName = chain.ToString();
        }

        return false;
    }

    /// <summary>
    /// 当该引用是一个真实、可读的 il2cpp 对象时为 true。当
    /// 给出了 <paramref name="expectedTypeName"/> 且类层级可读时,
    /// 该对象还必须属于该类型(或由它派生)。
    /// </summary>
    internal static bool IsProvablyUsable(Il2CppObjectBase instance, string expectedTypeName, out string detail)
    {
        detail = "null";
        if (instance is null)
        {
            return false;
        }

        IntPtr pointer;
        try
        {
            pointer = instance.Pointer;
        }
        catch (Exception exception)
        {
            detail = "unreadable wrapper (" + exception.GetType().Name + ")";
            return false;
        }

        if (!IsUsable(pointer))
        {
            detail = Describe(pointer) + " is not a native pointer";
            return false;
        }

        if (!IsProvablyReadable(pointer, 8))
        {
            detail = Describe(pointer) + " is not in a committed readable page";
            return false;
        }

        string typeName = TryReadTypeName(pointer);
        if (typeName is null)
        {
            // 该页可读,但在 il2cpp 对象必须有类名的位置却没有类名。
            // 除非探针本身尚未被证实,否则视其为不可用。
            if (!_typeProbeTrusted)
            {
                ReportFallbackOnce();
                detail = Describe(pointer) + " readable, class name unreadable (type probe unproven)";
                return true;
            }

            detail = Describe(pointer) + " readable but carries no il2cpp class name";
            return false;
        }

        detail = Describe(pointer) + " (" + typeName + ")";

        if (string.IsNullOrEmpty(expectedTypeName) || !_typeProbeTrusted)
        {
            return true;
        }

        if (TypeNameIsOrDerivesFrom(pointer, expectedTypeName, out string chain, out bool hierarchyReadable))
        {
            detail = Describe(pointer) + " (" + chain + ")";
            return true;
        }

        if (!hierarchyReadable)
        {
            // 类名可读但父链不可读:此时名称本身
            // 就是可获得的最好证据,因此接受该对象。
            detail = Describe(pointer) + " (" + (chain ?? typeName) + ", hierarchy unreadable)";
            return true;
        }

        detail = Describe(pointer) + " is " + chain + ", expected " + expectedTypeName;
        return false;
    }

    /// <summary>
    /// 通过读取一个类型已知的引用来确认本类使用的 Il2CppClass 布局
    /// 与正在运行的运行时一致。必须在依赖更严格的检查之前
    /// 调用一次。
    /// </summary>
    internal static bool VerifyTypeProbe(Il2CppObjectBase knownReference, string knownTypeName)
    {
        try
        {
            string observed = knownReference is null ? null : TryReadTypeName(knownReference.Pointer);
            _typeProbeTrusted = observed is not null &&
                observed.IndexOf(knownTypeName, StringComparison.Ordinal) >= 0;

            if (_typeProbeTrusted)
            {
                _hierarchyTrusted = TypeNameIsOrDerivesFrom(knownReference.Pointer, knownTypeName,
                    out string chain, out bool hierarchyReadable) && hierarchyReadable;
                FikaGlobals.LogInfo(
                    $"[NativeRefGuard] type probe confirmed on {knownTypeName}: {observed}; hierarchy '{chain}' (readable={hierarchyReadable})");
            }
            else
            {
                _hierarchyTrusted = false;
                FikaGlobals.LogWarning(
                    $"[NativeRefGuard] type probe could not be confirmed (expected a name containing '{knownTypeName}', read '{observed ?? "null"}'); references are only checked for readability");
            }

            return _typeProbeTrusted;
        }
        catch (Exception exception)
        {
            _typeProbeTrusted = false;
            _hierarchyTrusted = false;
            FikaGlobals.LogWarning($"[NativeRefGuard] type probe failed: {exception.Message}");
            return false;
        }
    }

    internal static bool TypeProbeTrusted
    {
        get
        {
            return _typeProbeTrusted;
        }
    }

    internal static bool HierarchyTrusted
    {
        get
        {
            return _hierarchyTrusted;
        }
    }

    private static void ReportFallbackOnce()
    {
        if (_fallbackReported)
        {
            return;
        }

        _fallbackReported = true;
        FikaGlobals.LogWarning("[NativeRefGuard] the il2cpp class name could not be read; references are only required to be readable pages");
    }

    private static IntPtr TryReadPointer(IntPtr address)
    {
        if (!IsProvablyReadable(address, 8))
        {
            return IntPtr.Zero;
        }

        try
        {
            byte[] buffer = new byte[8];
            Marshal.Copy(address, buffer, 0, 8);
            return new IntPtr(BitConverter.ToInt64(buffer, 0));
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static string TryReadCString(IntPtr address)
    {
        if (!IsProvablyReadable(address, 1))
        {
            return null;
        }

        var builder = new StringBuilder(32);
        for (int offset = 0; offset < MaxTypeNameLength; offset += 8)
        {
            if (!IsProvablyReadable(address + offset, 8))
            {
                return null;
            }

            try
            {
                byte[] buffer = new byte[8];
                Marshal.Copy(address + offset, buffer, 0, 8);
                for (int index = 0; index < 8; index++)
                {
                    if (buffer[index] == 0)
                    {
                        return builder.Length == 0 ? null : builder.ToString();
                    }

                    builder.Append((char)buffer[index]);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        return builder.ToString();
    }

    private static bool IsTypeNameLike(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        char first = name[0];
        if (!char.IsLetter(first) && first != '_')
        {
            return false;
        }

        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                continue;
            }

            switch (character)
            {
                case '_':
                case '.':
                case '`':
                case '<':
                case '>':
                case '[':
                case ']':
                case '+':
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>引用的简短、适合日志的描述,可安全用于任何值。</summary>
    internal static string Describe(Il2CppObjectBase instance)
    {
        if (instance is null)
        {
            return "null";
        }

        try
        {
            return Describe(instance.Pointer);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>原始指针值的简短、适合日志的描述。</summary>
    internal static string Describe(IntPtr pointer)
    {
        long value = pointer.ToInt64();
        if (value == 0)
        {
            return "0x0";
        }

        if (!IsUsable(pointer))
        {
            return string.Format(CultureInfo.InvariantCulture, "0x{0:X} (not a pointer)", value);
        }

        string typeName = TryReadTypeName(pointer);
        return typeName is null
            ? string.Format(CultureInfo.InvariantCulture, "0x{0:X}", value)
            : string.Format(CultureInfo.InvariantCulture, "0x{0:X} ({1})", value, typeName);
    }
}
