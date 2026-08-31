using System.ComponentModel;
using System.Runtime.CompilerServices;
using Module.Camoprof.SharedLogic;

namespace Module.Camoprof.Launcher;

internal sealed class LauncherProfileRow : INotifyPropertyChanged
{
    private bool _isRunning;

    public LauncherProfileRow(ProfileEntry profile, bool isRunning)
    {
        Profile = profile;
        _isRunning = isRunning;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProfileEntry Profile { get; }

    public string Name => Profile.Name;

    public string LastWrite => Profile.LastWrite;

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
            OnPropertyChanged(nameof(CanVerify));
        }
    }

    public string ActionLabel => IsRunning ? "Close" : "Launch";

    public bool CanVerify => IsRunning;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
