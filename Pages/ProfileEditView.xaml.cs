using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>The profile editor. Bound to the connect page's <see cref="ConnectViewModel"/>, so the
/// copy beside the list and the copy on the edit sheet edit the same form.</summary>
public partial class ProfileEditView : ContentView
{
    /// <summary>Raised by the Cancel button, which only the sheet shows.</summary>
    public event EventHandler? CloseRequested;

    public ProfileEditView()
    {
        InitializeComponent();
    }

    /// <summary>The sheet always shows Cancel. Beside the list it shows only for a new profile, which
    /// Cancel drops.</summary>
    public bool ShowClose
    {
        get => _showClose;
        set
        {
            _showClose = value;
            if (value)
            {
                CloseButton.RemoveBinding(IsVisibleProperty);
                CloseButton.IsVisible = true;
            }
        }
    }
    private bool _showClose;

    private void OnCloseClicked(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private Page? HostPage
    {
        get
        {
            Element? e = this;
            while (e is not null and not Page) e = e.Parent;
            return e as Page;
        }
    }

    private const string SecureChoice = "Secure (SSH)";
    private const string TelnetChoice = "Oldschool (Telnet)";
    private const string ManualChoice = "Manual (type a number)";

    /// <summary>The connection picker: SSH and Telnet on their usual ports, or a port number typed in.</summary>
    private async void OnConnectionClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not ConnectViewModel vm || HostPage is not { } page) return;
        string[] choices = ConnectViewModel.SshAvailable
            ? [SecureChoice, TelnetChoice, ManualChoice]
            : [TelnetChoice, ManualChoice];
        var pick = await page.DisplayActionSheetAsync("Connection", "Cancel", null, choices);
        switch (pick)
        {
            case SecureChoice:
                vm.SetConnection(ConnectViewModel.ConnectionSsh);
                break;
            case TelnetChoice:
                vm.SetConnection(ConnectViewModel.ConnectionTelnet);
                break;
            case ManualChoice:
                var typed = await page.DisplayPromptAsync("Port", "The port number to connect to:",
                    accept: "OK", cancel: "Cancel", keyboard: Keyboard.Numeric,
                    initialValue: vm.Port > 0 ? vm.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
                if (int.TryParse(typed, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
                    vm.SetConnection("port", port);
                break;
        }
    }
}
