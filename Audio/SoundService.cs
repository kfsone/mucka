using Mucka.Core;
using Mucka.Sounds;

namespace Mucka.Audio;

/// <summary>
/// Fire-and-forget platform-native sound effect player.
/// Asset names are app-package-relative paths, e.g. "sounds/clio.1311.wav".
/// Called from the TCP background thread - Play() is non-blocking.
/// </summary>
internal static class SoundService
{
    // Linear playback volume, 0-100. Set from the profile at session start and live
    // from the settings dialog; applied to each player as it is created.
    private static volatile int s_volumePercent = 75;

    // Per-sound enablement and group fallbacks. Replaced wholesale (reference write is
    // atomic) at session start and on settings apply; read on the TCP thread.
    private static volatile SoundSettings s_settings = new();

    // Tell alerts (tell.wav / tell-*.wav) should never overlap; while one is in flight,
    // subsequent tell alerts are dropped.
    private static int s_tellPlaybackActive;

    // Cache of discovered mucka overrides for clio assets: sounds/clio.XXXX.wav -> sounds/mucka.XXXX.wav.
    private static readonly object s_overrideLock = new();
    private static readonly HashSet<string> s_checkedMuckaOverrides = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_existingMuckaOverrides = new(StringComparer.Ordinal);

    /// <summary>Sets the playback volume (0-100) for subsequently played sounds.</summary>
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

        // Catalogued by exact asset (a clio.*.wav or a prefixed family like the tell alerts):
        // gate by group + sound, play at the resolved sound -> group -> master volume.
        var hit = SoundCatalog.FindByAsset(assetName);
        if (hit is not null)
        {
            var (group, def) = hit.Value;
            if (!settings.IsGroupEnabled(group.Prefix)) return;
            if (!settings.IsSoundEnabled(def.Code)) return;
            Play(def.AssetName, settings.GetSoundVolume(def.Code) ?? settings.GetGroupVolume(group.Prefix));
            return;
        }

        // Not shipped by name - maybe a clio code within a known group that has no wav of its
        // own; play that group's chosen fallback (if any), else it's uncatalogued -> play as-is.
        var code = ExtractClioCode(assetName);
        var grp  = code is null ? null : SoundCatalog.FindGroupForCode(code);
        if (code is null || grp is null)
        {
            Play(assetName);
            return;
        }
        if (!settings.IsGroupEnabled(grp.Prefix)) return;
        if (settings.GetGroupDefault(grp.Prefix) is { Length: > 0 } fallback)
            Play($"sounds/clio.{fallback}.wav", settings.GetSoundVolume(fallback) ?? settings.GetGroupVolume(grp.Prefix));
    }

    /// <summary>Plays the terminal bell at the bell row's volume (master when not
    /// overridden). The caller (GameViewModel) owns the mute/master/rate-limit gating.</summary>
    public static void PlayBell()
        => Play("beep.wav", s_settings.GetGroupVolume(SoundSettings.BellGroup));

    /// <summary>"sounds/clio.0703.wav" -> "0703"; null when the name isn't that shape.</summary>
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

    /// <summary>
    /// Plays a sound kept permanently open, for cases where WHEN it sounds matters.
    ///
    /// <para><see cref="Play"/> builds a fresh <c>MediaSource</c> and assigns <c>player.Source</c> on
    /// every call, which makes WinRT open and buffer the file each time. The player pool avoids the
    /// engine-init cost but not that per-play open, and its latency is both significant and VARIABLE -
    /// fine for an event sound nobody is timing, useless for the combat metronome, whose entire job is
    /// to sound a fixed distance either side of a tick boundary. Symptom when it was used: both clicks audibly trailing
    /// the tick bar, and the offset differing from session to session, so one login sounded right and
    /// the next did not.</para>
    ///
    /// <para>Here the source is assigned ONCE per asset and replayed by seeking back to zero, so the
    /// only remaining latency is the audio path itself - small, and far more consistent. Each asset
    /// holds its own player for the life of the process.</para>
    ///
    /// <para>Concurrency: safe to call from any thread. A single asset cannot overlap itself - seeking
    /// to zero restarts it - which is what you want for a click.</para>
    /// </summary>
    public static void PlayPrepared(string assetName, int? volumePercent = null)
    {
#if WINDOWS
        var player = GetPreparedPlayer(assetName);
        if (player is null)
            return;
        player.Volume = Math.Clamp(volumePercent ?? s_volumePercent, 0, 100) / 100.0;
        try
        {
            // Position is only settable once the session knows the
            // duration; if it is not ready yet, Play() from wherever it sits still sounds, and the
            // next click will be exact.
            if (player.PlaybackSession.CanSeek)
                player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SoundService] PlayPrepared failed for '{assetName}': {ex.Message}");
        }
#elif MACCATALYST
        // Never opens a file on the caller's thread: an asset that is not ready yet is opened on the
        // thread pool and this click is skipped.
        if (!TryGetReadyMacPlayer(assetName, out var player))
        {
            PrepareSound(assetName);
            return;
        }
        if (player is null)
        {
            Play(assetName, volumePercent);
            return;
        }
        try
        {
            lock (s_preparedMac)
            {
                player.Volume = Math.Clamp(volumePercent ?? s_volumePercent, 0, 100) / 100f;
                player.CurrentTime = 0;
                player.Play();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Warn("SoundService", $"PlayPrepared failed for '{assetName}': {ex}");
        }
#else
        Play(assetName, volumePercent);
#endif
    }

#if MACCATALYST
    // Null value: the file is missing or cannot be opened; cached so it is not retried on every click.
    private static readonly Dictionary<string, AVFoundation.AVAudioPlayer?> s_preparedMac = new();

    /// <summary>True when <paramref name="assetName"/> has been prepared; the player is null when its
    /// file is missing or cannot be opened. Takes the lock only for the dictionary lookup.</summary>
    private static bool TryGetReadyMacPlayer(string assetName, out AVFoundation.AVAudioPlayer? player)
    {
        lock (s_preparedMac)
            return s_preparedMac.TryGetValue(assetName, out player);
    }

    /// <summary>Opens and primes the kept-open player for <paramref name="assetName"/>. Does file I/O,
    /// so it runs on the thread pool; the lock is held only to read and write the dictionary.</summary>
    private static void GetPreparedMacPlayer(string assetName)
    {
        if (TryGetReadyMacPlayer(assetName, out _))
            return;
        AVFoundation.AVAudioPlayer? player = null;
        try
        {
            var path = AssetPath(ResolveAssetName(assetName));
            if (File.Exists(path))
            {
                player = AVFoundation.AVAudioPlayer.FromUrl(Foundation.NSUrl.FromFilename(path), out var error);
                if (player is null)
                    CrashLog.Warn("SoundService", $"AVAudioPlayer failed for {path}: {error?.LocalizedDescription}");
                else
                    player.PrepareToPlay();
            }
            else
                CrashLog.Warn("SoundService", $"missing asset {path}");
        }
        catch (Exception ex)
        {
            player = null;
            CrashLog.Warn("SoundService", $"Prepare failed for '{assetName}': {ex}");
        }
        lock (s_preparedMac)
        {
            if (s_preparedMac.ContainsKey(assetName))
            {
                player?.Dispose();
                return;
            }
            s_preparedMac[assetName] = player;
        }
    }
#endif

    /// <summary>
    /// Where an asset's audible content sits inside it - see <see cref="Mucka.Sounds.ClipSpan"/>. Null if
    /// the file is missing or is not a PCM WAV.
    ///
    /// <para>The parsing lives in <see cref="Mucka.Sounds.WavProbe"/> rather than here: this class is
    /// Windows-only and full of WinRT, and the metronome's bracket timing now depends on that
    /// arithmetic, so it belongs somewhere the test project can link and exercise it. All this method
    /// adds is asset-name resolution, which is this class's business.</para>
    ///
    /// <para>File I/O - call it once, off the UI thread. See CombatMetronome.SetEnabled.</para>
    /// </summary>
    public static Mucka.Sounds.ClipSpan? WavClipSpan(string assetName)
        => Mucka.Sounds.WavProbe.Read(AssetPath(ResolveAssetName(assetName)));

    // Where MauiAssets land: beside the executable on Windows, in the bundle's Resources on a Mac.
    private static string AssetPath(string assetName)
    {
#if MACCATALYST
        var root = Foundation.NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory;
#else
        var root = AppContext.BaseDirectory;
#endif
        return Path.GetFullPath(Path.Combine(root, assetName.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Opens a sound for <see cref="PlayPrepared"/> ahead of time, so the first click of a
    /// fight does not pay the open cost. Idempotent and a no-op except on Windows and Mac. <c>GetPreparedPlayer</c>
    /// (Windows-only, so not a resolvable cref from a cross-platform doc comment) does a synchronous
    /// <c>File.Exists</c> plus WinRT <c>MediaPlayer</c>/<c>MediaSource</c> construction, so it is
    /// dispatched to the thread pool here rather than run on whatever thread calls this; callers
    /// include page setup on the UI thread.</summary>
    public static void PrepareSound(string assetName)
    {
#if WINDOWS
        _ = Task.Run(() => GetPreparedPlayer(assetName));
#elif MACCATALYST
        _ = Task.Run(() => GetPreparedMacPlayer(assetName)).ContinueWith(
            t => CrashLog.Warn("SoundService", $"Prepare failed for '{assetName}': {t.Exception!.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);
#endif
    }

    /// <summary>Plays a sound at <paramref name="volumePercent"/> (0-100 absolute), or
    /// at the master volume when null (the inherited default).</summary>
    public static void Play(string assetName, int? volumePercent = null)
    {
        var resolvedAsset = ResolveAssetName(assetName);
        var nonOverlappingTell = IsTellAsset(resolvedAsset);
        // Fire-and-forget; never block or throw on the caller (TCP) thread.
        _ = PlaySafeAsync(resolvedAsset, Math.Clamp(volumePercent ?? s_volumePercent, 0, 100), nonOverlappingTell);
    }

    private static async Task PlaySafeAsync(string assetName, int volumePercent, bool nonOverlappingTell)
    {
        if (nonOverlappingTell && Interlocked.Exchange(ref s_tellPlaybackActive, 1) == 1)
            return;

        try { await PlayCoreAsync(assetName, volumePercent).ConfigureAwait(false); }
        catch (Exception ex)
        {
            CrashLog.Warn("SoundService", $"Play failed for '{assetName}': {ex}");
        }
        finally
        {
            if (nonOverlappingTell)
                Volatile.Write(ref s_tellPlaybackActive, 0);
        }
    }

    private static bool IsTellAsset(string assetName)
        => assetName.StartsWith("sounds/tell", StringComparison.Ordinal)
        && assetName.EndsWith(".wav", StringComparison.Ordinal);

    // Prefer app-local overrides when present: sounds/clio.1307.wav -> sounds/mucka.1307.wav.
    // This keeps the protocol/catalog code-space stable (clio IDs) while allowing bespoke assets.
    private static string ResolveAssetName(string assetName)
    {
        const string clioPrefix = "sounds/clio.";
        const string suffix = ".wav";
        if (!assetName.StartsWith(clioPrefix, StringComparison.Ordinal)
            || !assetName.EndsWith(suffix, StringComparison.Ordinal))
            return assetName;

        var code = assetName[clioPrefix.Length..^suffix.Length];
        if (code.Length == 0) return assetName;

        var overrideAsset = $"sounds/mucka.{code}.wav";
        lock (s_overrideLock)
        {
            if (s_existingMuckaOverrides.Contains(overrideAsset))
                return overrideAsset;
            if (s_checkedMuckaOverrides.Contains(overrideAsset))
                return assetName;
        }

        var exists = AssetExists(overrideAsset);
        lock (s_overrideLock)
        {
            s_checkedMuckaOverrides.Add(overrideAsset);
            if (exists) s_existingMuckaOverrides.Add(overrideAsset);
        }
        return exists ? overrideAsset : assetName;
    }

    private static bool AssetExists(string assetName)
    {
#if WINDOWS || MACCATALYST
        return File.Exists(AssetPath(assetName));
#elif ANDROID
        try
        {
            using var stream = Android.App.Application.Context.Assets?.Open(assetName);
            return stream != null;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

#if WINDOWS
    private static Task PlayCoreAsync(string assetName, int volumePercent)
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, assetName.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) return Task.CompletedTask;

        var player = RentPlayer();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish()
        {
            player.MediaEnded -= OnEnded;
            player.MediaFailed -= OnFailed;
            ReturnPlayer(player);
            tcs.TrySetResult();
        }
        void OnEnded(Windows.Media.Playback.MediaPlayer s, object e) => Finish();
        void OnFailed(Windows.Media.Playback.MediaPlayer s, Windows.Media.Playback.MediaPlayerFailedEventArgs e) => Finish();

        player.MediaEnded += OnEnded;
        player.MediaFailed += OnFailed;
        player.Volume = volumePercent / 100.0;
        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
        player.Play();
        return tcs.Task;
    }

    // Reusing MediaPlayer instances avoids the per-play engine-init cost of spinning up a fresh
    // WinRT media pipeline/audio session for every effect -- that cold-start latency (can be
    // 1+ second) is barely noticeable for a single occasional sound, but stacks up badly in
    // combat where several distinct clio sound codes fire in quick succession. Instead we keep a
    // small pool of already-initialised players and hand out/return them around each play; only
    // the very first few plays (or a burst deep enough to exhaust the pool) pay the cold-start cost.
    private const int MaxPooledPlayers = 8;
    private static readonly object s_poolLock = new();
    private static readonly Stack<Windows.Media.Playback.MediaPlayer> s_playerPool = new();

    // Dedicated, permanently-sourced players for PlayPrepared - see its remarks. Keyed by asset, never
    // returned to the pool, deliberately never disposed: there are two of them (the metronome clicks)
    // and they must stay open for the life of the process, because re-opening is the very cost this
    // exists to avoid.
    private static readonly Dictionary<string, Windows.Media.Playback.MediaPlayer?> s_prepared =
        new(StringComparer.Ordinal);

    private static Windows.Media.Playback.MediaPlayer? GetPreparedPlayer(string assetName)
    {
        lock (s_poolLock)
        {
            if (s_prepared.TryGetValue(assetName, out var existing))
                return existing;
        }

        Windows.Media.Playback.MediaPlayer? created = null;
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, ResolveAssetName(assetName).Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(path))
        {
            try
            {
                created = new Windows.Media.Playback.MediaPlayer { AutoPlay = false };
                created.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SoundService] Prepare failed for '{assetName}': {ex.Message}");
                created = null;
            }
        }

        lock (s_poolLock)
        {
            // A concurrent caller may have won; keep theirs so only one player per asset ever exists.
            if (s_prepared.TryGetValue(assetName, out var raced))
            {
                created?.Dispose();
                return raced;
            }
            // Cached even when null, so a missing or unopenable file is not retried on every click.
            s_prepared[assetName] = created;
            return created;
        }
    }


    private static Windows.Media.Playback.MediaPlayer RentPlayer()
    {
        lock (s_poolLock)
        {
            if (s_playerPool.Count > 0)
                return s_playerPool.Pop();
        }
        return new Windows.Media.Playback.MediaPlayer();
    }

    private static void ReturnPlayer(Windows.Media.Playback.MediaPlayer player)
    {
        lock (s_poolLock)
        {
            if (s_playerPool.Count < MaxPooledPlayers)
            {
                s_playerPool.Push(player);
                return;
            }
        }
        player.Dispose();
    }

    /// <summary>Pre-creates a few pooled players up front so the first sounds of a session
    /// (e.g. early combat right after logging in) don't each pay the cold-start engine-init cost.
    /// Safe to call multiple times; a no-op once the pool is already warm.</summary>
    public static void WarmUp(int count = 3)
    {
        lock (s_poolLock)
        {
            count = Math.Min(count, MaxPooledPlayers) - s_playerPool.Count;
        }
        for (var i = 0; i < count; i++)
            ReturnPlayer(new Windows.Media.Playback.MediaPlayer());
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

#elif MACCATALYST
    // AVAudioPlayer is a native object whose managed wrapper must outlive the call: held here until
    // playback finishes.
    private static readonly HashSet<AVFoundation.AVAudioPlayer> s_activePlayers = new();

    private static Task PlayCoreAsync(string assetName, int volumePercent)
    {
        var path = AssetPath(assetName);
        if (!File.Exists(path))
        {
            CrashLog.Warn("SoundService", $"missing asset {path}");
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = AVFoundation.AVAudioPlayer.FromUrl(Foundation.NSUrl.FromFilename(path), out var error);
        if (player is null)
        {
            CrashLog.Warn("SoundService", $"AVAudioPlayer failed for {path}: {error?.LocalizedDescription}");
            return Task.CompletedTask;
        }

        var finished = 0;
        void Finish()
        {
            if (Interlocked.Exchange(ref finished, 1) == 1)
                return;
            lock (s_activePlayers) s_activePlayers.Remove(player);
            player.Dispose();
            tcs.TrySetResult();
        }
        lock (s_activePlayers) s_activePlayers.Add(player);
        player.Volume = volumePercent / 100f;
        player.FinishedPlaying += (_, _) => Finish();
        // Backstop: a player that never reports finishing would otherwise hold the caller - and, for a
        // tell alert, the flag that stops overlapping tells - for good.
        _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(1.0, player.Duration) + 2.0)).ContinueWith(_ =>
        {
            try { Finish(); }
            catch (Exception ex) { CrashLog.Warn("SoundService", $"Finish failed for {path}: {ex}"); }
        });
        if (!player.Play())
        {
            CrashLog.Warn("SoundService", $"Play() returned false for {path}");
            Finish();
        }
        return tcs.Task;
    }
#else
    private static Task PlayCoreAsync(string assetName, int volumePercent) => Task.CompletedTask;
#endif

#if !WINDOWS
    /// <summary>No-op on platforms without a pooled-player implementation.</summary>
    public static void WarmUp(int count = 3) { }
#endif
}
