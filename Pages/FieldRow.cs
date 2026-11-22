namespace Mucka.Pages;

/// <summary>
/// A setting's label and its field, the first two children. Side by side in a label column of
/// <see cref="LabelWidth"/> so the fields of consecutive rows line up; with <see cref="Stacked"/>
/// the label sits on the line above, the field the width of the row.
/// </summary>
public sealed class FieldRow : Grid
{
    private const double ColumnGapDp = 12;
    private const double StackedGapDp = 4;

    public static readonly BindableProperty StackedProperty = BindableProperty.Create(
        nameof(Stacked), typeof(bool), typeof(FieldRow), false,
        propertyChanged: (row, _, _) => ((FieldRow)row).Arrange());

    public static readonly BindableProperty LabelWidthProperty = BindableProperty.Create(
        nameof(LabelWidth), typeof(double), typeof(FieldRow), 160.0,
        propertyChanged: (row, _, _) => ((FieldRow)row).Arrange());

    /// <summary>The label goes above the field instead of beside it.</summary>
    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    /// <summary>The label column's width when <see cref="Stacked"/> is false.</summary>
    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    private bool? _arrangedStacked;
    private double _arrangedLabelWidth;

    public FieldRow()
    {
        ChildAdded += (_, _) => Arrange();
    }

    private void Arrange()
    {
        var stacked = Stacked;
        if (_arrangedStacked != stacked || _arrangedLabelWidth != LabelWidth)
        {
            _arrangedStacked = stacked;
            _arrangedLabelWidth = LabelWidth;
            ColumnDefinitions.Clear();
            RowDefinitions.Clear();
            if (stacked)
            {
                ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                ColumnSpacing = 0;
                RowSpacing = StackedGapDp;
            }
            else
            {
                ColumnDefinitions.Add(new ColumnDefinition(new GridLength(LabelWidth)));
                ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                ColumnSpacing = ColumnGapDp;
                RowSpacing = 0;
            }
        }

        if (Children.Count > 0 && Children[0] is BindableObject label)
        {
            SetRow(label, 0);
            SetColumn(label, 0);
        }
        if (Children.Count > 1 && Children[1] is BindableObject field)
        {
            SetRow(field, stacked ? 1 : 0);
            SetColumn(field, stacked ? 0 : 1);
        }
    }
}
