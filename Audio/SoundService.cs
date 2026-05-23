namespace Mucka.Audio;

/// <summary>
/// Fire-and-forget platform-native sound effect player.
/// Asset names are app-package-relative paths, e.g. "sounds/clio.1311.wav".
/// Called from the TCP background thread — Play() is non-blocking.
/// </summary>
internal static class SoundService
{
    public static void Play(string assetName)
    {
        // Fire-and-forget; never block or throw on the caller (TCP) thread.
        _ = Task.Run(async () =>
        {
            try { await PlayCoreAsync(assetName).ConfigureAwait(false); }
            catch { /* sound failure is non-critical */ }
        });
    }

#if WINDOWS
    private static Task PlayCoreAsync(string assetName)
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, assetName.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return Task.CompletedTask;

        var player = new Windows.Media.Playback.MediaPlayer();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaEnded += (s, e) => { player.Dispose(); tcs.TrySetResult(); };
        player.MediaFailed += (s, e) => { player.Dispose(); tcs.TrySetResult(); };
        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
        player.Play();
        return tcs.Task;
    }

#elif ANDROID
    private static Task PlayCoreAsync(string assetName)
    {
        var assets = Android.App.Application.Context.Assets;
        if (assets == null) return Task.CompletedTask;

        Android.Content.Res.AssetFileDescriptor? afd = null;
        Android.Media.MediaPlayer? player = null;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            afd = assets.OpenFd(assetName);
            player = new Android.Media.MediaPlayer();
            player.SetDataSource(afd.FileDescriptor, afd.StartOffset, afd.Length);
            player.Prepare();
            afd.Close();
            afd = null;

            player.Completion += (s, e) => { player?.Release(); tcs.TrySetResult(); };
            player.Error += (s, e) => { player?.Release(); tcs.TrySetResult(); };
            player.Start();
        }
        catch
        {
            afd?.Close();
            player?.Release();
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

#else
    private static Task PlayCoreAsync(string assetName) => Task.CompletedTask;
#endif
}
