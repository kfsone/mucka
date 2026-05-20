using Mucka.Core;

namespace Mucka.Pages;

public partial class PasswordPage : ContentPage
{
    private readonly TaskCompletionSource<PasswordResult?> _tcs;
    private bool _completed;

    public PasswordPage(PasswordPromptArgs args, TaskCompletionSource<PasswordResult?> tcs)
    {
        InitializeComponent();
        _tcs = tcs;
        Title = $"{args.ProfileName} password required";
        TitleLabel.Text = Title;
        HostLabel.Text = $"{args.Host}:{args.Port}";
        AccountLabel.Text = args.AccountId;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        PasswordEntry.Focus();
    }

    private async void OnNeverMind(object? sender, EventArgs e)
    {
        _completed = true;
        _tcs.TrySetResult(null);
        await Navigation.PopModalAsync();
    }

    private async void OnConnect(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? string.Empty;
        var remember = RememberCheck.IsChecked;
        _completed = true;
        _tcs.TrySetResult(new PasswordResult(password, remember));
        await Navigation.PopModalAsync();
    }

    private void OnPasswordCompleted(object? sender, EventArgs e) => OnConnect(sender, e);

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_completed)
            _tcs.TrySetResult(null);
    }
}
