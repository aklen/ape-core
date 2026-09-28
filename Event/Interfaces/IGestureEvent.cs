namespace Ape.Core.Event;

/// <summary>
/// Base interface for gesture-related events (mouse, touch, keyboard, etc.)
/// </summary>
public interface IGestureEvent : IEvent
{
    /// <summary>
    /// Type of gesture event (RawTouch, Drag, Pinch, Keyboard, etc.)
    /// </summary>
    string GestureType { get; }
}
