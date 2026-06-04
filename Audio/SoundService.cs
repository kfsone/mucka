using Mucka.Core;

namespace Mucka.Audio;

/// <summary>
/// Fire-and-forget platform-native sound effect player.
/// Asset names are app-package-relative paths, e.g. "sounds/clio.1311.wav".
/// Called from the TCP background thread — Play() is non-blocking.
/// </summary>
internal static class SoundService
{
    // Linear playback volume, 0-100. Set from the profile at session start and live
    // from the settings dialog; applied to each player as it is created.
    private static volatile int s_volumePercent = 75;

    // Per-sound enablement and group fallbacks. Replaced wholesale (reference write is
    // atomic) at session start and on settings apply; read on the TCP thread.
    private static volatile SoundSettings s_settings = new();

    /// <summary>Sets the playback volume (0–100) for subsequently played sounds.</summary>
    public static void SetVolume(int percent) => s_volumePercent = Math.Clamp(percent, 0, 100);

    /// <summary>Replaces the sound enablement settings used by <see cref="PlayServerSound"/>.</summary>
    public static void SetSoundSettings(SoundSettings settings) => s_settings = settings;

    /// <summary>True when the master sounds switch is on (gates the bell too).</summary>
    public static bool MasterEnabled => s_settings.MasterEnabled;

    /// <summary>
    /// Plays a server-triggered clio sound, honouring the per-sound settings: master and
    /// group switches gate playback, individual sounds can be off, and a code with no
    /// shipped wav plays its group's chosen fallback sound instead (if any).
    /// </summary>
    public static void PlayServerSound(string assetName)
    {
        var settings = s_settings;
        if (!settings.MasterEnabled) return;

        var code = ExtractClioCode(assetName);
        var group = code is null ? null : SoundCatalog.FindGroupForCode(code);
        if (code is null || group is null)
        {
            Play(assetName); // not a catalogued clio sound — play as-is
            return;
        }
        if (!settings.IsGroupEnabled(group.Prefix)) return;
        // Volume inheritance: the sound's own override, else the group's, else master.
        var groupVolume = settings.GetGroupVolume(group.Prefix);
        if (group.Contains(code))
        {
            if (settings.IsSoundEnabled(code))
                Play(assetName, settings.GetSoundVolume(code) ?? groupVolume);
            return;
        }
        // No wav shipped for this code — play the group's fallback choice, if set,
        // at the fallback sound's own (inherited) volume.
        if (settings.GetGroupDefault(group.Prefix) is { Length: > 0 } fallback)
            Play($"sounds/clio.{fallback}.wav", settings.GetSoundVolume(fallback) ?? groupVolume);
    }

    /// <summary>Plays the terminal bell at the bell row's volume (master when not
    /// overridden). The caller (GameViewModel) owns the mute/master/rate-limit gating.</summary>
    public static void PlayBell()
        => Play("beep.wav", s_settings.GetGroupVolume(SoundSettings.BellGroup));

    /// <summary>"sounds/clio.0703.wav" → "0703"; null when the name isn't that shape.</summary>
    private static string? ExtractClioCode(string assetName)
    {
        const string prefix = "sounds/clio.";
        const string suffix = ".wav";
        if (!assetName.StartsWith(prefix, StringComparison.Ordinal) ||
            !assetName.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var code = assetName[prefix.Length..^suffix.Length];
        return code.Length > 0 ? code : null;
    }

    /// <summary>Plays a sound at <paramref name="volumePercent"/> (0–100 absolute), or
    /// at the master volume when null (the inherited default).</summary>
    public static void Play(string assetName, int? volumePercent = null)
    {
        // Fire-and-forget; never block or throw on the caller (TCP) thread.
        _ = PlaySafeAsync(assetName, Math.Clamp(volumePercent ?? s_volumePercent, 0, 100));
    }

    private static async Task PlaySafeAsync(string assetName, int volumePercent)
    {
        try { await PlayCoreAsync(assetName, volumePercent).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SoundService] Play failed for '{assetName}': {ex.Message}"); }
    }

#if WINDOWS
    private static Task PlayCoreAsync(string assetName, int volumePercent)
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, assetName.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return Task.CompletedTask;

        var player = new Windows.Media.Playback.MediaPlayer();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaEnded += (s, e) => { player.Dispose(); tcs.TrySetResult(); };
        player.MediaFailed += (s, e) => { player.Dispose(); tcs.TrySetResult(); };
        player.Volume = volumePercent / 100.0;
        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
        player.Play();
        return tcs.Task;
    }

#elif ANDROID
    private static Task PlayCoreAsync(string assetName, int volumePercent)
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
            var gain = volumePercent / 100f;
            player.SetVolume(gain, gain);
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
    private static Task PlayCoreAsync(string assetName, int volumePercent) => Task.CompletedTask;
#endif
}
