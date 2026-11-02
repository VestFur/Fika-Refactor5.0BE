using System;

namespace SPTushonka.Reflection.Patching;

// 迁移期间保留的元数据。PatchManager 会拒绝仍在的 Mono
// transpiler，而不是让 SPT 那套只支持 prefix/postfix 的加载器把它们跳过。
[AttributeUsage(AttributeTargets.Method)] public sealed class PatchTranspilerAttribute : Attribute { }
// 47888 SPTushonka.Reflection 已经提供了该特性：[AttributeUsage(AttributeTargets.Class)] public sealed class IgnoreAutoPatchAttribute : Attribute { }
// 47888 SPTushonka.Reflection 已经提供了该特性：[AttributeUsage(AttributeTargets.Class)] public sealed class DebugPatchAttribute : Attribute { }
