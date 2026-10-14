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

    /// <summary>Reads the saved step from mucka.ini off the UI thread and applies it on the UI thread.</summary>
    public static void LoadSavedStepInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var step = UiFontScale.Parse(await SettingsStore.GetGlobalValueAsync(UiFontScale.IniKey).ConfigureAwait(false));
                if (step != UiFontScale.Normal)
                    MainThread.BeginInvokeOnMainThread(() => Current.Step = step);
            }
            catch (Exception ex)
            {
                CrashLog.Write("UiFontScale", ex);
            }
        });
    }
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

    public BindingBase ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(UiFontScaleState.Step), BindingMode.OneWay,
            Tight ? UiFontScaleConverter.Tight : UiFontScaleConverter.Normal, Size,
            source: UiFontScaleState.Current);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
