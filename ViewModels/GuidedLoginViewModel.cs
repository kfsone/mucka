using System.Windows.Input;
using Microsoft.Maui.Graphics;
using Mucka.Core;
using Mucka.Core.GuidedLogin;
using MudSharp.Models;
using Mucka.Commands;

namespace Mucka.ViewModels;

/// <summary>
/// Bindable status/splash state for <c>GuidedLoginPage</c>. Interactive decisions (persona
/// pick/create, sex confirmation) are surfaced as awaitable hooks the page sets, matching the
/// <c>ConnectViewModel.PasswordRequired</c> pattern - this view model stays UI-toolkit agnostic
/// (no DisplayAlert/DisplayActionSheet calls here). Splash lines are forwarded as raw
/// <see cref="StyledLine"/>s via <see cref="SplashLinesReady"/> for the page to feed straight into
/// a <c>TerminalView</c> (real ANSI colours/font, same as the game screen) rather than being
/// bound as plain text.
/// </summary>
public sealed class GuidedLoginViewModel : BaseViewModel
{
    private readonly GuidedLoginController _controller;
    private readonly SessionDropContext? _drop;
    private string _status = "Connecting...";
    private bool _hasSplash;

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool HasSplash { get => _hasSplash; private set => Set(ref _hasSplash, value); }

    // -- Why the player is looking at this overlay ----------------------------------------------
    // Fixed for the whole life of the page: set once from the drop that opened it (null on the
    // initial connect, where there is no drop to explain) and never touched again, so it survives
    // every phase change, the picker sheet, a failure dialog, and the page's own teardown.

    public bool HasDropContext => _drop is not null;
    public string DropHeadline => _drop?.Headline ?? string.Empty;

    /// <summary>Amber for a reset (routine, you'll be straight back), red for an unexplained drop,
    /// bone-white for a death.</summary>
    public Color DropHeadlineColor => _drop?.Reason switch
    {
        SessionDropReason.Reset => Color.FromArgb("#d29922"),
        SessionDropReason.Permadeath => Color.FromArgb("#c9d1d9"),
        SessionDropReason.Quit => Color.FromArgb("#8b949e"),   // your own doing; nothing to alarm about
        _ => Color.FromArgb("#f85149"),
    };

    public bool HasDropTail => _drop?.ShowsTailLines == true;

    /// <summary>The server's own last words before the drop, for the page to feed into its
    /// <c>TerminalView</c> - empty when the headline says it all (a reset).</summary>
    public IReadOnlyList<StyledLine> DropTailLines
        => _drop?.ShowsTailLines == true ? _drop.TailLines : Array.Empty<StyledLine>();

    /// <summary>Fired once the real login splash/banner has been captured, for the page to render
    /// via <c>Terminal.AppendLines(...)</c>.</summary>
    public event Action<IReadOnlyList<StyledLine>>? SplashLinesReady;

    public GuidedLoginController Controller => _controller;

    /// <summary>Set by the page: shown when the profile has no configured persona name (or it
    /// wasn't found and a free slot exists). Must call SelectExistingPersona/RequestCreateNew/
    /// CancelPersonaChoice on the controller once the player decides.</summary>
    public Func<PersonaChoice, Task>? PersonaChoiceRequested { get; set; }

    /// <summary>Set by the page: shown to confirm creating a persona with the given name
    /// (Male/Female/Cancel). Must call ConfirmCreateSex/CancelCreate on the controller.</summary>
    public Func<string, Task>? CreateConfirmationRequested { get; set; }

    public ICommand CancelCommand { get; }

    public event Action? CancelRequested;

    public GuidedLoginViewModel(GuidedLoginController controller, SessionDropContext? drop = null)
    {
        _controller = controller;
        _drop = drop;
        CancelCommand = new Command(() => CancelRequested?.Invoke());

        _controller.PhaseChanged += OnPhaseChanged;
        _controller.SplashTextReady += OnSplashLinesReady;
        _controller.PersonaChoiceReady += OnPersonaChoiceReady;
        _controller.CreateConfirmationReady += OnCreateConfirmationReady;
    }

    public void Detach()
    {
        _controller.PhaseChanged -= OnPhaseChanged;
        _controller.SplashTextReady -= OnSplashLinesReady;
        _controller.PersonaChoiceReady -= OnPersonaChoiceReady;
        _controller.CreateConfirmationReady -= OnCreateConfirmationReady;

        // Release what the page gave us as well as what we subscribed. Both handlers marshal
        // through the dispatcher, so an event raised just before Detach is already queued with
        // the hook captured, and would run against a page being popped.
        PersonaChoiceRequested = null;
        CreateConfirmationRequested = null;
    }

    private void OnPhaseChanged(GuidedLoginPhase phase)
        => MainThread.BeginInvokeOnMainThread(() => Status = Describe(phase));

    private void OnSplashLinesReady(IReadOnlyList<StyledLine> lines)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            HasSplash = lines.Count > 0;
            SplashLinesReady?.Invoke(lines);
        });

    // Both hooks run in an async void lambda, so nothing above observes a faulted task. A throw
    // that escaped would skip select, create and cancel alike: the controller would still be
    // awaiting the decision, RunAsync would never return, and the picker would sit abandoned over
    // a live connection - the state the drop-to-menu rule exists to prevent, reached by nobody
    // choosing it. Cancelling is the safe resolution, and the controller's TrySetResult makes it a
    // no-op when the hook already answered before it threw.

    private void OnPersonaChoiceReady(PersonaChoice choice)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var hook = PersonaChoiceRequested;
            try
            {
                if (hook != null)
                    await hook(choice);
                else
                    _controller.CancelPersonaChoice();
            }
            catch (Exception ex)
            {
                CrashLog.Write("GuidedLoginPersonaChoice", ex);
                _controller.CancelPersonaChoice();
            }
        });

    private void OnCreateConfirmationReady(string name)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var hook = CreateConfirmationRequested;
            try
            {
                if (hook != null)
                    await hook(name);
                else
                    _controller.CancelCreate();
            }
            catch (Exception ex)
            {
                CrashLog.Write("GuidedLoginCreateConfirmation", ex);
                _controller.CancelCreate();
            }
        });

    private static string Describe(GuidedLoginPhase phase) => phase switch
    {
        GuidedLoginPhase.Connecting => "Connecting...",
        GuidedLoginPhase.NegotiatingShell => "Negotiating terminal...",
        GuidedLoginPhase.QueryingPersonae => "Checking your personae...",
        GuidedLoginPhase.AwaitingPersonaChoice => "Waiting for you to choose a persona...",
        GuidedLoginPhase.AwaitingCreateConfirmation => "Waiting for you to confirm persona creation...",
        GuidedLoginPhase.AwaitingSexChoice => "Waiting for you to choose a sex for the new persona...",
        GuidedLoginPhase.SelectingPersona => "Selecting persona...",
        GuidedLoginPhase.WaitingForGameMode => "Entering the game...",
        GuidedLoginPhase.Succeeded => "Connected.",
        GuidedLoginPhase.Failed => "Persona login failed.",
        GuidedLoginPhase.Cancelled => "Cancelled.",
        _ => "Working...",
    };
}
