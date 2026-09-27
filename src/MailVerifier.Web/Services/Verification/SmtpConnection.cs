using System.Net.Sockets;
using System.Text;

namespace MailVerifier.Web.Services.Verification;

public readonly record struct SmtpReply(int Code, string Text);

/// <summary>A minimal SMTP client connection with a timeout on every read/write.</summary>
internal sealed class SmtpConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _timeoutMs;

    private SmtpConnection(TcpClient tcp, int timeoutMs)
    {
        _tcp = tcp;
        _timeoutMs = timeoutMs;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.ASCII, leaveOpen: true);
        _writer = new StreamWriter(_stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
    }

    public static async Task<SmtpConnection> OpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await WithTimeoutAsync(t => tcp.ConnectAsync(host, port, t), timeoutMs, ct);
            return new SmtpConnection(tcp, timeoutMs);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task<SmtpReply> ReadReplyAsync(CancellationToken ct) =>
        WithTimeoutAsync(ReadReplyCoreAsync, _timeoutMs, ct);

    public Task<SmtpReply> SendAsync(string command, CancellationToken ct) =>
        WithTimeoutAsync(async t =>
        {
            await _writer.WriteLineAsync(command.AsMemory(), t);
            return await ReadReplyCoreAsync(t);
        }, _timeoutMs, ct);

    private async Task<SmtpReply> ReadReplyCoreAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct)
                ?? throw new IOException("Connection closed by remote server");

            sb.AppendLine(line);

            // Multi-line replies use "250-..." on every line but the last, which uses "250 ...".
            if (line.Length < 4 || line[3] == ' ')
                break;
        }

        var text = sb.ToString().TrimEnd();
        return new SmtpReply(ParseCode(text), text);
    }

    internal static int ParseCode(string text) =>
        text.Length >= 3 && int.TryParse(text.AsSpan(0, 3), out var code) ? code : 0;

    private static async Task WithTimeoutAsync(Func<CancellationToken, ValueTask> action, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("SMTP operation timed out");
        }
    }

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("SMTP operation timed out");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _writer.DisposeAsync();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
