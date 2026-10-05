using Mucka.Core.GuidedLogin;
using Mucka.ViewModels;
using MudSharp.Models;

namespace Mucka.Pages;

/// <summary>
/// Hosts the guided-login "Connecting..." experience: shows status/splash text and turns the
/// controller's persona-choice/create-confirmation events into native pickers/prompts. Pushed
/// modally by <c>ConnectPage</c> while <see cref="GuidedLoginController.RunAsync"/> runs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "A MAUI page is never Dispose()d, and the token this source hands out outlives the page's own teardown path.")]
public partial class GuidedLoginPage : ContentPage
{
    private const string DropToMenuLabel = "[Drop to menu]";

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
        // menu with the player driving it by hand, rather than dropping the connection. With mail
        // waiting it leads the list and says so: a native action sheet cannot highlight one entry,
        // so position and wording are all there is to stand out with.
        var dropLabel = choice.MailItems > 0 ? $"{DropToMenuLabel} {Core.Glyph.Envelope} Mail!" : DropToMenuLabel;
        if (choice.MailItems > 0)
            options.Insert(0, dropLabel);
        else
            options.Add(dropLabel);

        var pick = await DisplayActionSheetAsync("Choose a persona", "Cancel", null, options.ToArray());
        if (string.IsNullOrEmpty(pick) || pick == "Cancel")
        {
            _vm.Controller.CancelPersonaChoice();
            return;
        }

        if (pick == dropLabel)
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
        var choice = await DisplayActionSheetAsync($"Create persona \"{personaName}\"?", "Cancel", null, "Male", "Female", DropToMenuLabel);
        if (choice == "Male")
            _vm.Controller.ConfirmCreateSex('m');
        else if (choice == "Female")
            _vm.Controller.ConfirmCreateSex('f');
        else if (choice == DropToMenuLabel)
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
