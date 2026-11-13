using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.BaseClasses;

internal interface IFikaMagazineProcess
{
    bool IsCancelled { get; }
    Task<IResult> Start();
    void Proceed(bool success);
    void TryProceedForItem(Item item);
}

/// <summary>Keeps the game's process handle so native stop/item-change paths reach Fika's batch process.</summary>
internal static class FikaMagazineProcesses
{
    private static readonly Dictionary<IntPtr, IFikaMagazineProcess> Processes = new();

    internal static void Add(Il2CppObjectBase native, IFikaMagazineProcess process) => Processes.Add(native.Pointer, process);
    internal static void Remove(Il2CppObjectBase native) => Processes.Remove(native.Pointer);
    internal static IFikaMagazineProcess Find(Il2CppObjectBase native) =>
        native != null && Processes.TryGetValue(native.Pointer, out var process) ? process : null;
}
