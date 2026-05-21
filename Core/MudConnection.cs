using System.Net.Sockets;
using System.Text;

namespace Mucka.Core;

public sealed class MudConnection : IAsyncDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly MudStream _parser = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
#if DEBUG
    private readonly SessionCapture _capture = new();
#endif
    private string _host = string.Empty;

    public MudStream Stream => _parser;
    public bool IsConnected => _client?.Connected ?? false;
#if DEBUG
    public bool IsCapturing => _capture.IsRecording;
    public string? CaptureFilePath => _capture.FilePath;
#else
    public bool IsCapturing => false;
    public string? CaptureFilePath => null;
#endif

    public event Action? Disconnected;
    public event Action<string>? ConnectionError;

    public async Task ConnectAsync(string host, int port)
    {
        _host = host;
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port);
        _stream = _client.GetStream();

    #if DEBUG
        _parser.Capture = _capture;
    #endif
        _parser.ResponseReady += async bytes =>
        {
            try
            {
                await WriteAsync(bytes);
            }
            catch
            {
            }
        };

        _cts = new CancellationTokenSource();
        _ = ReadLoopAsync(_cts.Token);
    }

    public bool TryStartCapture(string? hostOverride, out string? error)
    {
#if DEBUG
        var host = string.IsNullOrWhiteSpace(hostOverride) ? _host : hostOverride.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "unknown";
        return _capture.TryStart(host, out error);
#else
        error = "Capture is only available in debug builds.";
        return false;
#endif
    }

    public void StopCapture()
    {
#if DEBUG
        _capture.Stop();
#endif
    }

    public async Task SendAsync(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        await WriteAsync(bytes);
    }

    private async Task WriteAsync(byte[] bytes)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_stream != null)
            {
                await _stream.WriteAsync(bytes);
#if DEBUG
                _capture.RecordTx(bytes);
#endif
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream!.ReadAsync(buf, ct);
                if (n == 0)
                {
                    break;
                }

#if DEBUG
                _capture.RecordRx(buf.AsSpan(0, n));
#endif
                _parser.Feed(buf.AsSpan(0, n));
                _parser.EmitPartial();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(ex.Message);
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
#if DEBUG
        _capture.Dispose();
#endif
        _cts?.Cancel();
        _cts?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
