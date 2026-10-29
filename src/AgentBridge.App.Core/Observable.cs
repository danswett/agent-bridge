using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AgentBridge.App.Core;

// Where view models change: the UI thread in the app, inline in tests.
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public static readonly InlineDispatcher Instance = new();
    public void Post(Action action) => action();
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Everything at once: what a card does when its session changes wholesale.
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

// How a status line or an outcome is coloured, as the PWA's tones.
public enum Tone { None, Progress, Ok, Warn, Bad }
