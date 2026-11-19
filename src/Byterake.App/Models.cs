using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Byterake.Core;

namespace Byterake.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One row of the cleanup catalog on the Overview page.</summary>
public sealed class TargetRow : Observable
{
    public TargetRow(CatalogTarget t)
    {
        Target = t;
        _isChecked = t.TickedByDefault;
    }

    public CatalogTarget Target { get; }
    public string Id => Target.Id;
    public string Name => Target.Name;
    public string Description => Target.Description;
    public bool IsSafe => Target.Risk == Risk.Safe;
    public bool IsReview => Target.Risk == Risk.Review;
    public bool Admin => Target.Admin;
    public string AdminText => Target.Admin ? "Yes" : "No";
    public bool IsFileList => Target.Kind is TargetKind.Downloads or TargetKind.LargeFiles;
    public bool IsCheckable => !IsFileList && Available;
    public string PathsText => Target.Kind switch
    {
        TargetKind.RecycleBin => "Recycle Bin of the chosen drive",
        TargetKind.Downloads => $"Downloads: {string.Join(", ", Target.Extensions)} over {Target.MinSizeMB} MB",
        TargetKind.LargeFiles => "Anywhere on the drive, outside protected folders",
        _ => string.Join("  ·  ", Target.Paths.Take(3)) + (Target.Paths.Count > 3 ? $"  · +{Target.Paths.Count - 3} more" : ""),
    };

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) Changed?.Invoke(); } }

    bool _available = true;
    public bool Available
    {
        get => _available;
        set
        {
            if (!Set(ref _available, value)) return;
            if (!value && !IsFileList && _isChecked) { _isChecked = false; Raise(nameof(IsChecked)); }
            Raise(nameof(IsCheckable));
            Raise(nameof(RowOpacity));
        }
    }
    public double RowOpacity => Available ? 1 : 0.5;

    string _sizeText = "Measuring…";
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    string _note = "";
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    string _chooseText = "";
    public string ChooseText { get => _chooseText; set => Set(ref _chooseText, value); }

    public TargetMeasure? Measure { get; set; }
    public long Bytes { get; set; }
    public long Items { get; set; }

    /// <summary>Ticked files for per-file targets.</summary>
    public List<FileRow> Files { get; } = new();

    public long SelectedBytes => !Available ? 0 : IsFileList ? Files.Where(f => f.IsChecked).Sum(f => f.Size) : (IsChecked ? Bytes : 0);
    public long SelectedItems => !Available ? 0 : IsFileList ? Files.Count(f => f.IsChecked) : (IsChecked ? Items : 0);
    public bool IsSelected => IsFileList ? SelectedItems > 0 : IsChecked && Available;

    public event Action? Changed;

    public void RefreshFileState()
    {
        int ticked = Files.Count(f => f.IsChecked);
        _isChecked = ticked > 0;
        Raise(nameof(IsChecked));
        ChooseText = Files.Count == 0 ? "" : $"Choose files ({ticked} of {Files.Count} ticked)";
        SizeText = ticked > 0 ? Units.Bytes(SelectedBytes) : (Files.Count > 0 ? Units.Bytes(Files.Where(f => !f.IsLocked).Sum(f => f.Size)) : SizeText);
    }
}

/// <summary>A single file in a Review list (old installers, large files).</summary>
public sealed class FileRow : Observable
{
    public FileRow(string path, long size, DateTime modified, string? lockedReason = null)
    {
        Path = path;
        Size = size;
        Modified = modified;
        LockedReason = lockedReason;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public long Size { get; }
    public string SizeText => Units.Bytes(Size);
    public DateTime Modified { get; }
    public string ModifiedText => Modified.ToString("d MMM yyyy");
    public string? LockedReason { get; }
    public bool IsLocked => LockedReason != null;
    public bool IsUnlocked => LockedReason == null;
    public Visibility LockVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;
    public double RowOpacity => IsLocked ? 0.55 : 1;

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set { if (IsLocked) value = false; if (Set(ref _isChecked, value)) Changed?.Invoke(); } }
    public event Action? Changed;
}

/// <summary>A folder in the scan tree. Children are built only when the folder is opened.</summary>
public sealed class FolderRow
{
    readonly DirNode _node;
    readonly long _parentSize;
    List<object>? _children;

    public FolderRow(DirNode node, long parentSize)
    {
        _node = node;
        _parentSize = parentSize;
    }

    public string Name => _node.Name;
    public string Path => _node.Path;
    public string SizeText => _node.SkipReason != null ? "No access" : Units.Bytes(_node.Size);
    public double Percent => _parentSize <= 0 ? 0 : Math.Min(100, 100.0 * _node.Size / _parentSize);
    public GridLength BarStar => new(Math.Max(Percent, 0.001), GridUnitType.Star);
    public GridLength RestStar => new(Math.Max(100 - Percent, 0.001), GridUnitType.Star);
    public string PercentText => $"{Percent:0}%";
    public bool Protected => PathSafety.ContentsProtected(_node.Path);
    public Visibility LockVisibility => Protected ? Visibility.Visible : Visibility.Collapsed;

    public List<object> Children => _children ??= _node.Children
        .Take(100)
        .Select(c => (object)new FolderRow(c, _node.Size))
        .ToList();
}

/// <summary>Largest folders on the Overview page.</summary>
public sealed record TopFolder(string Path, string SizeText, GridLength BarStar, GridLength RestStar, bool Locked)
{
    public Visibility LockVisibility => Locked ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class ProgramRow
{
    public ProgramRow(InstalledProgram p) => Program = p;
    public InstalledProgram Program { get; }
    public string Name => Program.Name;
    public string Details => string.Join("  ·  ", new[] { Program.Publisher, Program.Version }.Where(s => s.Length > 0));
    public string SizeText => Program.SizeBytes > 0 ? Units.Bytes(Program.SizeBytes) : "—";
    public string DateText => Program.InstallDate?.ToString("d MMM yyyy") ?? "—";
}
