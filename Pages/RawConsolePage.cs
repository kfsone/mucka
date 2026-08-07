#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// Windows-only diagnostic window opened by the $con command.
///
/// Top half: read-only scrolling Editor showing raw bytes to/from the server,
/// shown as escaped text (← for RX, → for TX). Text is selectable and copyable.
///
/// Bottom half: accumulated outgoing sequence built by raw keypresses.
/// Every key appends its byte(s) to the sequence; Backspace removes the last
/// byte; clicking Send transmits the accumulated bytes then clears the buffer.
/// Enter adds \r\n (not Send) so sequences can include CR/LF explicitly.
/// </summary>
internal sealed class RawConsolePage : ContentPage
{
    private readonly GameViewModel _vm;

    // Accumulated outgoing sequence
    private readonly List<byte> _sequence = [];

    // Output ring-buffer (UI thread only)
    private const int MaxOutputChars = 32768;
    private readonly StringBuilder _outputSb = new(MaxOutputChars);

    // Thread-safe pending output from read/write threads
    private readonly StringBuilder _pendingOutput = new();
    private readonly object _pendingLock = new();
    private volatile bool _pendingDirty;
    private bool _paused;

    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private int _markCount;

    private readonly Editor      _outputEditor;
    private readonly Label       _seqLabel;
    private readonly Label       _hexLabel;
    private readonly Label       _statusLabel;
    private readonly Label       _cancelLabel;
    private readonly Button      _stopBtn;
    private readonly CheckBox    _probeBlockCheck;
    private IDispatcherTimer?    _updateTimer;
    private bool                 _keyboardHooked;

    public RawConsolePage(GameViewModel vm)
    {
        _vm = vm;
        BackgroundColor = Color.FromArgb("#0C0C0C");

        // Read-only Editor: maps to WinUI3 TextBox — supports selection and copy natively.
        _outputEditor = new Editor
        {
            FontFamily      = "Cascadia Mono",
            FontSize        = 12,
            TextColor       = Color.FromArgb("#CCCCCC"),
            BackgroundColor = Color.FromArgb("#0C0C0C"),
            IsReadOnly      = true,
            VerticalOptions = LayoutOptions.Fill,
            AutoSize        = EditorAutoSizeOption.Disabled,
        };

        _seqLabel = new Label
        {
            FontFamily           = "Cascadia Mono",
            FontSize             = 13,
            TextColor            = Color.FromArgb("#F9F1A5"),
            Text                 = "(press keys to build sequence — Backspace removes last byte)",
            MinimumHeightRequest = 22,
            Padding              = new Thickness(4, 2),
            BackgroundColor      = Color.FromArgb("#1A1A1A"),
            VerticalOptions      = LayoutOptions.Center,
        };

        _hexLabel = new Label
        {
            FontFamily  = "Cascadia Mono",
            FontSize    = 11,
            TextColor   = Color.FromArgb("#767676"),
            Padding     = new Thickness(4, 1),
        };

        _statusLabel = new Label
        {
            FontFamily      = "Cascadia Mono",
            FontSize        = 11,
            TextColor       = Color.FromArgb("#767676"),
            VerticalOptions = LayoutOptions.Center,
        };

        // ── Cancel (✕) tap target — inline label, sizes to text height ──────
        _cancelLabel = new Label
        {
            Text                    = "✕",
            FontFamily              = "Cascadia Mono",
            FontSize                = 12,
            TextColor               = Color.FromArgb("#666666"),
            Padding                 = new Thickness(6, 2),
            VerticalOptions         = LayoutOptions.Center,
            VerticalTextAlignment   = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            BackgroundColor         = Color.FromArgb("#1A1A1A"),
        };
        _cancelLabel.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(OnCancelTapped),
        });

        // Sequence row: label fills the space, ✕ sits at the right end
        var inputRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        inputRow.Add(_seqLabel,    column: 0, row: 0);
        inputRow.Add(_cancelLabel, column: 1, row: 0);

        // ── Output-area buttons ───────────────────────────────────────────
        var sendBtn = new Button
        {
            Text            = "Send",
            BackgroundColor = Color.FromArgb("#0037DA"),
            TextColor       = Color.FromArgb("#F2F2F2"),
            Padding         = new Thickness(16, 5),
        };
        sendBtn.Clicked += OnSendClicked;

        var markBtn = new Button
        {
            Text            = "Mark",
            BackgroundColor = Color.FromArgb("#333333"),
            TextColor       = Color.FromArgb("#CCCCCC"),
            Padding         = new Thickness(16, 5),
        };
        markBtn.Clicked += OnMarkClicked;

        _stopBtn = new Button
        {
            Text            = "Stop",
            BackgroundColor = Color.FromArgb("#333333"),
            TextColor       = Color.FromArgb("#CCCCCC"),
            Padding         = new Thickness(16, 5),
        };
        _stopBtn.Clicked += OnStopStartClicked;

        var clearBtn = new Button
        {
            Text            = "Clear",
            BackgroundColor = Color.FromArgb("#333333"),
            TextColor       = Color.FromArgb("#CCCCCC"),
            Padding         = new Thickness(16, 5),
        };
        clearBtn.Clicked += OnClearOutputClicked;

        // Stop status probes: while $con is open you usually want to watch raw traffic without
        // the periodic FES/FEW/FEI interrupts. Checked on open (see OnAppearing); toggling it
        // holds/resumes probes live, and closing the window resumes them (see OnDisappearing).
        _probeBlockCheck = new CheckBox { Color = Color.FromArgb("#0037DA"), VerticalOptions = LayoutOptions.Center };
        _probeBlockCheck.CheckedChanged += (_, e) => _vm.SetStatusProbesBlocked(e.Value);
        var probeBlockLabel = new Label
        {
            Text            = "Stop status probes",
            FontFamily      = "Cascadia Mono",
            FontSize        = 11,
            TextColor       = Color.FromArgb("#CCCCCC"),
            VerticalOptions = LayoutOptions.Center,
        };
        var probeBlockStack = new HorizontalStackLayout
        {
            Spacing  = 2,
            Children = { _probeBlockCheck, probeBlockLabel },
        };

        var buttonRow = new HorizontalStackLayout
        {
            Spacing  = 8,
            Margin   = new Thickness(0, 4, 0, 0),
            Children = { sendBtn, markBtn, _stopBtn, clearBtn, probeBlockStack, _statusLabel },
        };

        var separator = new BoxView
        {
            Color         = Color.FromArgb("#333333"),
            HeightRequest = 1,
        };

        var grid = new Grid
        {
            Padding    = new Thickness(6),
            RowSpacing = 3,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        grid.Add(_outputEditor, column: 0, row: 0);
        grid.Add(separator,     column: 0, row: 1);
        grid.Add(inputRow,      column: 0, row: 2);
        grid.Add(_hexLabel,     column: 0, row: 3);
        grid.Add(buttonRow,     column: 0, row: 4);

        Content = grid;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RawBytesReceived += OnRawBytesReceived;
        _vm.RawBytesSent     += OnRawBytesSent;
        // Opening $con blocks status probes by default (CheckedChanged applies the hold).
        _probeBlockCheck.IsChecked = true;
        TryHookKeyboard();

        _updateTimer = Dispatcher.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMilliseconds(50);
        _updateTimer.Tick += OnUpdateTick;
        _updateTimer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.RawBytesReceived -= OnRawBytesReceived;
        _vm.RawBytesSent     -= OnRawBytesSent;
        // Resume probes on close — the only control lives in this window, so we must not leave
        // the side panel silently starved once it's gone.
        _vm.SetStatusProbesBlocked(false);

        if (_keyboardHooked &&
            Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
        {
            root.PreviewKeyDown -= OnRootPreviewKeyDown;
            _keyboardHooked = false;
        }

        _updateTimer?.Stop();
        _updateTimer = null;
    }

    private bool TryHookKeyboard()
    {
        if (_keyboardHooked) return true;
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window win &&
            win.Content is Microsoft.UI.Xaml.UIElement root)
        {
            root.PreviewKeyDown += OnRootPreviewKeyDown;
            _keyboardHooked = true;
            return true;
        }
        return false;
    }

    // ── Incoming byte stream (background threads) ─────────────────────────

    private void OnRawBytesReceived(byte[] bytes)
    {
        var text = FormatChunk("←", bytes);
        lock (_pendingLock)
        {
            _pendingOutput.Append(text);
            _pendingDirty = true;
        }
    }

    private void OnRawBytesSent(byte[] bytes)
    {
        var text = FormatChunk("→", bytes);
        lock (_pendingLock)
        {
            _pendingOutput.Append(text);
            _pendingDirty = true;
        }
    }

    // ── UI timer (50 ms, main thread) ────────────────────────────────────

    private void OnUpdateTick(object? sender, EventArgs e)
    {
        if (!_keyboardHooked) TryHookKeyboard();

        if (_paused || !_pendingDirty) return;

        string pending;
        lock (_pendingLock)
        {
            pending = _pendingOutput.ToString();
            _pendingOutput.Clear();
            _pendingDirty = false;
        }

        _outputSb.Append(pending);
        if (_outputSb.Length > MaxOutputChars)
            _outputSb.Remove(0, _outputSb.Length - MaxOutputChars);

        _outputEditor.Text = _outputSb.ToString();
        ScrollOutputToEnd();
    }

    /// <summary>
    /// Scrolls the output editor to the end, but only when the user has no active
    /// text selection — so selecting text to copy isn't disrupted by incoming traffic.
    ///
    /// Drives the TextBox's inner ScrollViewer directly rather than Select(end) caret
    /// tracking: Select() only scrolls a TextBox that has keyboard focus (this one never
    /// does under the main window's focus pinning), and the Win32 caret is a per-thread
    /// singleton — repositioning it from this window every 50ms broke caret-follow in
    /// the main window's input box.
    /// </summary>
    private void ScrollOutputToEnd()
    {
        if (_outputEditor.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.TextBox tb
            || tb.SelectionLength > 0)
            return;
        if (FindScrollViewer(tb) is not { } scroller)
            return;
        // The Text assignment that precedes this call hasn't been measured yet; force the
        // layout pass so ScrollableHeight reflects the appended lines before we jump.
        tb.UpdateLayout();
        scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
    }

    private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindScrollViewer(Microsoft.UI.Xaml.DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }

    // ── Byte escape formatting ────────────────────────────────────────────

    private static string TimestampPrefix(double elapsed) => $"+{elapsed,8:F3}|";

    private string FormatChunk(string dir, byte[] bytes)
    {
        var prefix = TimestampPrefix(_stopwatch.Elapsed.TotalSeconds);
        var sb = new StringBuilder(bytes.Length * 3 + prefix.Length + 4);
        sb.Append(prefix);
        sb.Append(dir);
        sb.Append(' ');
        foreach (var b in bytes)
        {
            sb.Append(EscapeByte(b));
            if (b == 0x0A)
                sb.Append('\n').Append(prefix).Append("  ");
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string EscapeByte(byte b) => b switch
    {
        0x00 => @"\0",
        0x07 => @"\a",
        0x08 => @"\b",
        0x09 => @"\t",
        0x0A => @"\n",
        0x0D => @"\r",
        0x1B => @"\e",
        >= 0x20 and <= 0x7E => ((char)b).ToString(),
        _ => $@"\x{b:X2}",
    };

    // ── Accumulated sequence ─────────────────────────────────────────────

    private void AppendByte(byte b)
    {
        _sequence.Add(b);
        RefreshSequenceDisplay();
    }

    private void RemoveLastByte()
    {
        if (_sequence.Count > 0)
        {
            _sequence.RemoveAt(_sequence.Count - 1);
            RefreshSequenceDisplay();
        }
    }

    private void RefreshSequenceDisplay()
    {
        if (_sequence.Count == 0)
        {
            _seqLabel.Text = "(press keys to build sequence — Backspace removes last byte)";
            _hexLabel.Text = string.Empty;
            return;
        }
        var esc = new StringBuilder(_sequence.Count * 3);
        foreach (var b in _sequence)
            esc.Append(EscapeByte(b));
        _seqLabel.Text = esc.ToString();
        _hexLabel.Text = string.Join(' ', _sequence.Select(b => $"{b:X2}"));
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void OnSendClicked(object? sender, EventArgs e)
    {
        if (_sequence.Count == 0) return;
        var bytes = _sequence.ToArray();
        _vm.SendRawBytes(bytes);
        _statusLabel.Text = $"sent {bytes.Length} byte{(bytes.Length == 1 ? "" : "s")}";
        _sequence.Clear();
        RefreshSequenceDisplay();
    }

    private void OnCancelTapped()
    {
        _sequence.Clear();
        RefreshSequenceDisplay();
        _statusLabel.Text = string.Empty;
    }

    private void OnMarkClicked(object? sender, EventArgs e)
    {
        _markCount++;
        var prefix = TimestampPrefix(_stopwatch.Elapsed.TotalSeconds);
        var label  = $"-- Mark {_markCount} ";
        var line   = $"\n{prefix}{label.PadRight(72, '-')}\n";
        _outputSb.Append(line);
        _outputEditor.Text = _outputSb.ToString();
        ScrollOutputToEnd();
        if (_vm.IsCapturing)
            _vm.Annotate($"Mark {_markCount}");
    }

    private void OnStopStartClicked(object? sender, EventArgs e)
    {
        _paused = !_paused;
        if (_paused)
        {
            _stopBtn.Text = "Start";
        }
        else
        {
            lock (_pendingLock)
            {
                _pendingOutput.Clear();
                _pendingDirty = false;
            }
            _outputSb.Clear();
            _outputEditor.Text = string.Empty;
            _stopBtn.Text = "Stop";
        }
    }

    private void OnClearOutputClicked(object? sender, EventArgs e)
    {
        lock (_pendingLock)
        {
            _pendingOutput.Clear();
            _pendingDirty = false;
        }
        _outputSb.Clear();
        _outputEditor.Text = string.Empty;
    }

    // ── Key capture (WinUI PreviewKeyDown on the window root) ─────────────

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(
        uint wVirtKey, uint wScanCode, byte[]? lpKeyState,
        StringBuilder pwszBuff, int cchBuff, uint wFlags);

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool ctrl = (GetKeyState((int)Windows.System.VirtualKey.Control) & 0x8000) != 0;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                AppendByte(0x1B);
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Tab:
                AppendByte(0x09);
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Enter:
                if ((GetKeyState((int)Windows.System.VirtualKey.Shift) & 0x8000) != 0)
                    AppendByte(0x0D);
                AppendByte(0x0A);
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Back:
                RemoveLastByte();
                e.Handled = true;
                return;
        }

        if (ctrl)
        {
            // Ctrl+A…Z → SOH…SUB (0x01…0x1A)
            if (e.Key >= Windows.System.VirtualKey.A && e.Key <= Windows.System.VirtualKey.Z)
            {
                AppendByte((byte)((int)e.Key - (int)Windows.System.VirtualKey.A + 1));
                e.Handled = true;
            }
            return;
        }

        var keyState = new byte[256];
        GetKeyboardState(keyState);
        var scan = MapVirtualKey((uint)e.Key, 0 /* MAPVK_VK_TO_VSC */);
        var buf  = new StringBuilder(8);
        int result = ToUnicode((uint)e.Key, scan, keyState, buf, 8, 0);
        if (result > 0)
        {
            for (int i = 0; i < result; i++)
            {
                char c = buf[i];
                if (c is >= '\x20' and <= '\xFF')
                    AppendByte((byte)c);
            }
            e.Handled = true;
        }
    }
}
#endif
