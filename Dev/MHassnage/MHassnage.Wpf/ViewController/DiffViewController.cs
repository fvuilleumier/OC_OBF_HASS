using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;

namespace MHassnage.Wpf.ViewController;

/// <summary>Case à cocher d'un bloc : retenu ou non dans le prochain commit.</summary>
public sealed partial class HunkToggle(int index, bool isChecked, Action<int, bool> changed) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(Index, value);
}

/// <summary>Ligne affichée : ligne du diff + case du bloc (sur les en-têtes de bloc, si sélectionnable).</summary>
public sealed record DiffRowItem(DiffRow Row, HunkToggle? Toggle);

/// <summary>Diff d'un fichier tel qu'affiché par DiffView, avec la sélection des blocs.</summary>
public sealed class DiffViewController
{
    public DiffViewController(FileDiff diff, ChangeItem? owner)
    {
        Diff = diff;

        // Sélection par bloc : seulement pour un fichier modifié dont les blocs sont fiables
        bool selectable = owner is not null && owner.Change.Kind == ChangeKind.Modified && diff.HunkCount > 0;
        if (selectable) owner!.HunkCount = diff.HunkCount;

        var toggles = new Dictionary<int, HunkToggle>();
        Rows = diff.Rows.Select(row =>
        {
            if (!selectable || !row.IsHunkHeader) return new DiffRowItem(row, null);
            var toggle = new HunkToggle(row.Hunk, !owner!.ExcludedHunks.Contains(row.Hunk), owner.SetHunk);
            toggles[row.Hunk] = toggle;
            return new DiffRowItem(row, toggle);
        }).ToList();
    }

    public FileDiff Diff { get; }
    public IReadOnlyList<DiffRowItem> Rows { get; }
}
