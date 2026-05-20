using System.Threading.Tasks;

namespace Floss.App;

internal static class TaskExtensions
{
    public static void FireAndForget(this Task task, string context)
    {
        task.ContinueWith(t =>
        {
            if (t.Exception != null)
                CrashLog.Write(t.Exception, context);
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
