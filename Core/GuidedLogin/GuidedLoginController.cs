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
public sealed record PersonaChoice(IReadOnlyList<ExaminePersona> Existing, bool CanCreateNew);

public enum GuidedLoginOutcome { Succeeded, Failed, Cancelled }

public sealed record GuidedLoginResult(GuidedLoginOutcome Outcome, string? FailureReason = null);

/// <summary>
/// Drives the MUD Shell (Option menu -&gt; EXAMINE -&gt; persona select/create -&gt; tearoom) on behalf
/// of a profile with Guided Login enabled, so the player can skip the manual shell dance.
/// Runs entirely off <see cref="MuckaConnection"/>'s line/game-mode events; <see cref="MudLoginHandler"/>
/// (already wired into the connection) still owns the pre-shell login/password/client-mode steps.
/// </summary>
public sealed class GuidedLoginController : IDisposable
{
    private static readonly TimeSpan LandmarkTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DbRetryWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DbRetryInterval = TimeSpan.FromSeconds(2);

    private readonly MuckaConnection _conn;
    private readonly string? _configuredPersonaName;

    private readonly List<StyledLine> _buffer = new();
    private readonly object _bufferLock = new();
    private TaskCompletionSource? _lineSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _gameModeEntered;
    private Exception? _disconnectError;
    private bool _disconnected;

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
    {
        _conn = conn;
        _configuredPersonaName = string.IsNullOrWhiteSpace(configuredPersonaName) ? null : configuredPersonaName.Trim();

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

            // -- Query existing personae via EXAMINE ("E") --------------------------------------
            SetPhase(GuidedLoginPhase.QueryingPersonae);
            _conn.SendLine("e");
            var sawExamine = await WaitForLandmarkAsync(ShellText.IsExaminePrompt, LandmarkTimeout, ct)
                .ConfigureAwait(false);
            if (!sawExamine)
                return Fail("Timed out waiting for the EXAMINE persona list.");
            var existing = ShellText.ParseExaminePersonae(NormalizedBufferSnapshot());
            ResetBuffer();

            _conn.SendLine("q");
            var sawBackAtOption = await WaitForLandmarkAsync(ShellText.IsShellOptionPrompt, LandmarkTimeout, ct)
                .ConfigureAwait(false);
            if (!sawBackAtOption)
                return Fail("Timed out returning to the Option menu after EXAMINE.");
            ResetBuffer();

            // -- Decide what to do with the persona list ----------------------------------------
            var matched = _configuredPersonaName is null
                ? null
                : existing.FirstOrDefault(p => string.Equals(p.Name, _configuredPersonaName, StringComparison.OrdinalIgnoreCase));

            string? personaToSelect = matched?.Name;
            string? personaToCreate = null;

            if (personaToSelect is null)
            {
                // Send "p" now: the numbered slot list (with **Unused** markers) is the only
                // authoritative source of free-slot information -- EXAMINE never shows empty slots.
                SetPhase(GuidedLoginPhase.SelectingPersona);
                var slots = await SendPlayAndGetSlotsAsync(ct).ConfigureAwait(false);
                if (slots is null)
                    return Fail("Timed out waiting for the persona slot list.");

                var hasFreeSlot = slots.Any(s => s.IsUnused);

                if (_configuredPersonaName is not null)
                {
                    // Configured name not found among existing personae.
                    if (!hasFreeSlot)
                    {
                        AbandonPersonaPrompt();
                        return Fail($"Persona \"{_configuredPersonaName}\" was not found and there is no free slot to create it.");
                    }

                    SetPhase(GuidedLoginPhase.AwaitingCreateConfirmation);
                    _sexDecision = new TaskCompletionSource<char?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    CreateConfirmationReady?.Invoke(_configuredPersonaName);
                    var sex = await _sexDecision.Task.ConfigureAwait(false);
                    if (sex is null)
                    {
                        AbandonPersonaPrompt();
                        return Cancel();
                    }
                    personaToCreate = _configuredPersonaName;
                    return await CreatePersonaAsync(personaToCreate, sex.Value, ct).ConfigureAwait(false);
                }

                // No configured persona name at all -- ask the UI to pick or create.
                SetPhase(GuidedLoginPhase.AwaitingPersonaChoice);
                _personaDecision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                PersonaChoiceReady?.Invoke(new PersonaChoice(existing, hasFreeSlot));
                var choice = await _personaDecision.Task.ConfigureAwait(false);
                if (choice is null)
                {
                    AbandonPersonaPrompt();
                    return Cancel();
                }

                var isExisting = slots.Any(s => !s.IsUnused && string.Equals(s.Name, choice, StringComparison.OrdinalIgnoreCase));
                if (isExisting)
                {
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
                    AbandonPersonaPrompt();
                    return Cancel();
                }
                return await CreatePersonaAsync(choice, newSex.Value, ct).ConfigureAwait(false);
            }

            // Configured persona name matched an existing one -- go straight to "p" and select it.
            SetPhase(GuidedLoginPhase.SelectingPersona);
            var directSlots = await SendPlayAndGetSlotsAsync(ct).ConfigureAwait(false);
            if (directSlots is null)
                return Fail("Timed out waiting for the persona slot list.");
            return await SelectExistingAtPromptAsync(personaToSelect, ct).ConfigureAwait(false);
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

    /// <summary>Call from the create-confirmation UI with 'm' or 'f'.</summary>
    public void ConfirmCreateSex(char sex) => _sexDecision?.TrySetResult(char.ToLowerInvariant(sex));

    /// <summary>Call from the create-confirmation UI when the player cancels.</summary>
    public void CancelCreate() => _sexDecision?.TrySetResult(null);

    private GuidedLoginResult Cancel()
    {
        SetPhase(GuidedLoginPhase.Cancelled);
        return new GuidedLoginResult(GuidedLoginOutcome.Cancelled);
    }

    private GuidedLoginResult Fail(string reason)
    {
        SetPhase(GuidedLoginPhase.Failed);
        Failed?.Invoke(reason);
        return new GuidedLoginResult(GuidedLoginOutcome.Failed, reason);
    }

    /// <summary>Sends "q" at the "By what name...?" prompt to back out to the Option menu without
    /// selecting/creating anything, best-effort (a failed connection makes this a no-op).</summary>
    private void AbandonPersonaPrompt()
    {
        if (!_disconnected)
            _conn.SendLine("q");
    }

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

    /// <summary>Sends "p" and waits for the numbered slot list, retrying a couple of times to ride
    /// out a possible transient "database restarting" delay (exact wording unknown/unverified --
    /// this is a generic timeout+retry rather than a hardcoded string match).</summary>
    private async Task<IReadOnlyList<PersonaSlot>?> SendPlayAndGetSlotsAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + DbRetryWindow + LandmarkTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ResetBuffer();
            _conn.SendLine("p");
            var seenPrompt = await WaitForLandmarkAsync(ShellText.IsPersonaNamePrompt, DbRetryInterval, ct)
                .ConfigureAwait(false);
            if (seenPrompt)
            {
                var slots = ShellText.TryParsePersonaSlots(NormalizedBufferSnapshot());
                if (slots is not null)
                    return slots;
            }
            // Not there yet -- either still loading or the server hasn't replied. Give it another
            // beat before resending "p" (harmless if the shell already showed the prompt and is
            // just waiting on us: it will simply reprint the same list).
        }
        return null;
    }

    private async Task<GuidedLoginResult> SelectExistingAtPromptAsync(string name, CancellationToken ct)
    {
        _conn.SendLine(name);
        return await WaitForGameModeAsync(ct).ConfigureAwait(false);
    }

    private async Task<GuidedLoginResult> CreatePersonaAsync(string name, char sex, CancellationToken ct)
    {
        _conn.SendLine(name);
        var sawSexPrompt = await WaitForLandmarkAsync(ShellText.IsSexPrompt, LandmarkTimeout, ct).ConfigureAwait(false);
        if (!sawSexPrompt)
            return Fail($"Timed out waiting for the new-persona sex prompt after naming \"{name}\".");
        ResetBuffer();

        _conn.SendLine(sex.ToString());
        return await WaitForGameModeAsync(ct).ConfigureAwait(false);
    }

    private async Task<GuidedLoginResult> WaitForGameModeAsync(CancellationToken ct)
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
