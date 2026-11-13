using System;
using System.Threading.Tasks;

namespace Fika.Core;

public static class TaskExtensions
{
    /// <summary>
    /// 用于安全地忽略一个异步 Task、同时仍然记录异常的扩展方法。 <br/>
    /// 这与“fire-and-forget”类似，但避免了静默吞掉错误。
    /// </summary>
    /// <param name="task">要运行并忽略的 Task。</param>
    public static async void Forget(this Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            // Task 的失败可能在没有 IL2CPP 挂载的情况下从 CLR 工作线程到达。
            FikaPlugin.Instance.FikaLogger.LogError(e.ToString());
        }
    }
}
