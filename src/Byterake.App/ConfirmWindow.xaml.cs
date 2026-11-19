using System.Windows;
using Byterake.Core;

namespace Byterake.App;

public sealed class ConfirmInfo
{
    public int Files { get; init; }
    public long FileBytes { get; init; }
    public long BinItems { get; init; }
    public long BinBytes { get; init; }
    public long FreeBefore { get; init; }
    public List<string> Lines { get; init; } = new();
    public string? ExtraNote { get; init; }
    public int TotalItems => Files + (int)BinItems;
    public long TotalBytes => FileBytes + BinBytes;
}

/// <summary>The one place a clean can start. Shows the exact count and size, then the removal mode.</summary>
public partial class ConfirmWindow : Window
{
    readonly ConfirmInfo _info;

    public CleanMode? Result { get; private set; }

    public ConfirmWindow(ConfirmInfo info)
    {
        InitializeComponent();
        _info = info;
        Headline.Text = $"Clean {info.TotalItems:N0} items, {Units.Bytes(info.TotalBytes)}?";
        FreeBefore.Text = Units.Bytes(info.FreeBefore);
        Lines.ItemsSource = info.Lines;
        if (info.ExtraNote != null)
        {
            ExtraNote.Text = info.ExtraNote;
            ExtraNoteBox.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => App.TitleBar(this);
        Update();
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded || Go != null) Update();
    }

    void Update()
    {
        if (Go == null || FreeAfter == null) return;
        bool recycle = ModeRecycle.IsChecked == true;
        // Emptying the bin frees space at once; recycled files keep using space until the bin is emptied.
        long after = _info.FreeBefore + _info.BinBytes + (recycle ? 0 : _info.FileBytes);
        FreeAfter.Text = Units.Bytes(after);
        BinNote.Text = recycle && _info.FileBytes > 0
            ? $"Recycled files still use space. Another {Units.Bytes(_info.FileBytes)} comes back when the Recycle Bin is emptied (Byterake offers this when it is done)."
            : _info.BinItems > 0 ? "Emptying the Recycle Bin is permanent." : "";
        Go.Content = recycle ? $"Move {_info.Files:N0} files to Recycle Bin" : $"Delete {_info.Files:N0} files permanently";
        if (_info.Files == 0) Go.Content = "Clean";
        Go.Style = (Style)FindResource(recycle ? "Primary" : "Danger");
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Go_Click(object sender, RoutedEventArgs e)
    {
        if (ModePermanent.IsChecked == true)
        {
            var again = Dialog.Ask(this, "Delete permanently?",
                $"{_info.TotalItems:N0} items ({Units.Bytes(_info.TotalBytes)}) will be deleted for good. They will not go to the Recycle Bin and cannot be restored.",
                ("no", "Go back", Tone.Ghost), ("yes", "Delete permanently", Tone.Danger));
            if (again != "yes") return;
            Result = CleanMode.Permanent;
        }
        else Result = CleanMode.RecycleBin;
        Close();
    }
}
