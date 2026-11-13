using System;

namespace SPTushonka.Reflection.Patching;

// Metadata retained during migration. PatchManager rejects remaining Mono
// transpilers instead of allowing SPT's prefix/postfix-only loader to skip them.
[AttributeUsage(AttributeTargets.Method)] public sealed class PatchTranspilerAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Class)] public sealed class IgnoreAutoPatchAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Class)] public sealed class DebugPatchAttribute : Attribute { }
