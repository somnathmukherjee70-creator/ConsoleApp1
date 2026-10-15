using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace RevitCopilot.Core;

/// <summary>
/// The Revit API may only be used on Revit's main thread inside an API context.
/// The AI conversation runs asynchronously, so every model read/write is queued here
/// and executed by Revit through an ExternalEvent; callers simply await the result.
/// </summary>
public sealed class RevitBridge : IExternalEventHandler
{
    private readonly ConcurrentQueue<(Func<UIApplication, string> Work, TaskCompletionSource<string> Done)> _queue = new();
    private readonly ExternalEvent _event;

    public RevitBridge() => _event = ExternalEvent.Create(this);

    public Task<string> RunAsync(Func<UIApplication, string> work)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((work, done));
        _event.Raise();
        return done.Task;
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var job))
        {
            try
            {
                job.Done.SetResult(job.Work(app));
            }
            catch (Exception ex)
            {
                job.Done.SetException(ex);
            }
        }
    }

    public string GetName() => "Revit Copilot";
}
