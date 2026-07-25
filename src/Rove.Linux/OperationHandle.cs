using Rove.Core.FileSystem;

namespace Rove.Linux;

/// <summary>
/// A running operation. This exists as an object rather than operations being
/// bare awaitable calls because pause, resume and cancel cannot be retrofitted
/// onto a Task — and a transfer queue needs all three.
/// </summary>
public sealed class OperationHandle : IOperationHandle
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _bytesDone;
    private long _bytesTotal;
    private int _itemsDone;
    private int _itemsTotal;
    private string? _currentItem;

    public Guid Id { get; } = Guid.NewGuid();
    public OperationState State { get; private set; } = OperationState.Queued;
    public Exception? Error { get; private set; }

    public IProgress<OperationProgress> Progress => ProgressReporter;
    public Progress<OperationProgress> ProgressReporter { get; } = new();

    public Task Completion => _completion.Task;
    internal CancellationToken Token => _cts.Token;

    public event EventHandler<OperationProgress>? Progressed;
    public event EventHandler? StateChanged;

    internal void Begin(int itemsTotal, long totalBytes)
    {
        _itemsTotal = itemsTotal;
        _bytesTotal = totalBytes;
        SetState(OperationState.Running);
        Report();
    }

    internal void ItemStarted(string path)
    {
        _currentItem = Path.GetFileName(path);
        Report();
    }

    internal void ItemFinished()
    {
        Interlocked.Increment(ref _itemsDone);
        Report();
    }

    internal void BytesCopied(long count)
    {
        Interlocked.Add(ref _bytesDone, count);
        Report();
    }

    internal void Complete()
    {
        SetState(OperationState.Completed);
        _completion.TrySetResult();
    }

    internal void Cancelled()
    {
        SetState(OperationState.Cancelled);
        _completion.TrySetResult();
    }

    internal void Failed(Exception ex)
    {
        Error = ex;
        SetState(OperationState.Failed);
        _completion.TrySetResult();
    }

    /// <summary>Blocks the worker while paused, without burning a thread spinning.</summary>
    internal async Task WaitIfPausedAsync()
    {
        if (_gate.IsSet) return;
        await Task.Run(() => _gate.Wait(_cts.Token), _cts.Token).ConfigureAwait(false);
    }

    public void Pause()
    {
        if (State != OperationState.Running) return;
        _gate.Reset();
        SetState(OperationState.Paused);
    }

    public void Resume()
    {
        if (State != OperationState.Paused) return;
        _gate.Set();
        SetState(OperationState.Running);
    }

    public void Cancel()
    {
        _gate.Set();
        _cts.Cancel();
    }

    private void SetState(OperationState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Report()
    {
        var snapshot = new OperationProgress(
            Interlocked.Read(ref _bytesDone),
            _bytesTotal,
            Volatile.Read(ref _itemsDone),
            _itemsTotal,
            _currentItem);

        Progressed?.Invoke(this, snapshot);
    }
}
