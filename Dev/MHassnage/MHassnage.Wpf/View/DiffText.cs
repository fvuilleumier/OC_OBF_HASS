using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using MHassnage.Core.Model;

namespace MHassnage.Wpf.View;

/// <summary>Couleurs du diff, partagées par les converters et DiffText.</summary>
internal static class DiffColors
{
    public static readonly Brush Removed = Frozen("#4E2A2E");
    public static readonly Brush Added = Frozen("#2E4A33");
    public static readonly Brush RemovedWord = Frozen("#8C3A44");
    public static readonly Brush AddedWord = Frozen("#3F7A4C");
    public static readonly Brush Empty = Frozen("#20242A");

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Fond d'une moitié de ligne ; ConverterParameter = « old » ou « new ».</summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNew = "new".Equals(parameter);
        return value switch
        {
            DiffLineKind.Deleted => DiffColors.Removed,
            DiffLineKind.Inserted => DiffColors.Added,
            DiffLineKind.Modified => isNew ? DiffColors.Added : DiffColors.Removed,
            DiffLineKind.Empty => DiffColors.Empty,
            _ => Brushes.Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Remplit un TextBlock avec les segments d'une ligne : mots modifiés surlignés
/// (rouge côté ancien, vert côté nouveau, selon DiffText.IsNew).
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached(
        "Side", typeof(DiffSide), typeof(DiffText), new PropertyMetadata(null, Update));

    public static readonly DependencyProperty IsNewProperty = DependencyProperty.RegisterAttached(
        "IsNew", typeof(bool), typeof(DiffText), new PropertyMetadata(false, Update));

    public static DiffSide? GetSide(DependencyObject d) => (DiffSide?)d.GetValue(SideProperty);
    public static void SetSide(DependencyObject d, DiffSide? value) => d.SetValue(SideProperty, value);
    public static bool GetIsNew(DependencyObject d) => (bool)d.GetValue(IsNewProperty);
    public static void SetIsNew(DependencyObject d, bool value) => d.SetValue(IsNewProperty, value);

    private static void Update(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (GetSide(block) is not { } side) return;

        var highlight = GetIsNew(block) ? DiffColors.AddedWord : DiffColors.RemovedWord;
        foreach (var segment in side.Segments)
        {
            var run = new Run(segment.Text);
            if (segment.Changed) run.Background = highlight;
            block.Inlines.Add(run);
        }
    }
}

/// <summary>Numéro de ligne ou vide.</summary>
public sealed class LineNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n ? n.ToString(culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
