#if UIKIT
using Foundation;
using UIKit;

namespace Mucka.Pages;

// The command box on a Mac and on iOS. It is built the way the Windows one is (Mucka.Input between the native
// field and everything else), not left on the Entry's two-way binding: that binding writes the view
// model on every keystroke, which is the round-trip the typing path is designed to avoid.
public partial class GamePage
{
    // Enter's key code as Mucka.Input is told it; the router treats codes as opaque, and Enter is
    // only ever passed with isAcceptKey set.
    private const int MacEnterKeyCode = 13;

    private UITextField? _macInputField;
    private Mucka.Input.CommandInput? _macCommandInput;
#if MACCATALYST
    private UITapGestureRecognizer? _macFocusBackstop;
#endif

    private void SetUpMacInput()
    {
        InputEntry.HandlerChanged += OnMacInputHandlerChanged;
        OnMacInputHandlerChanged(InputEntry, EventArgs.Empty);

        // The play button reads the native field like Enter does, instead of the XAML command.
        SendButton.Command = null;
        SendButton.Clicked -= OnMacSendClicked;
        SendButton.Clicked += OnMacSendClicked;

#if MACCATALYST
        InstallMacFocusBackstop();
#endif
    }

    private void OnMacInputHandlerChanged(object? sender, EventArgs e)
    {
        if (_macInputField is not null)
        {
            _macInputField.ShouldReturn = null!;
            _macInputField.ShouldChangeCharacters = null!;
        }
        _macInputField = InputEntry.Handler?.PlatformView as UITextField;
        if (_macInputField is not { } tf)
            return;

        // Typed text never touches the view model: it is read from the field on Enter, and the view
        // model writes to the field only through explicit requests (history, clear, a clicked name).
        InputEntry.ReturnCommand = null;
        InputEntry.RemoveBinding(Entry.TextProperty);
        // Commands go out as typed: no curly quotes, no em dashes, no autocorrect or capitals.
        tf.SmartQuotesType = UITextSmartQuotesType.No;
        tf.SmartDashesType = UITextSmartDashesType.No;
        tf.SmartInsertDeleteType = UITextSmartInsertDeleteType.No;
        tf.AutocorrectionType = UITextAutocorrectionType.No;
        tf.AutocapitalizationType = UITextAutocapitalizationType.None;
        tf.SpellCheckingType = UITextSpellCheckingType.No;
        if (OperatingSystem.IsMacCatalystVersionAtLeast(17) || OperatingSystem.IsIOSVersionAtLeast(17))
            tf.InlinePredictionType = UITextInlinePredictionType.No;
        tf.ShouldChangeCharacters = KeepTypedCharacters;
        tf.Text = _vm.InputText;
        // Synchronous, on the key itself: the field must be read and emptied before the next
        // keystroke is processed. False so UIKit does not also resign or insert a newline.
        tf.ShouldReturn = _ =>
        {
            AcceptMacInputLine();
            return false;
        };

        _macCommandInput ??= new Mucka.Input.CommandInput(
            new MacCommandInputSurface(() => _macInputField, FocusInput), _vm.InputGate);
    }

    // Commands go out as typed. Typing "..." has been seen to arrive as a single ellipsis, which
    // breaks runs of dots in a movement or a command; any substitution of that kind is written back
    // as the plain characters instead.
    private static bool KeepTypedCharacters(UITextField field, NSRange range, string replacement)
    {
        if (replacement.AsSpan().IndexOfAny(SubstitutedCharacters) < 0)
            return true;

        var plain = replacement
            .Replace("\u2026", "...", StringComparison.Ordinal)
            .Replace('\u2013', '-').Replace('\u2014', '-')
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"');
        var current = new NSString(field.Text ?? string.Empty);
        field.Text = current.Replace(range, new NSString(plain));
        var caret = field.GetPosition(field.BeginningOfDocument, (nint)(range.Location + plain.Length));
        if (caret is not null)
            field.SelectedTextRange = field.GetTextRange(caret, caret);
        return false;
    }

    private static readonly System.Buffers.SearchValues<char> SubstitutedCharacters =
        System.Buffers.SearchValues.Create(['\u2026', '\u2013', '\u2014', '\u2018', '\u2019', '\u201C', '\u201D']);

    private void OnMacSendClicked(object? sender, EventArgs e) => AcceptMacInputLine();

    private void AcceptMacInputLine()
    {
        if (_macCommandInput is not null)
            _macCommandInput.HandleKey(MacEnterKeyCode, Mucka.Input.InputModifiers.None, isAcceptKey: true);
        else
            _vm.SendCommand.Execute(null);
    }

#if MACCATALYST
    // -- Invariant #0 on a Mac ------------------------------------------------------------------
    // UIKit does not move the keyboard to a control that is clicked unless it is a text input, so the
    // command box keeps it through clicks on buttons, the menu and the panels. What does lose it is
    // anything that takes the responder chain for a while - an alert or action sheet, a modal page
    // closing, the window being activated - and those are the cases the refocus below covers: after
    // any click on the page, and whenever the page appears or the window is activated.

    private void InstallMacFocusBackstop()
    {
        if (_macFocusBackstop is not null || Handler?.PlatformView is not UIView root)
            return;
        _macFocusBackstop = new UITapGestureRecognizer(() =>
        {
            if (!KeyboardOwnedElsewhere)
                FocusInput();
        })
        {
            CancelsTouchesInView = false,
            DelaysTouchesBegan = false,
            DelaysTouchesEnded = false,
            ShouldRecognizeSimultaneously = (_, _) => true,
        };
        root.AddGestureRecognizer(_macFocusBackstop);
    }
#endif
}
#endif
