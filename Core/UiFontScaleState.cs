using System.ComponentModel;
using System.Globalization;
using Mucka.Ui;

namespace Mucka.Core;

/// <summary>
/// The live UI font step. Every <see cref="ScaledFontExtension"/> binding in the app observes
/// <see cref="Step"/>, so changing it restyles the chrome without a restart. Read and set on the
/// UI thread only.
/// </summary>
public sealed class UiFontScaleState : INotifyPropertyChanged
{
    public static UiFontScaleState Current { get; } = new();

    private int _step;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Step
    {
        get => _step;
        set
        {
            value = UiFontScale.Clamp(value);
            if (value == _step) return;
            _step = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Step)));
        }
    }

}

/// <summary>The side panel's width at the step: <see cref="Mucka.Combat.CombatRailResize.SidePanelWidthAt"/>
/// of <see cref="UiFontScale.PanelScale"/>.</summary>
public sealed class SidePanelWidthConverter : IValueConverter
{
    public static readonly SidePanelWidthConverter Instance = new();

    // Android and iOS keep the authored width: on a phone a panel widened with the step would take
    // the screen. Its text still scales and may wrap.
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
#if MOBILE
        => Mucka.Combat.CombatRailResize.SidePanelWidthDp;
#else
        => Mucka.Combat.CombatRailResize.SidePanelWidthAt(
            UiFontScale.PanelScale(value is int step ? step : UiFontScale.Normal));
#endif

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary><c>WidthRequest="{core:SidePanelWidth}"</c>: the side panel's width, live with the UI font step.</summary>
public sealed class SidePanelWidthExtension : IMarkupExtension<BindingBase>
{
    public BindingBase ProvideValue(IServiceProvider serviceProvider) => Create();

    /// <summary>The same binding, for views built in code.</summary>
    public static BindingBase Create()
        => new Binding(nameof(UiFontScaleState.Step), BindingMode.OneWay, SidePanelWidthConverter.Instance,
            source: UiFontScaleState.Current);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}

/// <summary>Multiplies the authored size passed as the converter parameter by the step's scale.</summary>
public sealed class UiFontScaleConverter(bool tight) : IValueConverter
{
    public static readonly UiFontScaleConverter Normal = new(false);
    public static readonly UiFontScaleConverter Tight = new(true);

    /// <summary>An authored size that is not a positive double yields UnsetValue (the control's
    /// default size) rather than a zero-size, invisible font.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => parameter is double size and > 0
            ? UiFontScale.Apply(size, value is int step ? step : UiFontScale.Normal, tight)
            : BindableProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <c>FontSize="{core:ScaledFont 11}"</c>: the authored size, times the UI font step's scale, live.
/// The one way chrome font sizes are written. <c>Tight=True</c> caps the scale at
/// <see cref="UiFontScale.TightMaxScale"/> for the status bar and F-key toolbar.
/// </summary>
[ContentProperty(nameof(Size))]
public sealed class ScaledFontExtension : IMarkupExtension<BindingBase>
{
    public double Size { get; set; }
    public bool Tight { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider) => ScaledFontBinding.For(Size, Tight);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}

/// <summary>
/// The same live binding as <see cref="ScaledFontExtension"/>, for pages built in code:
/// <c>label.SetBinding(Label.FontSizeProperty, ScaledFontBinding.For(12))</c>.
/// </summary>
public static class ScaledFontBinding
{
    public static BindingBase For(double size, bool tight = false)
        => new Binding(nameof(UiFontScaleState.Step), BindingMode.OneWay,
            tight ? UiFontScaleConverter.Tight : UiFontScaleConverter.Normal, size,
            source: UiFontScaleState.Current);

    /// <summary>Gives <paramref name="label"/> the authored <paramref name="size"/>, scaled live.</summary>
    public static Label Scaled(this Label label, double size, bool tight = false)
    {
        label.SetBinding(Label.FontSizeProperty, For(size, tight));
        return label;
    }
}
