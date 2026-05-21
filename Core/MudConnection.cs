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
    private readonly SessionCapture _capture = new();
    private string _host = string.Empty;

    public MudStream Stream => _parser;
    public bool IsConnected => _client?.Connected ?? false;
    public bool IsCapturing => _capture.IsRecording;
    public string? CaptureFilePath => _capture.FilePath;

    public event Action? Disconnected;
    public event Action<string>? ConnectionError;

    public async Task ConnectAsync(string host, int port)
    {
        _host = host;
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port);
        _stream = _client.GetStream();

        _parser.Capture = _capture;
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

    public void StartCapture() => _capture.Start(_host);
    public void StopCapture() => _capture.Stop();

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
                _capture.RecordTx(bytes);
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

                _capture.RecordRx(buf.AsSpan(0, n));
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
        _capture.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
