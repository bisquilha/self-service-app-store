using System.Windows;
using System.Windows.Controls;

namespace Loja.App;

/// <summary>
/// Painel dos cartões: todos com a mesma altura e a mesma largura, em quantas colunas couberem
/// (a largura sobrando é dividida entre as colunas, então não sobra faixa vazia na lateral).
/// </summary>
public sealed class CardPanel : Panel
{
    public static readonly DependencyProperty MinCardWidthProperty =
        DependencyProperty.Register(nameof(MinCardWidth), typeof(double), typeof(CardPanel),
            new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CardHeightProperty =
        DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(CardPanel),
            new FrameworkPropertyMetadata(204d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(CardPanel),
            new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinCardWidth { get => (double)GetValue(MinCardWidthProperty); set => SetValue(MinCardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); set => SetValue(CardHeightProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private int Columns(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinCardWidth + Gap)));

    private double CardWidth(double width, int columns) => Math.Max(1, (width - Gap * (columns - 1)) / columns);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinCardWidth : available.Width;
        var columns = Columns(width);
        var cardSize = new Size(CardWidth(width, columns), CardHeight);
        foreach (UIElement child in InternalChildren) child.Measure(cardSize);

        var count = InternalChildren.Count;
        var rows = count == 0 ? 0 : (count + columns - 1) / columns;
        var height = rows == 0 ? 0 : rows * CardHeight + (rows - 1) * Gap;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cardWidth = CardWidth(finalSize.Width, columns);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            InternalChildren[i].Arrange(new Rect(column * (cardWidth + Gap), row * (CardHeight + Gap), cardWidth, CardHeight));
        }
        return finalSize;
    }
}
