namespace Vaktari.Core.FileSystem;

/// <summary>
/// A running operation. This exists as an object rather than operations being
/// bare awaitable calls because pause, resume and cancel cannot be retrofitted
/// onto a Task — and a transfer queue needs all three.
///
/// **In Core, not in a platform assembly.** It was written for Linux and moved
/// here on 3 August 2026 when the Windows operations needed the same state
/// machine: it is a progress counter and a pause gate, with nothing in it that
/// knows what a file is.
///
/// **The driving methods are public**, which they were not before the move. They
/// are called by the operations engine in whichever platform assembly owns the
/// work, and once the type lives here "internal to the assembly that drives it"
/// stops being expressible. They are not for the UI: it observes
/// <see cref="Progressed"/> and <see cref="State"/> and drives nothing.
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
    public CancellationToken Token => _cts.Token;

    public event EventHandler<OperationProgress>? Progressed;
    public event EventHandler? StateChanged;

    public void Begin(int itemsTotal, long totalBytes)
    {
        _itemsTotal = itemsTotal;
        _bytesTotal = totalBytes;
        SetState(OperationState.Running);
        Report();
    }

    public void ItemStarted(string path)
    {
        _currentItem = Path.GetFileName(path);
        Report();
    }

    public void ItemFinished()
    {
        Interlocked.Increment(ref _itemsDone);
        Report();
    }

    public void BytesCopied(long count)
    {
        Interlocked.Add(ref _bytesDone, count);
        Report();
    }

    public void Complete()
    {
        SetState(OperationState.Completed);
        _completion.TrySetResult();
    }

    public void Cancelled()
    {
        SetState(OperationState.Cancelled);
        _completion.TrySetResult();
    }

    public void Failed(Exception ex)
    {
        Error = ex;
        SetState(OperationState.Failed);
        _completion.TrySetResult();
    }

    /// <summary>Blocks the worker while paused, without burning a thread spinning.</summary>
    public async Task WaitIfPausedAsync()
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
