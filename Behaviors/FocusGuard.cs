namespace Mucka.Behaviors;

#if WINDOWS
using Xaml = Microsoft.UI.Xaml;
using XamlControls = Microsoft.UI.Xaml.Controls;
using XamlInput = Microsoft.UI.Xaml.Input;
using XamlMedia = Microsoft.UI.Xaml.Media;

/// <summary>
/// <b>The single owner of Invariant #0 on the game page: the command box owns the keyboard.</b>
/// If you are adding a widget and wondering what to wire up so it doesn't steal focus - nothing.
/// That is the entire point of this class. It works by tree position, not by name, so a control
/// that did not exist when this was written is covered the moment it appears.
///
/// <para>Four layers, weakest-but-earliest first. Any one of them holding is enough:</para>
/// <list type="number">
/// <item><description><b>Hover pre-emption.</b> A mouse reaches a control before it clicks it, so
/// the moment the pointer moves onto something new, that element and its ancestors are marked
/// non-focusable. This is the only layer that acts BEFORE the click, which is what the lazily
/// created who-list rows needed: they lose the refocus race, so the grab has to be prevented
/// rather than undone.</description></item>
/// <item><description><b>Full sweep.</b> Everything currently realised under the page gets the
/// same treatment, coalesced onto a low-priority callback after each click. Catches whatever the
/// pointer never hovered - touch, and controls that appear without the mouse going near
/// them.</description></item>
/// <item><description><b>Focus veto.</b> <c>GettingFocus</c>/<c>LosingFocus</c> on the window root:
/// cancel a move away from the command box, or redirect an incoming focus to it, so focus does not
/// land elsewhere even for one frame.</description></item>
/// <item><description><b>Refocus backstop.</b> Any pointer release on the page puts focus back on
/// the command box, then verifies once more at low priority - after WinUI has settled its own
/// post-click focus, which is the race a single deferred refocus can lose.</description></item>
/// </list>
///
/// <para><b>The exceptions, which are deliberate.</b> A real text field owns the keyboard while it
/// is open - that is Invariant #0's own carve-out. So: the command box itself is never touched;
/// any <c>TextBox</c>/<c>RichEditBox</c>/<c>PasswordBox</c>/<c>AutoSuggestBox</c> is left alone and
/// never descended into (its template needs focus-on-click to place the caret); and nothing outside
/// the guarded page subtree is policed at all, which is what keeps flyouts, the modal settings and
/// F-key editors, and the separate <c>$con</c> window working.</para>
///
/// <para><b>Why the command box's ancestors are exempt too.</b> This codebase carries two
/// contradictory claims about whether <c>AllowFocusOnInteraction=false</c> inherits to children.
/// Rather than settle it, the chain from the command box up to the page root is left untouched -
/// those are non-focusable panels, so denying them buys nothing, and if inheritance IS real,
/// denying them would stop a click from placing the caret in the command box. That would be a
/// self-inflicted Invariant #0 violation of the worst kind, so the question is designed out.</para>
///
/// <para>Windows only: this is a WinUI focus model. Android's is different and its own port
/// handles it (see GamePage's non-Windows FocusInput).</para>
/// </summary>
internal sealed class FocusGuard : IDisposable
{
    // The window root: where the routed focus and pointer events are hooked. Focus moves and
    // clicks anywhere in the window bubble to here.
    private readonly Xaml.UIElement _windowRoot;
    // The guarded subtree - GamePage's own platform view. Resolved through a callback because
    // platform views can be recreated, and because nothing outside this subtree is policed.
    private readonly Func<Xaml.FrameworkElement?> _pageRoot;
    // The command box. Also a callback: it does not exist until the Entry's handler is built.
    private readonly Func<XamlControls.TextBox?> _commandBox;
    // True while something else legitimately owns the keyboard - scrollback (the input is hidden
    // behind the SCROLLBACK bar) or a modal editor we pushed ourselves.
    private readonly Func<bool> _suspended;
    // GamePage.FocusInput - the one refocus path, which knows about the caret-reset guard.
    private readonly Action _refocus;

    private readonly XamlInput.PointerEventHandler _releasedHandler;
    private readonly XamlInput.PointerEventHandler _movedHandler;

    private object? _lastHovered;
    private bool _sweepQueued;
    private bool _disposed;

    /// <summary>
    /// Re-marks the page after the command box itself has come into existence or been recreated.
    /// Called from GamePage's input-handler hook: until the box is known, the guard deliberately
    /// marks nothing at all (it cannot yet tell which chain to leave focusable), so this is the
    /// event that turns it on.
    /// </summary>
    public void Refresh() => QueueSweep();

    public FocusGuard(
        Xaml.UIElement windowRoot,
        Func<Xaml.FrameworkElement?> pageRoot,
        Func<XamlControls.TextBox?> commandBox,
        Func<bool> suspended,
        Action refocus)
    {
        _windowRoot  = windowRoot;
        _pageRoot    = pageRoot;
        _commandBox  = commandBox;
        _suspended   = suspended;
        _refocus     = refocus;

        _windowRoot.GettingFocus += OnGettingFocus;
        _windowRoot.LosingFocus  += OnLosingFocus;
        // handledEventsToo: taps consumed by a gesture recognizer (every chip and toggle on the
        // page) never reach an ordinary handler, and those are exactly the ones that steal focus.
        _releasedHandler = new XamlInput.PointerEventHandler(OnPointerReleased);
        _movedHandler    = new XamlInput.PointerEventHandler(OnPointerMoved);
        _windowRoot.AddHandler(Xaml.UIElement.PointerReleasedEvent, _releasedHandler, handledEventsToo: true);
        _windowRoot.AddHandler(Xaml.UIElement.PointerMovedEvent,    _movedHandler,    handledEventsToo: true);

        // The page's platform children may not be realised yet; sweep once layout has run.
        QueueSweep();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowRoot.GettingFocus -= OnGettingFocus;
        _windowRoot.LosingFocus  -= OnLosingFocus;
        _windowRoot.RemoveHandler(Xaml.UIElement.PointerReleasedEvent, _releasedHandler);
        _windowRoot.RemoveHandler(Xaml.UIElement.PointerMovedEvent,    _movedHandler);
        _lastHovered = null;
    }

    // -- Layer 1: hover pre-emption ------------------------------------------
    // Runs on every mouse move over the window, so the fast path is one reference comparison and
    // nothing else (Invariant #1). Real work happens only when the pointer crosses onto a
    // different element, which is a human-speed event - and the mouse is not moving while the
    // owner is typing, so this contributes nothing to the input path.
    private void OnPointerMoved(object sender, XamlInput.PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _lastHovered)) return;
        _lastHovered = e.OriginalSource;
        DenyChain(e.OriginalSource as Xaml.DependencyObject);
    }

    // Walks up from the hovered element marking each ancestor non-focusable, stopping at the first
    // ancestor the command box shares (see the class remarks on why those are exempt).
    //
    // Two passes, look-then-mark, and the order is the whole point: this walk runs bottom-up, so a
    // text field is discovered AFTER its own template children. Marking as it went would deny the
    // parts INSIDE a TextBox - including the command box's - and those need focus-on-interaction to
    // place the caret when the player clicks into the box. One pass to find any text field in the
    // chain and abandon the whole thing; a second to mark, only once that is ruled out.
    private void DenyChain(Xaml.DependencyObject? node)
    {
        // IsGuarded is the same containment test Sweep gets for free by only ever walking down
        // from ResolveRoot(): true only when node is inside the guarded subtree and does not pass
        // through a real text field on the way up. Without it, a node OUTSIDE the subtree (e.g. a
        // control in a modal pushed as a sibling under the shared window root) never meets `root`
        // or the box chain at all, and the mark-ancestors pass below would walk straight past the
        // page and start denying shared window chrome.
        if (_commandBox() is null || !IsGuarded(node)) return;
        var root = ResolveRoot();
        if (root is null) return;
        var boxChain = CommandBoxAncestors();

        for (var d = node; d is not null; d = XamlMedia.VisualTreeHelper.GetParent(d))
        {
            if (boxChain.Contains(d)) return;
            if (d is Xaml.FrameworkElement fe) Deny(fe);
            if (ReferenceEquals(d, root)) return;
        }
    }

    // -- Layer 2: the full sweep ---------------------------------------------
    // Coalesced onto a low-priority callback, which by definition runs after pending input and
    // layout work - so it never sits in a click's own path, and it sees a tree that has finished
    // being built.
    private void QueueSweep()
    {
        if (_disposed || _sweepQueued) return;
        _sweepQueued = true;
        _windowRoot.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _sweepQueued = false;
            if (!_disposed) Sweep();
        });
    }

    private void Sweep()
    {
        var root = ResolveRoot();
        if (root is null) return;
        // Mark nothing until the command box exists. Denying the whole page while the chain that
        // must stay focusable is still unknown risks denying the command box's own ancestors -
        // which, if AllowFocusOnInteraction does inherit (see the class remarks), would stop a
        // click placing the caret in the box. Refresh() runs the moment the box appears.
        if (_commandBox() is null) return;
        var boxChain = CommandBoxAncestors();
        // The page root itself is in boxChain, so it is skipped there rather than here.
        DenySubtree(root, boxChain, depth: 0);
    }

    private static void DenySubtree(Xaml.DependencyObject node, HashSet<Xaml.DependencyObject> boxChain, int depth)
    {
        // Pure paranoia rail - a visual tree cannot contain a cycle, so this only ever stops
        // runaway recursion from a framework surprise. Set high on purpose: this page's real tree
        // is DEEP (the who-list rows sit ~18 XAML levels down, and every MAUI element expands to
        // one or more platform elements plus its control template), and a cap that the side panel
        // could actually reach would silently stop sweeping exactly the lazily created rows that
        // needed sweeping most.
        if (depth > 256) return;

        int count = XamlMedia.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = XamlMedia.VisualTreeHelper.GetChild(node, i);
            if (child is Xaml.FrameworkElement fe)
            {
                // Do not touch a real text field, and do not walk into its template: the parts
                // inside a TextBox need focus-on-interaction to place the caret on click.
                if (IsTextInput(fe)) continue;
                if (!boxChain.Contains(child)) Deny(fe);
            }
            DenySubtree(child, boxChain, depth + 1);
        }
    }

    private static void Deny(Xaml.FrameworkElement fe)
    {
        // Read before write: these are dependency properties, and the sweep re-runs often enough
        // that most elements are already denied.
        if (fe.AllowFocusOnInteraction) fe.AllowFocusOnInteraction = false;
        // Tab is a keystroke the player can hit while typing. Without this, it walks focus out of
        // the command box and into the chrome - the same violation by a different input device.
        if (fe is XamlControls.Control c && c.IsTabStop) c.IsTabStop = false;
    }

    // -- Layer 3: the focus veto ---------------------------------------------
    // Keyboard belongs to the command box. A move away from it is cancelled outright so focus
    // never lands elsewhere even for a frame; an incoming focus that is not the box is redirected
    // to it. Both are best-effort - WinUI refuses to cancel some moves - which is why layer 4
    // exists.
    private void OnGettingFocus(Xaml.UIElement sender, XamlInput.GettingFocusEventArgs args)
    {
        if (_suspended()) return;
        var box = _commandBox();
        if (box is null) return;
        if (ReferenceEquals(args.NewFocusedElement, box)) return;
        // Only police our own page. Everything else - flyouts, the modal settings and F-key
        // editors, the $con window, the OS title bar - owns its own focus, and fighting it would
        // break the very carve-out Invariant #0 grants real text fields.
        if (!IsGuarded(args.NewFocusedElement)) return;

        if (ReferenceEquals(args.OldFocusedElement, box) && args.TryCancel())
            return;
        args.TrySetNewFocusedElement(box);
    }

    // Companion to the veto: catches focus leaving the command box for a target GettingFocus never
    // sees - "nothing" - which is what a click on non-focusable chrome produces.
    private void OnLosingFocus(Xaml.UIElement sender, XamlInput.LosingFocusEventArgs args)
    {
        if (_suspended()) return;
        var box = _commandBox();
        if (box is null) return;
        if (!ReferenceEquals(args.OldFocusedElement, box)) return;
        if (args.NewFocusedElement is null) args.TryCancel();
    }

    // -- Layer 4: the refocus backstop ---------------------------------------
    private void OnPointerReleased(object sender, XamlInput.PointerRoutedEventArgs e)
    {
        // Whatever the click just built or revealed (a menu, a panel, a fresh list of rows) is
        // neutralised before the next click can reach it.
        QueueSweep();

        if (_suspended()) return;
        _refocus();
        // Verify after WinUI has settled its own post-click focus. A single deferred refocus can
        // lose that race - the lazily created who-list rows did - and this pass costs nothing when
        // the focus is already right, which is the normal case.
        _windowRoot.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || _suspended()) return;
            var box = _commandBox();
            // XamlRoot is null once the element leaves the live tree, and GetFocusedElement(null)
            // throws - this runs on a queued callback, so that would be an unhandled crash.
            if (box?.XamlRoot is null) return;
            if (!ReferenceEquals(XamlInput.FocusManager.GetFocusedElement(box.XamlRoot), box))
                _refocus();
        });
    }

    // -- Shared helpers ------------------------------------------------------

    /// <summary>
    /// The guarded subtree: GamePage's own platform view.
    ///
    /// <para>Verified rather than trusted. Every layer here is scoped to "inside this element", so
    /// if the page's platform view were ever NOT an ancestor of the command box - a MAUI handler
    /// detail this code does not control - the guard would quietly police an empty subtree and the
    /// invariant would fail wholesale and silently. That failure mode is precisely the one this
    /// class exists to end, so the relationship is checked, and if it does not hold the container
    /// is derived from the command box instead: walk up from a control we know is ours and take
    /// the topmost element below the window root.</para>
    /// </summary>
    private Xaml.FrameworkElement? ResolveRoot()
    {
        var declared = _pageRoot();
        var box = _commandBox();
        // Cannot check the relationship yet (no box): trust the declared view for now.
        if (box is null) return declared;
        if (declared is not null && IsWithin(box, declared)) return declared;

        Xaml.FrameworkElement? top = box;
        for (Xaml.DependencyObject? d = box; d is not null; d = XamlMedia.VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, _windowRoot)) break;
            if (d is Xaml.FrameworkElement fe) top = fe;
        }
        return top;
    }

    private static bool IsWithin(Xaml.DependencyObject node, Xaml.DependencyObject ancestor)
    {
        for (var d = node; d is not null; d = XamlMedia.VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="target"/> is inside the guarded page subtree and is not a real
    /// text field (nor inside one). Anything else is left to own its focus.
    /// </summary>
    private bool IsGuarded(object? target)
    {
        var root = ResolveRoot();
        if (root is null || target is not Xaml.DependencyObject node) return false;

        for (var d = node; d is not null; d = XamlMedia.VisualTreeHelper.GetParent(d))
        {
            if (d is Xaml.FrameworkElement fe && IsTextInput(fe)) return false;
            if (ReferenceEquals(d, root)) return true;
        }
        return false;   // never reached the page root: not ours
    }

    /// <summary>
    /// The command box and every ancestor up to the page root - the chain that must stay
    /// focusable so a click can still place the caret in the box. Computed fresh rather than
    /// cached: it is a walk of a dozen parents, and a cached copy goes stale the moment a
    /// platform view is recreated.
    /// </summary>
    private HashSet<Xaml.DependencyObject> CommandBoxAncestors()
    {
        var chain = new HashSet<Xaml.DependencyObject>();
        var root = ResolveRoot();
        for (Xaml.DependencyObject? d = _commandBox(); d is not null; d = XamlMedia.VisualTreeHelper.GetParent(d))
        {
            chain.Add(d);
            if (ReferenceEquals(d, root)) break;
        }
        return chain;
    }

    /// <summary>
    /// A real text field - the one thing Invariant #0 allows to hold the keyboard. Kept as a type
    /// test rather than a name list so a field added later is recognised without being registered.
    /// </summary>
    private static bool IsTextInput(Xaml.FrameworkElement fe) =>
        fe is XamlControls.TextBox
           or XamlControls.RichEditBox
           or XamlControls.PasswordBox
           or XamlControls.AutoSuggestBox;
}
#endif
