using System;
using System.Collections.Generic;

namespace GlassBogo.App;

internal enum HoverPhase { Closed, HoverPending, Opening, Open, SwitchPending, ClosingPending, Closing }
internal enum HoldReason { PointerInDock, PointerInPopup, Dragging, ContextMenu, Editing, KeyboardFocus, OLEDrag }

// No WPF or timer dependency: the view supplies input and schedules the returned delays.
internal sealed class HoverCoordinator
{
    internal const int OpenDelay = 100, SwitchDelay = 35, CloseDelay = 240, DragDelay = 350;
    private readonly Dictionary<HoldReason, int> holds = new();
    internal HoverPhase Phase { get; private set; }
    internal string? Selected { get; private set; }
    internal string? Pending { get; private set; }
    internal int Generation { get; private set; }
    internal bool IsHeld => holds.Count > 0;

    internal int Enter(string category, bool dragging = false)
    {
        CancelClose();
        Pending = category;
        Phase = Selected is null ? HoverPhase.HoverPending : HoverPhase.SwitchPending;
        return dragging ? DragDelay : Selected is null ? OpenDelay : SwitchDelay;
    }
    internal void Leave(string category)
    {
        if (Pending != category) return;
        Pending = null;
        Phase = Selected is null ? HoverPhase.Closed : HoverPhase.Open;
    }
    internal bool Open(string category)
    {
        var changed = Selected != category;
        Generation++;
        Selected = category;
        Pending = null;
        Phase = HoverPhase.Opening;
        return changed;
    }
    internal void Opened(int generation)
    {
        if (Generation == generation && Phase == HoverPhase.Opening) Phase = HoverPhase.Open;
    }
    internal void CancelClose()
    {
        if (Phase is not (HoverPhase.Closing or HoverPhase.ClosingPending)) return;
        Generation++;
        Phase = Selected is null ? HoverPhase.Closed : HoverPhase.Open;
    }
    internal void ScheduleClose()
    {
        if (Selected is not null && !IsHeld && Phase != HoverPhase.Closing) Phase = HoverPhase.ClosingPending;
    }
    internal int? BeginClose(bool inCorridor)
    {
        if (Selected is null || IsHeld || inCorridor) return null;
        Pending = null;
        Phase = HoverPhase.Closing;
        return ++Generation;
    }
    internal bool CompleteClose(int generation)
    {
        if (Generation != generation || Phase != HoverPhase.Closing) return false;
        Reset();
        return true;
    }
    internal void Reset()
    {
        Generation++;
        Pending = Selected = null;
        Phase = HoverPhase.Closed;
        // Scoped menu/edit/drag leases still own their lifetime after Esc or a display change.
    }
    internal void SetHold(HoldReason reason, bool held)
    {
        if (held) { holds[reason] = 1; CancelClose(); }
        else holds.Remove(reason);
    }
    internal IDisposable Hold(HoldReason reason)
    {
        holds[reason] = holds.GetValueOrDefault(reason) + 1;
        CancelClose();
        return new Lease(() =>
        {
            var count = holds.GetValueOrDefault(reason);
            if (count <= 1) holds.Remove(reason); else holds[reason] = count - 1;
        });
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() { var current = action; action = null; current?.Invoke(); }
    }

    // A narrow trapezoid across the vertical gap, expressed entirely in screen pixels.
    internal static bool InCorridor(double x, double y, double dockLeft, double dockRight, double dockTop,
        double popupLeft, double popupRight, double popupBottom, double tolerance)
    {
        var gap = dockTop - popupBottom;
        if (gap < 0 || y < popupBottom - tolerance || y > dockTop + tolerance) return false;
        var t = gap == 0 ? .5 : Math.Clamp((y - popupBottom) / gap, 0, 1);
        var left = popupLeft + (dockLeft - popupLeft) * t;
        var right = popupRight + (dockRight - popupRight) * t;
        return x >= left - tolerance && x <= right + tolerance;
    }
}
