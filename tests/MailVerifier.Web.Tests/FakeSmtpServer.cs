using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailVerifier.Web.Tests;

/// <summary>Scriptable loopback SMTP server that records sessions and RCPT commands.</summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _sessions;

    /// <summary>Reply to RCPT TO for an address (without angle brackets).</summary>
    public Func<string, string> RcptReply { get; set; } = _ => "250 2.1.5 OK";

    /// <summary>Close the connection after this many RCPTs in a session (after sending 421). 0 = never.</summary>
    public int CloseAfterRcpts { get; set; }

    public ConcurrentQueue<string> RcptCommands { get; } = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Sessions => _sessions;

    public FakeSmtpServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref _sessions);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            var rcpts = 0;

            try
            {
                await writer.WriteLineAsync("220 fake.test ESMTP");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var upper = line.ToUpperInvariant();
                    if (upper.StartsWith("EHLO"))
                    {
                        await writer.WriteLineAsync("250-fake.test");
                        await writer.WriteLineAsync("250 SIZE 1000");
                    }
                    else if (upper.StartsWith("MAIL FROM"))
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (upper.StartsWith("RCPT TO"))
                    {
                        if (CloseAfterRcpts > 0 && rcpts >= CloseAfterRcpts)
                        {
                            await writer.WriteLineAsync("421 4.7.0 Too many recipients this session");
                            return;
                        }

                        rcpts++;
                        var address = line[(line.IndexOf('<') + 1)..line.LastIndexOf('>')];
                        RcptCommands.Enqueue(address);
                        await writer.WriteLineAsync(RcptReply(address));
                    }
                    else if (upper.StartsWith("QUIT"))
                    {
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
