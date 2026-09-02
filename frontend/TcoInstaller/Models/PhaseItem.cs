using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace TcoInstaller.Models;

public enum PhaseState
{
    Pending,
    Active,
    Complete,
    Failed
}

/// <summary>Mutable presentation state for one installer workflow phase.</summary>
public sealed class PhaseItem(string id, string label) : INotifyPropertyChanged
{
    private PhaseState _state;

    public string Id { get; } = id;
    public string Label { get; } = label;

    public PhaseState State
    {
        get => _state;
        set
        {
            if (_state == value)
                return;

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusGlyph));
        }
    }

    public IBrush StatusBrush => State switch
    {
        PhaseState.Active => new SolidColorBrush(Color.Parse("#8B72FF")),
        PhaseState.Complete => new SolidColorBrush(Color.Parse("#42D392")),
        PhaseState.Failed => new SolidColorBrush(Color.Parse("#FF6B7A")),
        _ => new SolidColorBrush(Color.Parse("#394255"))
    };

    public string StatusGlyph => State switch
    {
        PhaseState.Complete => "✓",
        PhaseState.Failed => "!",
        PhaseState.Active => "•",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
