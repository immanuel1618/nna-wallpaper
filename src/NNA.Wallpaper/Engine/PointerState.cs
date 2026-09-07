namespace NNA.Wallpaper.Engine;

/// <summary>
/// Pure capture/hover state machine for <see cref="InputBridge"/> — no Win32, no WebView2, so it can
/// be unit-tested (<c>src/NNA.Wallpaper.Tests/PointerStateTests.cs</c>) without a real desktop or
/// WallpaperWindow. Generic over the window/target type for that reason.
///
/// Tracks three things InputBridge otherwise kept as loose fields: which buttons are logically down
/// (for the MK_LBUTTON/MK_MBUTTON bits synthesized into every forwarded message), which window has
/// the in-progress left-button press ("capture" — press and release must land on the same window
/// even if the cursor drifts over another one first), and which composition-hosted window last got a
/// synthetic hover-enter (so it — and only it — gets exactly one Leave when hover moves elsewhere).
/// </summary>
public sealed class PointerState<T> where T : class
{
    public bool Left { get; private set; }
    public bool Middle { get; private set; }
    public T? Captured { get; private set; }
    public T? HoverWindow { get; private set; }

    /// <summary>Left button went down on <paramref name="target"/>: starts capture.</summary>
    public void Press(T target)
    {
        Left = true;
        Captured = target;
    }

    /// <summary>Left button went up: ends capture.</summary>
    public void Release()
    {
        Left = false;
        Captured = null;
    }

    public void PressMiddle() => Middle = true;
    public void ReleaseMiddle() => Middle = false;

    /// <summary>
    /// The window/target under the cursor is no longer usable as a forwarding destination (it left
    /// the desktop entirely, went not-Ready/Paused mid-gesture, or the desktop layer is being torn
    /// down for reattach) while a press or a hover may still be outstanding. Resets all state and
    /// reports what the caller still owes the outside world: <c>Up</c> is the window that needs a
    /// synthetic button-up (only when the left button was actually down; the caller should skip
    /// sending it if that window turns out not to be usable either, e.g. already disposed), and
    /// <c>Leave</c> is the window that needs a synthetic hover-leave (only when hover had actually
    /// moved there). Either or both may be null.
    /// </summary>
    public (T? Up, T? Leave) TargetLost()
    {
        var up = Left ? Captured : null;
        var leave = HoverWindow;
        Left = false;
        Middle = false;
        Captured = null;
        HoverWindow = null;
        return (up, leave);
    }

    /// <summary>
    /// Re-evaluates hover against <paramref name="target"/> (the window under the cursor right now,
    /// or null when the cursor is over none). Returns the previous hover window exactly once, the
    /// moment hover actually changes away from it — the caller sends it a Leave; every other call
    /// (hover unchanged) returns null and sends nothing, so a real window never gets more than one
    /// Leave per hover change.
    /// </summary>
    public T? Hover(T? target)
    {
        if (ReferenceEquals(HoverWindow, target)) return null;
        var prev = HoverWindow;
        HoverWindow = target;
        return prev;
    }

    /// <summary>
    /// Drops all state without reporting anything to synthesize — for callers that already know no
    /// message can or should be sent (the windows are already disposed on reattach, or the engine is
    /// entering a user pause and the pointer bridge should simply go quiet).
    /// </summary>
    public void Reset()
    {
        Left = false;
        Middle = false;
        Captured = null;
        HoverWindow = null;
    }
}
