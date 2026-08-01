using System.Windows.Input;
using Mucka.Core.GuidedLogin;

namespace Mucka.ViewModels;

/// <summary>
/// Bindable status/splash state for <c>GuidedLoginPage</c>. Interactive decisions (persona
/// pick/create, sex confirmation) are surfaced as awaitable hooks the page sets, matching the
/// <c>ConnectViewModel.PasswordRequired</c> pattern — this view model stays UI-toolkit agnostic
/// (no DisplayAlert/DisplayActionSheet calls here).
/// </summary>
public sealed class GuidedLoginViewModel : BaseViewModel
{
    private readonly GuidedLoginController _controller;
    private string _status = "Connecting…";
    private string _splashText = string.Empty;

    public string Status { get => _status; set => Set(ref _status, value); }
    public string SplashText
    {
        get => _splashText;
        set
        {
            if (Set(ref _splashText, value))
                OnPropertyChanged(nameof(HasSplash));
        }
    }
    public bool HasSplash => !string.IsNullOrWhiteSpace(SplashText);

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

    public GuidedLoginViewModel(GuidedLoginController controller)
    {
        _controller = controller;
        CancelCommand = new Command(() => CancelRequested?.Invoke());

        _controller.PhaseChanged += OnPhaseChanged;
        _controller.SplashTextReady += OnSplashTextReady;
        _controller.PersonaChoiceReady += OnPersonaChoiceReady;
        _controller.CreateConfirmationReady += OnCreateConfirmationReady;
    }

    public void Detach()
    {
        _controller.PhaseChanged -= OnPhaseChanged;
        _controller.SplashTextReady -= OnSplashTextReady;
        _controller.PersonaChoiceReady -= OnPersonaChoiceReady;
        _controller.CreateConfirmationReady -= OnCreateConfirmationReady;
    }

    private void OnPhaseChanged(GuidedLoginPhase phase)
        => MainThread.BeginInvokeOnMainThread(() => Status = Describe(phase));

    private void OnSplashTextReady(string text)
        => MainThread.BeginInvokeOnMainThread(() => SplashText = text);

    private void OnPersonaChoiceReady(PersonaChoice choice)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (PersonaChoiceRequested != null)
                await PersonaChoiceRequested(choice);
            else
                _controller.CancelPersonaChoice();
        });

    private void OnCreateConfirmationReady(string name)
        => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (CreateConfirmationRequested != null)
                await CreateConfirmationRequested(name);
            else
                _controller.CancelCreate();
        });

    private static string Describe(GuidedLoginPhase phase) => phase switch
    {
        GuidedLoginPhase.Connecting => "Connecting…",
        GuidedLoginPhase.NegotiatingShell => "Negotiating terminal…",
        GuidedLoginPhase.QueryingPersonae => "Checking your personae…",
        GuidedLoginPhase.AwaitingPersonaChoice => "Waiting for you to choose a persona…",
        GuidedLoginPhase.AwaitingCreateConfirmation => "Waiting for you to confirm persona creation…",
        GuidedLoginPhase.AwaitingSexChoice => "Waiting for you to choose a sex for the new persona…",
        GuidedLoginPhase.SelectingPersona => "Selecting persona…",
        GuidedLoginPhase.WaitingForGameMode => "Entering the game…",
        GuidedLoginPhase.Succeeded => "Connected.",
        GuidedLoginPhase.Failed => "Guided login failed.",
        GuidedLoginPhase.Cancelled => "Cancelled.",
        _ => "Working…",
    };
}
