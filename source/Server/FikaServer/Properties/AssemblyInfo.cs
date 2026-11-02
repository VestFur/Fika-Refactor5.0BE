using FikaServer;
using System.Reflection;
using System.Runtime.InteropServices;

// 在像本项目这样的 SDK 风格项目中，历史上定义在此文件中的若干程序集特性
// 现在会在构建期间自动添加，并填充以
// 项目属性中定义的值。关于包含哪些特性
// 以及如何自定义该过程的详细信息，请参见：https://aka.ms/assembly-info-properties


// 将 ComVisible 设为 false 会使本程序集中的类型对 COM
// 组件不可见。如果需要从 COM 访问本程序集中的某个类型，
// 请在该类型上把 ComVisible 特性设为 true。

[assembly: ComVisible(false)]

// 若本项目向 COM 公开，则以下 GUID 用于类型库的 ID。

[assembly: Guid("97e4e53b-117b-48c5-965f-dc6ce02ede3d")]
[assembly: AssemblyVersion(FikaModMetadata.FikaVersion)]
[assembly: AssemblyFileVersion(FikaModMetadata.FikaVersion)]
[assembly: AssemblyDescription("Play with friends in SPT")]
[assembly: AssemblyCompany("https://github.com/project-fika")]
[assembly: AssemblyCopyright("Copyright © Lacyway 2026")]
[assembly: AssemblyTitle("Project Fika - Server Plugin")]
[assembly: AssemblyProduct("Project Fika")]
