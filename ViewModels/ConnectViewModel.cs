using System.Collections.ObjectModel;
using System.Windows.Input;
using Mucka.Core;

namespace Mucka.ViewModels;

public sealed class ConnectViewModel : BaseViewModel
{
    private string _profileName = string.Empty;
    private string _host = "mud2.co.uk";
    private int _port = 23;
    private string _statusText = string.Empty;
    private bool _isConnecting;
    private bool _hasError;

    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsConnecting { get => _isConnecting; set => Set(ref _isConnecting, value); }
    public string VersionText => $"v{AppInfo.VersionString}";
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    public bool CanConnect => !_isConnecting;

    public ObservableCollection<Profile> SavedProfiles { get; } = new();

    public ICommand ConnectCommand { get; }
    public ICommand SelectProfileCommand { get; }

    public event Action<MudConnection, Profile>? Connected;

    public ConnectViewModel()
    {
        ConnectCommand = new AsyncCommand(ConnectAsync);
        SelectProfileCommand = new Command<Profile>(SelectProfile);
        _ = LoadProfilesAsync();
    }

    private async Task ConnectAsync()
    {
        HasError = false;
        StatusText = string.Empty;
        IsConnecting = true;
        OnPropertyChanged(nameof(CanConnect));
        try
        {
            var conn = new MudConnection();
            await conn.ConnectAsync(Host.Trim(), Port);
            var profile = new Profile { Name = ProfileName, Host = Host.Trim(), Port = Port };
            await SaveCurrentProfileAsync(profile);
            Connected?.Invoke(conn, profile);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            HasError = true;
        }
        finally
        {
            IsConnecting = false;
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    private void SelectProfile(Profile p)
    {
        ProfileName = p.Name;
        Host = p.Host;
        Port = p.Port;
    }

    private async Task LoadProfilesAsync()
    {
        var list = await ProfileStore.LoadAsync();
        SavedProfiles.Clear();
        foreach (var p in list)
        {
            SavedProfiles.Add(p);
        }

        if (SavedProfiles.Count > 0)
        {
            SelectProfile(SavedProfiles[0]);
        }
    }

    private async Task SaveCurrentProfileAsync(Profile incoming)
    {
        var existing = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Host = incoming.Host;
            existing.Port = incoming.Port;
        }
        else
        {
            SavedProfiles.Add(incoming);
        }

        await ProfileStore.SaveAsync(SavedProfiles.ToList());
    }
}
