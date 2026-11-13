using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace Fika.Core.Main.Utils;

internal static class NativeInjectedGc
{
    internal static unsafe void PreserveReferences(Type type)
    {
        var pointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)pointer);
        if (!IL2CPP.il2cpp_class_has_references((IntPtr)klass.Parent) && klass.FieldCount == 0) return;

        // The installed injector zero-initializes class metadata and sets
        // initialized=true, but leaves has_references=false. Native allocation
        // then uses atomic GC storage, so inherited reference fields are not
        // traced. Resolve the layout from the runtime's selected handler rather
        // than hard-coding a class offset for one Unity version.
        var layout = klass.GetType().GetProperty("_", BindingFlags.NonPublic | BindingFlags.Instance)?.PropertyType.GetElementType()
            ?? throw new NotSupportedException("Cannot resolve injected class GC layout.");
        var flags = layout.GetField("_bitfield0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(layout.FullName, "_bitfield0");
        var mask = Convert.ToByte(Enum.Parse(flags.FieldType, "has_references"));
        var offset = Marshal.OffsetOf(layout, flags.Name).ToInt32();
        var descriptor = Marshal.OffsetOf(layout, "gc_desc").ToInt32();
        if (Marshal.ReadIntPtr(pointer, descriptor) != IntPtr.Zero)
            throw new NotSupportedException("Injected class already has a GC descriptor; its field layout must be reviewed.");
        // A null descriptor plus has_references requests conservative tracing,
        // covering inherited and injected fields, including future assignments.
        Marshal.WriteByte(pointer, offset, (byte)(Marshal.ReadByte(pointer, offset) | mask));
        if (!IL2CPP.il2cpp_class_has_references(pointer))
            throw new InvalidOperationException($"Native reference tracing was not enabled for {type}.");
    }
}
