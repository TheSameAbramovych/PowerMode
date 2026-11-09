using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PowerMode.Core.Logging;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// One tray instance per user session (mutex). Later instances hand their command to it over a named pipe
/// whose ACL lets the same user connect even when the tray instance runs elevated.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>The tray's acknowledgement line.</summary>
    private static readonly byte[] Accepted = "ok\n"u8.ToArray();
    private const string Category = "instance";

    /// <summary>Longest wait for each pipe step (connect, write, drain, acknowledge, a client's line).</summary>
    private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GonePoll = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private bool _disposed;

    private static string Id { get; } = BuildId();

    private static string MutexName => @"Local\" + Id;

    private static string BuildId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $"PowerMode-{identity.User!.Value}";
    }

    /// <summary>
    /// True when this process is now the single instance. A mutex left by an instance that was killed (abandoned)
    /// counts as free: it is taken over, and <paramref name="log"/> says so.
    /// </summary>
    public bool TryAcquire(ILog? log = null)
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        mutex.Dispose();
                        return false;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous instance was killed while holding it; the wait gave it to us all the same.
                    log?.Write(Category, "The previous instance ended without releasing the single-instance lock; taking it over");
                }
            }

            _mutex = mutex;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Created by an elevated instance: it exists.
            return false;
        }
    }

    public void Release()
    {
        _stop.Cancel();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>Wait until no instance holds the single-instance lock (after <c>--quit</c>). False on timeout.</summary>
    public static bool WaitUntilGone(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            using var probe = new SingleInstance();
            if (probe.TryAcquire())
            {
                probe.Release();
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(GonePoll);
        }
    }

    /// <summary>
    /// Send one command line to the running instance. True once the command has reached it (the write drained into the
    /// pipe); false when there is no instance or the write did not complete. The tray acknowledges after it has taken
    /// the command and then closes its end, so the acknowledgement is awaited, but losing it is not a failure. Every
    /// step has a timeout: a tray that does not read can never hang the caller.
    /// </summary>
    public static async Task<bool> TryForwardAsync(string message)
    {
        var written = false;
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync((int)PipeTimeout.TotalMilliseconds).ConfigureAwait(false);

            // Raw bytes, no StreamWriter: disposing a writer flushes into a pipe the tray may already have closed.
            await client.WriteAsync(Encoding.UTF8.GetBytes(message + "\n")).AsTask().WaitAsync(PipeTimeout).ConfigureAwait(false);
            await client.FlushAsync().WaitAsync(PipeTimeout).ConfigureAwait(false);
            await Task.Run(client.WaitForPipeDrain).WaitAsync(PipeTimeout).ConfigureAwait(false);
            written = true;

            using var reader = new StreamReader(client, leaveOpen: true);
            await reader.ReadLineAsync().WaitAsync(PipeTimeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return written;
        }
    }

    /// <summary>
    /// Serve forwarded commands until disposed, on a pool thread (never the UI thread); <paramref name="onMessage"/>
    /// runs there too. A client that connects but sends no line within the timeout is dropped, so it can never hold
    /// the single pipe instance against the next caller.
    /// </summary>
    public void StartServer(ILog log, Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(Id, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    connection.CancelAfter(PipeTimeout);
                    using var reader = new StreamReader(server, leaveOpen: true);
                    var message = await reader.ReadLineAsync(connection.Token).ConfigureAwait(false);
                    if (message is not null)
                    {
                        // Take the command before acknowledging it: a client that is already gone must not lose it.
                        log.Write(Category, $"Forwarded: {message}");
                        onMessage(message);
                        await server.WriteAsync(Accepted, connection.Token).ConfigureAwait(false);
                        await server.FlushAsync(connection.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    log.Write(Category, "A client connected but did not finish in time; dropped it");
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    log.Error(Category, "Pipe error", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // The pipe name is held by a process this user cannot open (another, elevated instance): stop serving.
                    log.Error(Category, "Cannot serve forwarded commands", ex);
                    return;
                }
            }
        });
    }

    private static PipeSecurity Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _mutex?.Dispose();
        _stop.Dispose();
    }
}
