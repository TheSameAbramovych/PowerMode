using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PowerMode.Core.Logging;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// One tray instance per user session (mutex). Later instances hand their command to it over a named pipe
/// whose ACL lets the same user connect even when the tray instance runs elevated.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string Accepted = "ok";
    private const string Category = "instance";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

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

    /// <summary>True when this process is now the single instance.</summary>
    public bool TryAcquire()
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
                    // The previous instance crashed; the mutex is ours now.
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

    /// <summary>Send one command line to the running instance. Returns false when there is none.</summary>
    public static async Task<bool> TryForwardAsync(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);
            await writer.WriteLineAsync(message).ConfigureAwait(false);
            var reply = await reader.ReadLineAsync().WaitAsync(ConnectTimeout).ConfigureAwait(false);
            return reply == Accepted;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Serve forwarded commands until disposed. <paramref name="onMessage"/> runs on a pool thread.</summary>
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
                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server) { AutoFlush = true };
                    var message = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    await writer.WriteLineAsync(Accepted).ConfigureAwait(false);
                    if (message is not null)
                    {
                        log.Write(Category, $"Forwarded: {message}");
                        onMessage(message);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    log.Error(Category, "Pipe error", ex);
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
