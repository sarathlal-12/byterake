using System.Windows;
using System.Windows.Controls;

namespace Byterake.App;

public enum Tone { Primary, Ghost, Danger }

/// <summary>A themed message box with any set of buttons. Returns the id of the button clicked, or null.</summary>
public partial class Dialog : Window
{
    string? _result;

    Dialog() => InitializeComponent();

    public static string? Ask(Window? owner, string title, string body, params (string Id, string Label, Tone Tone)[] buttons)
    {
        var d = new Dialog { Owner = owner };
        d.TitleText.Text = title;
        d.BodyText.Text = body;
        foreach (var (id, label, tone) in buttons)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)d.FindResource(tone switch { Tone.Primary => "Primary", Tone.Danger => "Danger", _ => "Ghost" }),
            };
            if (tone == Tone.Ghost && buttons.Length > 1 && id == buttons[0].Id) b.IsCancel = true;
            b.Click += (_, _) => { d._result = id; d.Close(); };
            d.Buttons.Children.Add(b);
        }
        // Safe default: focus the first (least destructive) button.
        d.Loaded += (_, _) =>
        {
            App.TitleBar(d);
            if (d.Buttons.Children.Count > 0) ((Button)d.Buttons.Children[0]).Focus();
        };
        d.ShowDialog();
        return d._result;
    }

    public static void Info(Window? owner, string title, string body) =>
        Ask(owner, title, body, ("ok", "OK", Tone.Primary));
}
