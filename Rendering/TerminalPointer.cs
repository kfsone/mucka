#if WINDOWS || MACCATALYST
using Mucka.Terminal;
#if MACCATALYST
using CoreGraphics;
using UIKit;
#endif

namespace Mucka.Rendering;

/// <summary>
/// The mouse on a <see cref="TerminalView"/>, for every pane that shows one: the wheel (and on a Mac
/// the trackpad) scrolls, a press and drag selects, a right click copies the selection, and a click
/// on a clickable name inserts its text. On a Mac a double click selects a word and a triple click
/// a line. Touch is the view's own (finger-drag panning, for Android).
///
/// <para>Hooks the view's platform view, and again whenever the handler is replaced, until
/// <see cref="Detach"/>.</para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gesture recognizers and interaction belong to the view they are added to; Detach removes them, and they live as long as it does.")]
internal sealed class TerminalPointer
{
    private readonly TerminalView _view;
    private readonly Action _copied;
    private readonly Action? _gestureEnded;

    /// <param name="copied">Raised after a right click copied a selection.</param>
    /// <param name="gestureEnded">Raised when a press or drag is done, and on a Mac a multi-click or
    /// right click, for a pane that hands the keyboard back afterwards.</param>
    public TerminalPointer(TerminalView view, Action copied, Action? gestureEnded = null)
    {
        _view = view;
        _copied = copied;
        _gestureEnded = gestureEnded;
        _view.HandlerChanged += OnHandlerChanged;
        Install();
    }

    /// <summary>Unhooks everything this hooked.</summary>
    public void Detach()
    {
        _view.HandlerChanged -= OnHandlerChanged;
        Uninstall();
    }

    private void OnHandlerChanged(object? sender, EventArgs e) => Install();

    private void Copy()
    {
        if (_view.CopySelectionToClipboard())
            _copied();
    }

#if WINDOWS
    private readonly WheelNotches _wheelNotches = new();
    private Microsoft.UI.Xaml.UIElement? _element;   // the SKXamlCanvas

    private void Install()
    {
        Uninstall();
        if (_view.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement el)
            return;
        _element = el;
        el.PointerWheelChanged += OnPointerWheel;
        // Drive mouse selection / click-to-enter from native pointer events (reliable on
        // Windows). Turn off SkiaSharp's own pointer->Touch handling so the two don't fight;
        // touch-pan via OnTouch is only needed on Android, where this hook never runs.
        _view.EnableTouchEvents = false;
        el.PointerPressed += OnPointerPressed;
        el.PointerMoved += OnPointerMoved;
        el.PointerReleased += OnPointerReleased;
        // Drag-selecting in the pane must not take keyboard focus from the text box that has it.
        if (el is Microsoft.UI.Xaml.FrameworkElement fe)
            fe.AllowFocusOnInteraction = false;
        // WinUI elements with a NULL Background are not hit-test-visible, so pointer/wheel
        // events never fire over the canvas (Skia still paints it - hence "see text, can't
        // click"). A Transparent brush IS hit-testable; the canvas paints its own background.
        var hitBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        switch (el)
        {
            case Microsoft.UI.Xaml.Controls.Panel p when p.Background is null: p.Background = hitBrush; break;
            case Microsoft.UI.Xaml.Controls.Control c when c.Background is null: c.Background = hitBrush; break;
        }
    }

    private void Uninstall()
    {
        if (_element is null)
            return;
        _element.PointerWheelChanged -= OnPointerWheel;
        _element.PointerPressed -= OnPointerPressed;
        _element.PointerMoved -= OnPointerMoved;
        _element.PointerReleased -= OnPointerReleased;
        _element = null;
    }

    private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        var pt = e.GetCurrentPoint(el);
        if (pt.Properties.IsRightButtonPressed)           // right-click copies the selection
        {
            Copy();
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;   // selection / entry is the left button only
        if (_view.TryActivateSpanInsert((float)pt.Position.X, (float)pt.Position.Y))
        {
            e.Handled = true;
            return;
        }
        el.CapturePointer(e.Pointer);                     // keep getting moves if the cursor leaves the pane
        _view.PointerPress((float)pt.Position.X, (float)pt.Position.Y);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender);
        if (!pt.Properties.IsLeftButtonPressed)
        {
            _view.PointerRelease();   // a release the pane never saw: capture was lost mid-drag
            return;
        }
        _view.PointerDrag((float)pt.Position.X, (float)pt.Position.Y);   // no-op unless a drag is active
    }

    private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);
        _view.PointerRelease();
        _gestureEnded?.Invoke();
        e.Handled = true;
    }

    private void OnPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        var rows = _wheelNotches.RowsFor(delta);
        if (rows != 0)
            _view.ScrollByRows(rows);
    }
#elif MACCATALYST
    // Wheel and trackpad scrolling arrive as a pan gesture that takes no touches. Selection comes from
    // gesture recognizers that accept only the indirect pointer, so the wheel and trackpad never
    // start one.
    private UIView? _platformView;
    private UIPanGestureRecognizer? _scrollPan;
    private double _scrollRemainderRows;
    private readonly List<UIGestureRecognizer> _selectGestures = [];
    private UIContextMenuInteraction? _copyInteraction;
    // Held here because UIKit keeps an interaction's delegate weakly.
    private CopyMenuDelegate? _copyDelegate;

    private void Install()
    {
        Uninstall();
        if (_view.Handler?.PlatformView is not UIView platformView)
            return;
        _platformView = platformView;

        _scrollPan = new UIPanGestureRecognizer(OnScroll)
        {
            AllowedScrollTypesMask = UIScrollTypeMask.All,
            AllowedTouchTypes = [],   // wheel and trackpad only; a mouse drag stays the selection's
        };
        platformView.AddGestureRecognizer(_scrollPan);

        // A press with no minimum duration, so the selection anchors where the button went down
        // rather than where a pan would have recognised; movement is unbounded so the drag keeps
        // reporting however far it goes.
        var drag = new UILongPressGestureRecognizer(OnSelectDrag)
        {
            MinimumPressDuration = 0,
            AllowableMovement = 100000,
        };
        var word = new UITapGestureRecognizer(g => OnSelectMultiClick(g, lineWise: false)) { NumberOfTapsRequired = 2 };
        var line = new UITapGestureRecognizer(g => OnSelectMultiClick(g, lineWise: true)) { NumberOfTapsRequired = 3 };
        foreach (UIGestureRecognizer gesture in new UIGestureRecognizer[] { drag, word, line })
        {
            gesture.AllowedTouchTypes = [Foundation.NSNumber.FromInt32((int)UITouchType.IndirectPointer)];
            gesture.CancelsTouchesInView = false;
            gesture.ShouldRecognizeSimultaneously = (_, _) => true;
            gesture.ShouldReceiveEvent = (_, uiEvent) => uiEvent.ButtonMask != UIEventButtonMask.Secondary;
            platformView.AddGestureRecognizer(gesture);
            _selectGestures.Add(gesture);
        }

        // A right click copies the selection, as it does on Windows. The system reports it as a
        // context-menu request; the delegate copies and declines to show a menu.
        _copyDelegate = new CopyMenuDelegate(() =>
        {
            Copy();
            _gestureEnded?.Invoke();
        });
        _copyInteraction = new UIContextMenuInteraction(_copyDelegate);
        platformView.AddInteraction(_copyInteraction);
    }

    private void Uninstall()
    {
        if (_platformView is null)
            return;
        if (_scrollPan is not null)
            _platformView.RemoveGestureRecognizer(_scrollPan);
        foreach (var gesture in _selectGestures)
            _platformView.RemoveGestureRecognizer(gesture);
        if (_copyInteraction is not null)
            _platformView.RemoveInteraction(_copyInteraction);
        _scrollPan = null;
        _selectGestures.Clear();
        _copyInteraction = null;
        _copyDelegate = null;
        _platformView = null;
    }

    private void OnScroll(UIPanGestureRecognizer gesture)
    {
        var delta = gesture.TranslationInView(gesture.View).Y;
        gesture.SetTranslation(CGPoint.Empty, gesture.View);

        // Rows of the pane's own line height, so one notch moves the same text at any font size.
        var rowDp = _view.RowHeightDp;
        if (rowDp <= 0) return;
        _scrollRemainderRows += delta / rowDp;
        var rows = (int)_scrollRemainderRows;
        if (rows == 0) return;
        _scrollRemainderRows -= rows;
        _view.ScrollByRows(rows);
    }

    private void OnSelectDrag(UILongPressGestureRecognizer gesture)
    {
        var at = gesture.LocationInView(gesture.View);
        switch (gesture.State)
        {
            case UIGestureRecognizerState.Began:
                // A clickable name inserts its text and is not the start of a selection.
                if (_view.TryActivateSpanInsert((float)at.X, (float)at.Y)) return;
                _view.PointerPress((float)at.X, (float)at.Y);
                break;
            case UIGestureRecognizerState.Changed:
                _view.PointerDrag((float)at.X, (float)at.Y);
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                _view.PointerRelease();
                _gestureEnded?.Invoke();
                break;
        }
    }

    private void OnSelectMultiClick(UIGestureRecognizer gesture, bool lineWise)
    {
        if (gesture.State != UIGestureRecognizerState.Ended) return;
        var at = gesture.LocationInView(gesture.View);
        if (lineWise) _view.SelectLineAt((float)at.X, (float)at.Y);
        else _view.SelectWordAt((float)at.X, (float)at.Y);
        _gestureEnded?.Invoke();
    }

    private sealed class CopyMenuDelegate(Action copy) : UIContextMenuInteractionDelegate
    {
        public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
        {
            copy();
            return null;
        }
    }
#endif
}
#endif
