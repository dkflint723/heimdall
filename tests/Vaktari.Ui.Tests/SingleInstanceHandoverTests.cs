using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Handing a folder to the copy that is already running.
///
/// **This is the whole of "open with Vaktari" once it is the default file
/// manager.** Every double-clicked folder, and every "show in folder" that
/// reaches us, arrives as a fresh launch — so a launch that cannot hand its
/// path over is a folder that never opens.
///
/// It could not, ever. Dispose deleted the socket file unconditionally, and
/// Program disposes the instance that LOST the lock, immediately before asking
/// it to forward: the launch unlinked the running window's socket and then
/// connected to the path it had just removed. Every handover for the rest of
/// that window's life failed the same way, and nothing said so — the result was
/// discarded and the "handed over" line printed before the attempt.
///
/// Its own runtime directory. These files are per-user, not per-process, and
/// running this against the real pair would delete the socket belonging to
/// whatever copy the author has open — reproducing the bug on their machine to
/// prove it exists.
/// </summary>
public sealed class SingleInstanceHandoverTests : IDisposable
{
    private readonly string _runtime = Path.Combine(
        Path.GetTempPath(), "vaktari-instance-tests-" + Guid.NewGuid().ToString("N")[..12]);

    public SingleInstanceHandoverTests()
    {
        Directory.CreateDirectory(_runtime);
        SingleInstance.RuntimeDirectoryOverride = _runtime;
    }

    public void Dispose()
    {
        SingleInstance.RuntimeDirectoryOverride = null;

        // Only what this test made, and only ever under its own GUID directory.
        try { Directory.Delete(_runtime, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_second_launch_does_not_take_the_lock()
    {
        using var first = new SingleInstance();
        Assert.True(first.TryAcquire());

        using var second = new SingleInstance();
        Assert.False(second.TryAcquire());
    }

    /// <summary>
    /// The defect itself: the losing launch is disposed before it forwards, and
    /// that disposal must leave the running instance's channel intact.
    /// </summary>
    [Fact]
    public void Disposing_the_launch_that_lost_leaves_the_socket_alone()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());
        Assert.True(File.Exists(SingleInstance.SocketPath), "the listener never bound");

        var launch = new SingleInstance();
        Assert.False(launch.TryAcquire());
        launch.Dispose();

        Assert.True(File.Exists(SingleInstance.SocketPath),
            "the launch deleted the running instance's socket");
    }

    /// <summary>
    /// End to end, in the order Program uses: lose the lock, dispose, forward.
    /// Asserting on TryForward's return rather than on the received paths keeps
    /// this off the dispatcher — delivery raises the event on the UI thread,
    /// which is the window's business and not this channel's.
    /// </summary>
    [Fact]
    public void A_launch_that_lost_can_still_hand_its_paths_over()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        var launch = new SingleInstance();
        Assert.False(launch.TryAcquire());
        launch.Dispose();

        Assert.True(SingleInstance.TryForward([Path.GetTempPath()]),
            "nothing answered the socket");
    }

    /// <summary>
    /// **Twice, because once was never the failing case.** The first handover
    /// destroyed the channel and only the second showed it — a test that
    /// forwarded a single path would have passed against the broken code.
    /// </summary>
    [Fact]
    public void And_again_after_the_first_one()
    {
        using var running = new SingleInstance();
        Assert.True(running.TryAcquire());

        for (var i = 0; i < 3; i++)
        {
            var launch = new SingleInstance();
            launch.TryAcquire();
            launch.Dispose();

            Assert.True(SingleInstance.TryForward([Path.GetTempPath()]),
                $"handover {i + 1} found nothing listening");
        }
    }

    /// <summary>
    /// With nobody running there is nothing to hand to, and the caller has to
    /// be able to tell — that answer is what decides between forwarding and
    /// opening a window.
    /// </summary>
    [Fact]
    public void With_nothing_running_the_handover_reports_failure()
    {
        Assert.False(SingleInstance.TryForward([Path.GetTempPath()]));
    }

    /// <summary>
    /// The owner does clean up after itself: a socket file left behind would be
    /// connected to by the next launch, which would then believe it had handed
    /// its folder to a process that no longer exists.
    /// </summary>
    [Fact]
    public void The_owner_removes_the_socket_when_it_exits()
    {
        var running = new SingleInstance();
        Assert.True(running.TryAcquire());
        Assert.True(File.Exists(SingleInstance.SocketPath));

        running.Dispose();

        Assert.False(File.Exists(SingleInstance.SocketPath));
    }
}
