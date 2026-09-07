using NNA.Wallpaper.Engine;
using Xunit;

namespace NNA.Wallpaper.Tests;

/// <summary>
/// Unit tests for the pure capture/hover state machine InputBridge delegates to (see
/// Engine/PointerState.cs). A real InputBridge needs Raw Input, a message-only HWND and live
/// WallpaperWindow/WebView2 instances, so it cannot be exercised outside the real desktop
/// (tests/*-probe.ps1 cover that end to end); PointerState itself has none of that, which is the
/// point of having pulled it out — these three cases are exactly the robustness-review findings
/// against the old inline _left/_captured/_hoverWindow fields in InputBridge.
/// </summary>
public class PointerStateTests
{
    private sealed class FakeWindow { }

    [Fact]
    public void Capture_and_target_lost_releases_and_clears_state()
    {
        var state = new PointerState<FakeWindow>();
        var w = new FakeWindow();

        state.Press(w);
        Assert.True(state.Left);
        Assert.Same(w, state.Captured);

        var (up, leave) = state.TargetLost();

        Assert.Same(w, up); // caller owes this window a synthetic button-up
        Assert.Null(leave); // hover was never set in this scenario
        Assert.False(state.Left);
        Assert.Null(state.Captured);

        // Losing the target again with nothing pressed must not fabricate a stale Up.
        var (up2, leave2) = state.TargetLost();
        Assert.Null(up2);
        Assert.Null(leave2);
    }

    [Fact]
    public void Hover_change_sends_leave_exactly_once()
    {
        var state = new PointerState<FakeWindow>();
        var a = new FakeWindow();
        var b = new FakeWindow();

        // First time hovering a: no previous window to leave.
        Assert.Null(state.Hover(a));
        Assert.Same(a, state.HoverWindow);

        // Cursor keeps moving within the same window: hover unchanged, no repeated Leave.
        Assert.Null(state.Hover(a));
        Assert.Null(state.Hover(a));

        // Cursor moves to a different window: exactly one Leave, for the previous window only.
        var left = state.Hover(b);
        Assert.Same(a, left);
        Assert.Same(b, state.HoverWindow);

        // And it does not fire again on the next identical call.
        Assert.Null(state.Hover(b));

        // Cursor leaves every window: one more Leave, for b, then hover is empty.
        var leftAgain = state.Hover(null);
        Assert.Same(b, leftAgain);
        Assert.Null(state.HoverWindow);
    }

    [Fact]
    public void Reset_on_pause_clears_capture_and_hover_without_reporting_anything()
    {
        var state = new PointerState<FakeWindow>();
        var w = new FakeWindow();

        state.Press(w);
        state.PressMiddle();
        state.Hover(w);

        state.Reset();

        Assert.False(state.Left);
        Assert.False(state.Middle);
        Assert.Null(state.Captured);
        Assert.Null(state.HoverWindow);

        // After a pause-driven reset there is nothing left to synthesize: TargetLost must not
        // resurrect the window that was captured/hovered before the reset.
        var (up, leave) = state.TargetLost();
        Assert.Null(up);
        Assert.Null(leave);
    }
}
