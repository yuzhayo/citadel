using System.ComponentModel;
using System.Runtime.CompilerServices;
using Module.Camoprof.Providers.Google;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Launcher;

internal sealed class LauncherProfileRow : INotifyPropertyChanged
{
    private bool _isRunning;
    private GoogleAccountResult _google;

    public LauncherProfileRow(ProfileEntry profile, bool isRunning)
    {
        Profile = profile;
        _isRunning = isRunning;
        _google = new GoogleAccountResult(
            profile.IsLinked ? GoogleAccountState.Unknown : GoogleAccountState.Unlinked,
            profile.Email,
            profile.IsLinked ? "belum diperiksa" : "profile belum dipasangkan ke email",
            DateTimeOffset.MinValue);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProfileEntry Profile { get; }

    public string ProfileId => Profile.ProfileId;

    public string Name => Profile.DisplayName;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public string ActionLabel => IsRunning ? "Close" : "Launch";

    public GoogleAccountResult Google
    {
        get => _google;
        set
        {
            if (Equals(_google, value))
            {
                return;
            }

            _google = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GoogleLabel));
            OnPropertyChanged(nameof(GoogleDetail));
        }
    }

    public string GoogleLabel => Google.Label;

    public string GoogleDetail => Google.CheckedAt == DateTimeOffset.MinValue
        ? Google.Reason
        : Google.CheckedAt.ToString("HH:mm:ss") + " · " + Google.Reason;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
