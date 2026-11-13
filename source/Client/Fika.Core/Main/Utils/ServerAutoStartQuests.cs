using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using EFT.Quests;

namespace Fika.Core.Main.Utils;

/// <summary>Tracks only quest accepts explicitly requested by an active backend response.</summary>
internal static class ServerAutoStartQuests
{
    private static readonly Dictionary<IntPtr, Dictionary<string, int>> Pending = new();

    internal static IDisposable Begin(QuestController controller, AutoStartQuestsDTO response)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (response?.templates != null)
            foreach (var template in response.templates)
                if (template != null && template.AppearStatus == EQuestStatus.Started)
                    ids.Add(template.Id);
        return new Scope(controller.Pointer, ids);
    }

    internal static bool Contains(ItemController inventory, QuestAcceptResult accept)
    {
        if (accept?.QuestController == null || accept.Quest == null) return false;
        var controller = accept.QuestController;
        return controller.InventoryController?.Pointer == inventory.Pointer &&
            Pending.TryGetValue(controller.Pointer, out var ids) && ids.ContainsKey(accept.Quest.Id);
    }

    internal static Il2CppSystem.Threading.Tasks.Task Complete(
        Il2CppSystem.Threading.Tasks.Task task, IDisposable scope) =>
        NativeTask.FromManaged(MainThreadDispatcher.RunAsync(() => Wait(task, scope)));

    private static async Task Wait(Il2CppSystem.Threading.Tasks.Task task, IDisposable scope)
    {
        using (scope) await task.AsManaged();
    }

    private sealed class Scope : IDisposable
    {
        private readonly IntPtr _controller;
        private HashSet<string> _ids;

        internal Scope(IntPtr controller, HashSet<string> ids)
        {
            _controller = controller;
            _ids = ids;
            if (ids.Count == 0) return;
            if (!Pending.TryGetValue(controller, out var pending))
                Pending.Add(controller, pending = new(StringComparer.Ordinal));
            foreach (var id in ids)
                pending[id] = pending.TryGetValue(id, out var count) ? count + 1 : 1;
        }

        public void Dispose()
        {
            if (_ids == null || _ids.Count == 0) return;
            var pending = Pending[_controller];
            foreach (var id in _ids)
                if (--pending[id] == 0) pending.Remove(id);
            if (pending.Count == 0) Pending.Remove(_controller);
            _ids = null;
        }
    }
}
