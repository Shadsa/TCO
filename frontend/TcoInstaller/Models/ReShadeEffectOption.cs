using System.ComponentModel;

namespace TcoInstaller.Models;

/// <summary>One user-toggleable technique from the ReShade preset shipped with TCO.</summary>
public sealed class ReShadeEffectOption(
    string technique,
    string name,
    string description,
    bool enabled,
    bool isBlur = false) : INotifyPropertyChanged
{
    private bool _isEnabled = enabled;
    private bool _isAvailable = true;

    public string Technique { get; } = technique;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsBlur { get; } = isBlur;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value)
                return;
            _isAvailable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
