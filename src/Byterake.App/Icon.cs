using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Byterake.App;

/// <summary>A 24x24 stroke icon drawn from path data. Takes its colour from the surrounding text.</summary>
public sealed class Icon : Viewbox
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Icon));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Icon()
    {
        Width = 18;
        Height = 18;
        Focusable = false;
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetBinding(Path.DataProperty, new Binding(nameof(Data)) { Source = this });
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        Child = canvas;
    }
}
