using Mucka.Core.GuidedLogin;
using Mucka.ViewModels;
using MudSharp.Models;

namespace Mucka.Pages;

/// <summary>
/// Hosts the guided-login "Connecting…" experience: shows status/splash text and turns the
/// controller's persona-choice/create-confirmation events into native pickers/prompts. Pushed
/// modally by <c>ConnectPage</c> while <see cref="GuidedLoginController.RunAsync"/> runs.
/// </summary>
public partial class GuidedLoginPage : ContentPage
{
    private readonly GuidedLoginViewModel _vm;
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    public GuidedLoginPage(GuidedLoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
        _vm.CancelRequested += OnCancelRequested;
        _vm.PersonaChoiceRequested = ShowPersonaChoiceAsync;
        _vm.CreateConfirmationRequested = ShowCreateConfirmationAsync;
        _vm.SplashLinesReady += OnSplashLinesReady;

        // Fixed content, known before the page is ever shown -- paint it once here rather than
        // waiting on an event, so the player never sees the overlay without its explanation.
        if (_vm.DropTailLines.Count > 0)
            DropTail.AppendLines(_vm.DropTailLines);
    }

    private void OnCancelRequested() => _cts.Cancel();

    private void OnSplashLinesReady(IReadOnlyList<StyledLine> lines) => Terminal.AppendLines(lines);

    private async Task ShowPersonaChoiceAsync(PersonaChoice choice)
    {
        var options = choice.Slots
            .Where(slot => !slot.IsUnused && !string.IsNullOrWhiteSpace(slot.Name))
            .Select(slot => slot.Name!)
            .ToList();
        if (choice.CanCreateNew)
            options.Add("+ Create new");
        // Always offered, so the sheet is never empty: leaves the shell sitting at the Option
        // menu with the player driving it by hand, rather than dropping the connection.
        options.Add("[Drop to menu]");

        var pick = await DisplayActionSheetAsync("Choose a persona", "Cancel", null, options.ToArray());
        if (string.IsNullOrEmpty(pick) || pick == "Cancel")
        {
            _vm.Controller.CancelPersonaChoice();
            return;
        }

        if (pick == "[Drop to menu]")
        {
            _vm.Controller.DropToMenu();
            return;
        }

        if (pick == "+ Create new")
        {
            var name = await DisplayPromptAsync("New Persona", "Name for your new persona:", "Create", "Cancel");
            if (string.IsNullOrWhiteSpace(name))
            {
                _vm.Controller.CancelPersonaChoice();
                return;
            }
            _vm.Controller.RequestCreateNew(name.Trim());
            return;
        }

        _vm.Controller.SelectExistingPersona(pick);
    }

    private async Task ShowCreateConfirmationAsync(string personaName)
    {
        var choice = await DisplayActionSheetAsync($"Create persona \"{personaName}\"?", "Cancel", null, "Male", "Female", "[Drop to menu]");
        if (choice == "Male")
            _vm.Controller.ConfirmCreateSex('m');
        else if (choice == "Female")
            _vm.Controller.ConfirmCreateSex('f');
        else if (choice == "[Drop to menu]")
            _vm.Controller.DropToMenu();
        else
            _vm.Controller.CancelCreate();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.SplashLinesReady -= OnSplashLinesReady;
        _vm.Detach();
    }
}
