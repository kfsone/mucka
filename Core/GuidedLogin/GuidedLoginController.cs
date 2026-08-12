using MudSharp.Models;

namespace Mucka.Core.GuidedLogin;

/// <summary>Phases the guided-login state machine passes through, for UI status text.</summary>
public enum GuidedLoginPhase
{
    Connecting,
    NegotiatingShell,
    QueryingPersonae,
    AwaitingPersonaChoice,
    AwaitingCreateConfirmation,
    AwaitingSexChoice,
    SelectingPersona,
    WaitingForGameMode,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Existing personae plus whether a free slot is available, offered to the UI when
/// the profile has no configured persona name (or picking is otherwise required).</summary>
public sealed record PersonaChoice(IReadOnlyList<PersonaSlot> Slots, bool CanCreateNew);

/// <summary>Options for a guided-login pass. The initial connect uses the default mode; relogging
/// from the shell menu can start by re-prompting the menu and can either force a fresh picker or
/// prefer one persona for fast relog.</summary>
public sealed record GuidedLoginOptions(
    string? PreferredPersonaName = null,
    bool StartAtOptionMenu = false,
    bool ForcePersonaChoice = false,
    bool AllowCreatePreferredPersona = true,
    TimeSpan? PlayRetryWindow = null);

public enum GuidedLoginOutcome { Succeeded, Failed, Cancelled, ManualAtOptionMenu }

public sealed record GuidedLoginResult(GuidedLoginOutcome Outcome, string? FailureReason = null);

/// <summary>
/// Drives the MUD Shell (Option menu -&gt; persona select/create -&gt; tearoom) on behalf
/// of a profile with Guided Login enabled, so the player can skip the manual shell dance.
/// Runs entirely off <see cref="MuckaConnection"/>'s line/game-mode events; <see cref="MudLoginHandler"/>
/// (already wired into the connection) still owns the pre-shell login/password/client-mode steps.
/// </summary>
public sealed class GuidedLoginController : IDisposable
{
    private static readonly TimeSpan LandmarkTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DbRetryWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DbRetryInterval = TimeSpan.FromSeconds(2);
    /// <summary>How many times to answer a persona-name prompt. Two, because the thing that eats an
    /// answer is a single unterminated FES probe left in the shell's input buffer; a name the shell
    /// genuinely will not take is refused just as firmly the second time, so there is no point
    /// spraying it. Guards against a rejection loop either way.</summary>
    private const int NamePromptAttempts = 2;

    private readonly MuckaConnection _conn;
    private readonly GuidedLoginOptions _options;
    private readonly string? _preferredPersonaName;
    private readonly TimeSpan _playRetryWindow;

    private readonly List<StyledLine> _buffer = new();
    private readonly object _bufferLock = new();
    private TaskCompletionSource? _lineSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _gameModeEntered;
    private Exception? _disconnectError;
    private bool _disconnected;
    private int _dropToMenuRequested;

    private TaskCompletionSource<string?>? _personaDecision;   // resolved by SelectExistingPersona/RequestCreateNew
    private TaskCompletionSource<char?>? _sexDecision;         // resolved by ConfirmCreateSex/CancelCreate ('m'/'f'/null=cancel)

    public event Action<GuidedLoginPhase>? PhaseChanged;
    /// <summary>The real login splash/banner (ASCII logo etc), styled, for rendering in a
    /// terminal-like preview -- see <see cref="ShellText.ExtractSplashRange"/> for exactly what's
    /// included/excluded.</summary>
    public event Action<IReadOnlyList<StyledLine>>? SplashTextReady;
    /// <summary>Raised when the profile has no configured persona (or it wasn't found and there's no
    /// free slot to offer a create-confirmation instead). The consumer must call
    /// <see cref="SelectExistingPersona"/> or <see cref="RequestCreateNew"/>.</summary>
    public event Action<PersonaChoice>? PersonaChoiceReady;
    /// <summary>Raised when the configured persona name wasn't found but a slot is free. The consumer
    /// must call <see cref="ConfirmCreateSex"/> or <see cref="CancelCreate"/>.</summary>
    public event Action<string>? CreateConfirmationReady;
    public event Action<string>? Failed;
    public event Action? Completed;

    public GuidedLoginController(MuckaConnection conn, string? configuredPersonaName)
        : this(conn, new GuidedLoginOptions(PreferredPersonaName: configuredPersonaName))
    {
    }

    public GuidedLoginController(MuckaConnection conn, GuidedLoginOptions options)
    {
        _conn = conn;
        _options = options;
        _preferredPersonaName = string.IsNullOrWhiteSpace(options.PreferredPersonaName) ? null : options.PreferredPersonaName.Trim();
        _playRetryWindow = options.PlayRetryWindow ?? DbRetryWindow;

        _conn.LineReady += OnLineReady;
        _conn.GameModeEntered += OnGameModeEntered;
        _conn.Disconnected += OnDisconnected;
    }

    public void Dispose()
    {
        _conn.LineReady -= OnLineReady;
        _conn.GameModeEntered -= OnGameModeEntered;
        _conn.Disconnected -= OnDisconnected;
    }

    /// <summary>Runs the whole guided-login sequence to completion (success, failure, or cancellation).
    /// Safe to call once per connection attempt.</summary>
    public async Task<GuidedLoginResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            SetPhase(GuidedLoginPhase.Connecting);

            if (_options.StartAtOptionMenu)
            {
                // The shell is already at the Option menu -- that prompt is HOW game-mode exit gets
                // detected -- so there is nothing to wait for. But it may be holding a partial line:
                // an FES probe carries no trailing CR, so one that was in flight when the server
                // dropped us sits in the shell's input buffer and gets spliced onto the front of
                // whatever we send next ("<probe>p" -> "Option unavailable.", and no persona list
                // ever comes). A bare CR flushes it. The shell answers that with one
                // "Option unavailable." of its own, which is expected noise, not an error -- and is
                // deliberately not a landmark anywhere below.
                ResetBuffer();
                _conn.SendLine(string.Empty);
            }
            else
            {
                // Some servers (mud2.com) show a "Skip the rest? (y/n)" MOTD prompt before the login
                // splash/banner; others (mud2.co.uk) go straight to the banner. Answer "y" if asked,
                // then capture everything up to the FIRST "Option" prompt as the splash/banner for the
                // tiny-font preview. MudLoginHandler already sent login/account/password and the
                // client-mode entry on that first "Option" prompt; it reappears a second time once the
                // server has echoed back the confirmed terminal width -- that second occurrence is when
                // the shell is actually idle and ready for our own commands.
                var sawFirstOption = await NegotiateBannerAsync(ct).ConfigureAwait(false);
                if (!sawFirstOption)
                    return Fail("Timed out waiting for the MUD Shell's Option menu.");
                ResetBuffer();

                SetPhase(GuidedLoginPhase.NegotiatingShell);
                var sawSecondOption = await WaitForLandmarkAsync(ShellText.IsShellOptionPrompt, LandmarkTimeout, ct)
                    .ConfigureAwait(false);
                if (!sawSecondOption)
                    return Fail("Timed out waiting for the MUD Shell to finish negotiating the terminal.");
                ResetBuffer();
            }

            // Query personae directly from the Play prompt. That list already includes the live
            // occupied names plus any "**Unused**" slots, so it is both the authoritative source
            // of selectable names and the free-slot check.
            SetPhase(GuidedLoginPhase.QueryingPersonae);
            var slots = await SendPlayAndGetSlotsAsync(ct).ConfigureAwait(false);
            if (slots is null)
                return Fail("Timed out waiting for the persona slot list.");
            return await ResolvePersonaPromptAsync(slots, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancel();
        }
    }

    /// <summary>Call from the picker UI when the player chooses an existing persona.</summary>
    public void SelectExistingPersona(string name) => _personaDecision?.TrySetResult(name);

    /// <summary>Call from the picker UI when the player chooses "+ Create new" with the given name.</summary>
    public void RequestCreateNew(string name) => _personaDecision?.TrySetResult(name);

    /// <summary>Cancel the persona picker (drop back to manual mode).</summary>
    public void CancelPersonaChoice() => _personaDecision?.TrySetResult(null);

    /// <summary>Drop back to the shell's Option menu and leave the connection in manual mode.</summary>
    public void DropToMenu()
    {
        Interlocked.Exchange(ref _dropToMenuRequested, 1);
        _personaDecision?.TrySetResult(null);
        _sexDecision?.TrySetResult(null);
    }

    /// <summary>Call from the create-confirmation UI with 'm' or 'f'.</summary>
    public void ConfirmCreateSex(char sex) => _sexDecision?.TrySetResult(char.ToLowerInvariant(sex));

    /// <summary>Call from the create-confirmation UI when the player cancels.</summary>
    public void CancelCreate() => _sexDecision?.TrySetResult(null);

    private GuidedLoginResult Cancel()
    {
        SetPhase(GuidedLoginPhase.Cancelled);
        return new GuidedLoginResult(GuidedLoginOutcome.Cancelled);
    }

    private GuidedLoginResult ManualAtOptionMenu()
    {
        SetPhase(GuidedLoginPhase.Cancelled);
        return new GuidedLoginResult(GuidedLoginOutcome.ManualAtOptionMenu);
    }

    private GuidedLoginResult Fail(string reason)
    {
        SetPhase(GuidedLoginPhase.Failed);
        Failed?.Invoke(reason);
        return new GuidedLoginResult(GuidedLoginOutcome.Failed, reason);
    }

    /// <summary>Sends "q" at the "By what name...?" prompt to back out to the Option menu without
    /// selecting/creating anything, best-effort (a failed connection makes this a no-op).
    /// Only ever call this while the persona-name prompt is up: "q" AT the Option menu means QUIT,
    /// and would log the player out.</summary>
    private void AbandonPersonaPrompt()
    {
        if (!_disconnected)
            _conn.SendLine("q");
    }

    private bool ConsumeDropToMenuRequest() => Interlocked.Exchange(ref _dropToMenuRequested, 0) != 0;

    /// <summary>
    /// Waits for the first "Option (H for help):" prompt, answering any "(y/n)" confirmation prompt
    /// with "y" if the server shows one before then -- mud2.com may ask to skip the rest of the MOTD,
    /// or to usurp an existing session under the same account; mud2.co.uk typically asks neither.
    /// Fires <see cref="SplashTextReady"/> with the real login splash/banner (ASCII logo etc), as
    /// extracted by <see cref="ShellText.ExtractSplashRange"/> from the whole buffer accumulated
    /// since connecting -- see that method for exactly what's included/excluded.
    /// </summary>
    private async Task<bool> NegotiateBannerAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LandmarkTimeout;
        var promptAnswered = false;
        while (true)
        {
            // Grab the signal BEFORE inspecting the buffer: if a line arrives between the
            // snapshot below and the await, it resolves *this* TCS (OnLineReady swaps in a new
            // one and resolves the one we already hold), so the await returns immediately and we
            // re-check. Grabbing the signal *after* the predicate check would race -- a line
            // landing in that gap would resolve a TCS we no longer hold, and we'd wait on a fresh
            // one that only completes on some *later* line, silently missing this one until the
            // full timeout elapses (this was the cause of the intermittent co.uk hang).
            var signal = TakeSignalTask();

            List<StyledLine> snapshot;
            lock (_bufferLock)
                snapshot = new List<StyledLine>(_buffer);
            var normalized = ShellText.NormalizeWhitespace(string.Join(" ", snapshot.Select(l => l.PlainText)));

            if (!promptAnswered && ShellText.IsYesNoPrompt(normalized))
            {
                promptAnswered = true;
                _conn.SendLine("y");
            }

            if (ShellText.IsShellOptionPrompt(normalized))
            {
                var range = ShellText.ExtractSplashRange(snapshot.Select(l => l.PlainText).ToList());
                if (range is { } r)
                    SplashTextReady?.Invoke(snapshot.Skip(r.Start).Take(r.End - r.Start).ToList());
                return true;
            }

            if (_disconnected)
                return false;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends "p" at the Option menu and returns the numbered persona slot list.
    ///
    /// <para>The Option prompt is deliberately NOT a landmark here. The shell echoes whatever we
    /// type onto the prompt line itself ("Option (H for help): p"), and a partial line is
    /// re-published every time it grows, so a freshly cleared buffer matches "option (h for help)"
    /// the instant we send anything. Treating that as the shell's reply made this loop conclude the
    /// command had been refused, discard the persona list arriving behind it, and fire another "p"
    /// into the "By what name...?" prompt -- which the shell answered with
    /// 'Sorry, I can't call you "P".'</para>
    ///
    /// <para>So "p" goes out once, and is only re-sent when the shell explicitly refuses it because
    /// a reset is rebuilding the database. Anything we do not recognise rides the outer deadline and
    /// fails cleanly instead of typing into a prompt we have not identified.</para>
    /// </summary>
    private async Task<IReadOnlyList<PersonaSlot>?> SendPlayAndGetSlotsAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _playRetryWindow + LandmarkTimeout;
        var sendPlay = true;
        while (DateTime.UtcNow < deadline)
        {
            if (_disconnected)
                return null;

            if (sendPlay)
            {
                ResetBuffer();
                _conn.SendLine("p");
                sendPlay = false;
            }

            var sawReply = await WaitForLandmarkAsync(
                    normalized => ShellText.IsPersonaNamePrompt(normalized)
                        || ShellText.IsDatabaseStillInitialisingLine(normalized)
                        || ShellText.IsDatabaseStartedInitialisingLine(normalized)
                        || ShellText.IsDatabaseFinishedInitialisingLine(normalized),
                    DbRetryInterval,
                    ct)
                .ConfigureAwait(false);
            if (!sawReply)
                continue;   // nothing we recognise yet; the outer deadline bounds the wait

            var buffer = NormalizedBufferSnapshot();
            if (ShellText.IsPersonaNamePrompt(buffer))
            {
                var slots = ShellText.TryParsePersonaSlots(buffer);
                if (slots is null)
                    AbandonPersonaPrompt();   // at the name prompt, the one place "q" backs out safely
                return slots;
            }

            // A reset is rebuilding the persona database. Ask again the moment it reports itself
            // finished; otherwise pace the retries so we do not machine-gun the shell.
            if (!ShellText.IsDatabaseFinishedInitialisingLine(buffer))
            {
                await WaitForLandmarkAsync(ShellText.IsDatabaseFinishedInitialisingLine, DbRetryInterval, ct)
                    .ConfigureAwait(false);
            }
            sendPlay = true;
        }
        return null;
    }

    private async Task<GuidedLoginResult> ResolvePersonaPromptAsync(IReadOnlyList<PersonaSlot> slots, CancellationToken ct)
    {
        var hasFreeSlot = slots.Any(s => s.IsUnused);
        var matched = _options.ForcePersonaChoice || _preferredPersonaName is null
            ? null
            : slots.FirstOrDefault(s => !s.IsUnused
                && string.Equals(s.Name, _preferredPersonaName, StringComparison.OrdinalIgnoreCase));

        if (matched?.Name is string personaToSelect)
        {
            SetPhase(GuidedLoginPhase.SelectingPersona);
            return await SelectExistingAtPromptAsync(personaToSelect, ct).ConfigureAwait(false);
        }

        if (_preferredPersonaName is not null && !_options.ForcePersonaChoice && _options.AllowCreatePreferredPersona)
        {
            if (!hasFreeSlot)
            {
                AbandonPersonaPrompt();
                return Fail($"Persona \"{_preferredPersonaName}\" was not found and there is no free slot to create it.");
            }

            SetPhase(GuidedLoginPhase.AwaitingCreateConfirmation);
            _sexDecision = new TaskCompletionSource<char?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CreateConfirmationReady?.Invoke(_preferredPersonaName);
            var sex = await _sexDecision.Task.ConfigureAwait(false);
            if (sex is null)
            {
                if (ConsumeDropToMenuRequest())
                    return await DropToMenuAsync(ct).ConfigureAwait(false);
                AbandonPersonaPrompt();
                return Cancel();
            }
            return await CreatePersonaAsync(_preferredPersonaName, sex.Value, ct).ConfigureAwait(false);
        }

        SetPhase(GuidedLoginPhase.AwaitingPersonaChoice);
        _personaDecision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PersonaChoiceReady?.Invoke(new PersonaChoice(slots.Where(s => !s.IsUnused).ToList(), hasFreeSlot));
        var choice = await _personaDecision.Task.ConfigureAwait(false);
        if (choice is null)
        {
            if (ConsumeDropToMenuRequest())
                return await DropToMenuAsync(ct).ConfigureAwait(false);
            AbandonPersonaPrompt();
            return Cancel();
        }

        var isExisting = slots.Any(s => !s.IsUnused && string.Equals(s.Name, choice, StringComparison.OrdinalIgnoreCase));
        if (isExisting)
        {
            SetPhase(GuidedLoginPhase.SelectingPersona);
            return await SelectExistingAtPromptAsync(choice, ct).ConfigureAwait(false);
        }

        if (!hasFreeSlot)
        {
            AbandonPersonaPrompt();
            return Fail("No free persona slot is available to create a new one.");
        }

        SetPhase(GuidedLoginPhase.AwaitingSexChoice);
        _sexDecision = new TaskCompletionSource<char?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CreateConfirmationReady?.Invoke(choice);
        var newSex = await _sexDecision.Task.ConfigureAwait(false);
        if (newSex is null)
        {
            if (ConsumeDropToMenuRequest())
                return await DropToMenuAsync(ct).ConfigureAwait(false);
            AbandonPersonaPrompt();
            return Cancel();
        }
        return await CreatePersonaAsync(choice, newSex.Value, ct).ConfigureAwait(false);
    }

    private async Task<GuidedLoginResult> DropToMenuAsync(CancellationToken ct)
    {
        if (_disconnected)
            return Fail(_disconnectError?.Message ?? "Disconnected while returning to the Option menu.");

        // "q" at the "By what name...?" prompt backs out to the Option menu, which prints its own
        // prompt -- so the player lands on a live prompt without us sending anything else.
        ResetBuffer();
        _conn.SendLine("q");
        var sawOption = await WaitForLandmarkAsync(ShellText.IsShellOptionPrompt, LandmarkTimeout, ct).ConfigureAwait(false);
        if (!sawOption && !await RefreshOptionMenuPromptAsync(ct).ConfigureAwait(false))
            return Fail("Timed out returning to the Option menu.");

        return ManualAtOptionMenu();
    }

    /// <summary>Last-resort nudge when "q" did not produce a prompt: a blank line makes the shell
    /// answer "Option unavailable." and reprint the Option prompt, which is at least a live prompt
    /// for the player to type at.</summary>
    private async Task<bool> RefreshOptionMenuPromptAsync(CancellationToken ct)
    {
        if (_disconnected)
            return false;

        ResetBuffer();
        _conn.SendLine(string.Empty);
        return await WaitForLandmarkAsync(ShellText.IsShellOptionPrompt, LandmarkTimeout, ct).ConfigureAwait(false);
    }

    private async Task<GuidedLoginResult> SelectExistingAtPromptAsync(string name, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= NamePromptAttempts; attempt++)
        {
            ResetBuffer();
            _conn.SendLine(name);

            // Null means the shell refused the name rather than starting the game, so our answer
            // never reached the prompt -- say it again. See NamePromptAttempts.
            var result = await WaitForGameModeAsync(ct).ConfigureAwait(false);
            if (result is not null)
                return result;
        }
        return Fail(NameRefusedReason(name));
    }

    private async Task<GuidedLoginResult> CreatePersonaAsync(string name, char sex, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= NamePromptAttempts; attempt++)
        {
            ResetBuffer();
            _conn.SendLine(name);
            var sawPrompt = await WaitForLandmarkAsync(
                    normalized => ShellText.IsSexPrompt(normalized) || ShellText.IsNameRejectedPrompt(normalized),
                    LandmarkTimeout,
                    ct)
                .ConfigureAwait(false);
            if (!sawPrompt)
                return Fail($"Timed out waiting for the new-persona sex prompt after naming \"{name}\".");

            if (!ShellText.IsSexPrompt(NormalizedBufferSnapshot()))
                continue;   // name refused -- retry it, then give up with the shell's verdict

            ResetBuffer();
            _conn.SendLine(sex.ToString());
            // A name refusal cannot follow the sex answer, so a null here is unreachable; treat it
            // as a refusal anyway rather than dereferencing it.
            return await WaitForGameModeAsync(ct).ConfigureAwait(false) ?? Fail(NameRefusedReason(name));
        }
        return Fail(NameRefusedReason(name));
    }

    private static string NameRefusedReason(string name)
        => $"The MUD Shell would not accept the persona name \"{name}\".";

    /// <summary>Waits to land in the game after answering a name prompt. Returns null if the shell
    /// re-asked for the name instead ("What shall I call you instead?"), so the caller can answer it
    /// again -- see <see cref="NamePromptAttempts"/>.</summary>
    private async Task<GuidedLoginResult?> WaitForGameModeAsync(CancellationToken ct)
    {
        SetPhase(GuidedLoginPhase.WaitingForGameMode);
        var deadline = Task.Delay(LandmarkTimeout, ct);
        while (!_gameModeEntered)
        {
            // Grab the signal BEFORE re-checking _gameModeEntered -- see the comment in
            // NegotiateBannerAsync for why the order matters (missed-wakeup race).
            var signal = TakeSignalTask();
            if (_gameModeEntered)
                break;

            if (_disconnected)
                return Fail("Disconnected while waiting to enter the game.");
            if (ShellText.IsNameRejectedPrompt(NormalizedBufferSnapshot()))
                return null;

            var completed = await Task.WhenAny(signal, deadline).ConfigureAwait(false);
            if (completed == deadline)
                return Fail("Timed out waiting to enter the game after selecting the persona.");
        }

        SetPhase(GuidedLoginPhase.Succeeded);
        Completed?.Invoke();
        return new GuidedLoginResult(GuidedLoginOutcome.Succeeded);
    }

    // ── Line buffering / landmark waiting ──────────────────────────────────────────────────────

    private void OnLineReady(StyledLine line)
    {
        lock (_bufferLock)
        {
            _buffer.Add(line);
            if (_buffer.Count > 200)
                _buffer.RemoveRange(0, _buffer.Count - 200);
        }

        Interlocked.Exchange(ref _lineSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            ?.TrySetResult();
    }

    private void OnGameModeEntered()
    {
        _gameModeEntered = true;
        Interlocked.Exchange(ref _lineSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            ?.TrySetResult();
    }

    private void OnDisconnected(Exception? error)
    {
        _disconnected = true;
        _disconnectError = error;
        // RunAsync may be parked awaiting the persona/sex picker UI (_personaDecision/_sexDecision)
        // rather than a line -- pulsing _lineSignal alone would never wake it, leaving RunAsync
        // (and the overlay's finally block that clears _isGuidedLoginOverlayOpen) stuck forever.
        // Resolve both with null so the ResolvePersonaPromptAsync "choice/sex is null" branches run
        // and unwind through AbandonPersonaPrompt()/Cancel() -- AbandonPersonaPrompt() itself is a
        // no-op once _disconnected is set, so this is safe even though nothing is listening anymore.
        _personaDecision?.TrySetResult(null);
        _sexDecision?.TrySetResult(null);
        Interlocked.Exchange(ref _lineSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            ?.TrySetResult();
    }

    private Task TakeSignalTask() => Volatile.Read(ref _lineSignal)!.Task;

    private string NormalizedBufferSnapshot()
    {
        lock (_bufferLock)
        {
            return ShellText.NormalizeWhitespace(string.Join(" ", _buffer.Select(l => l.PlainText)));
        }
    }

    private void ResetBuffer()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>Waits until <paramref name="predicate"/> matches the normalized accumulated buffer,
    /// a new line arrives, or <paramref name="timeout"/> elapses.</summary>
    private async Task<bool> WaitForLandmarkAsync(Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // Grab the signal BEFORE evaluating the predicate -- see the comment in
            // NegotiateBannerAsync for why the order matters (missed-wakeup race).
            var signal = TakeSignalTask();

            if (predicate(NormalizedBufferSnapshot()))
                return true;
            if (_disconnected)
                return false;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            var completed = await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
            if (completed != signal)
                return predicate(NormalizedBufferSnapshot());
        }
    }

    private void SetPhase(GuidedLoginPhase phase) => PhaseChanged?.Invoke(phase);
}
