// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stingray.Core.Format;
using Stingray.Gui.Localization;

namespace Stingray.Gui.ViewModels;

/// <summary>
/// One line of the mod tree: either a folder, or a bundle that can be optimised.
/// </summary>
/// <remarks>
/// Mods package their variants as folders — one mod holding "Shorty" and
/// "Long boi" — so a flat list cannot say which mod a name belongs to. The tree
/// is the folder tree, because that is the only structure the files actually
/// carry.
/// </remarks>
public sealed partial class ModNodeViewModel : ObservableObject
{
    private bool _updatingFromChild;

    public ModNodeViewModel(string name, ModNodeViewModel? parent, DiscoveredBundle? bundle = null)
    {
        Name = name;
        Parent = parent;
        Bundle = bundle;
    }

    public string Name { get; }
    public ModNodeViewModel? Parent { get; }

    /// <summary>The bundle this line stands for, or null when it is only a folder.</summary>
    public DiscoveredBundle? Bundle { get; private set; }

    public ObservableCollection<ModNodeViewModel> Children { get; } = [];

    public bool IsBundle => Bundle is not null;
    public bool HasChildren => Children.Count > 0;

    /// <summary>Every bundle at or beneath this line.</summary>
    public IEnumerable<ModNodeViewModel> Bundles =>
        IsBundle ? [this] : Children.SelectMany(c => c.Bundles);

    /// <summary>Bytes of GPU data at or beneath this line.</summary>
    public long GpuSize => IsBundle ? Bundle!.GpuSize : Children.Sum(c => c.GpuSize);

    /// <summary>
    /// What a line says about itself: its size, plus the file name when a folder
    /// holds more than one bundle and the name alone would be ambiguous.
    /// </summary>
    public string Detail
    {
        get
        {
            var size = Format.Bytes(GpuSize);
            if (!IsBundle)
                return Bundles.Count() is var n && n == 1 ? size : S.BundleCount(n, size);

            var stream = Bundle!.StreamSize > 0
                ? $" + {Format.Bytes(Bundle.StreamSize)} {S.StreamedSuffix}"
                : string.Empty;
            return ShowFileName ? $"{Bundle.FileName} · {size}{stream}" : $"{size}{stream}";
        }
    }

    /// <summary>Set when a folder holds several bundles, which then need telling apart.</summary>
    public bool ShowFileName { get; set; }

    /// <summary>Whether this line's bundles are queued for optimising.</summary>
    [ObservableProperty] private bool? _isChecked = false;

    /// <summary>
    /// Folded away to begin with. A real mod manager folder holds a couple of
    /// hundred bundles, and opening all of them at once is not a list anyone can
    /// read; the mod names are what you are looking through.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Marks the bundle whose plan is on screen.</summary>
    [ObservableProperty] private bool _isOpen;

    partial void OnIsCheckedChanged(bool? value)
    {
        // Half-ticked means "some of the things under this", and a bundle has
        // none. One left there would look chosen and not be counted as chosen,
        // so there is no such state for a bundle to be in.
        if (value is null && Children.Count == 0)
        {
            IsChecked = false;
            return;
        }

        // A tick on a folder is a tick on everything under it, which is what
        // makes selecting a whole mod one click rather than one per option.
        if (!_updatingFromChild && value is { } state)
            foreach (var child in Children)
                child.IsChecked = state;

        Parent?.RefreshFromChildren();
    }

    /// <summary>
    /// Pulls a folder's state up from its children: all on, all off, or the
    /// indeterminate state that says "some of these".
    /// </summary>
    private void RefreshFromChildren()
    {
        if (Children.Count == 0) return;

        var checkedCount = Children.Count(c => c.IsChecked == true);
        var indeterminate = Children.Any(c => c.IsChecked is null);

        _updatingFromChild = true;
        IsChecked = indeterminate || (checkedCount > 0 && checkedCount < Children.Count)
            ? null
            : checkedCount == Children.Count;
        _updatingFromChild = false;
    }

    /// <summary>
    /// Re-reads this bundle's sizes from disk, and the totals of every folder
    /// above it.
    /// </summary>
    /// <remarks>
    /// The sizes come from the scan, so after a bundle is rewritten the panel
    /// still quotes what it used to cost — the one number the whole exercise
    /// was about, left saying the thing that is no longer true.
    /// </remarks>
    public void Refresh()
    {
        if (Bundle is not { } bundle) return;

        var gpu = new FileInfo(bundle.Path + ".gpu_resources");
        var stream = new FileInfo(bundle.Path + ".stream");
        Bundle = bundle with
        {
            GpuSize = gpu.Exists ? gpu.Length : 0,
            StreamSize = stream.Exists ? stream.Length : 0,
        };

        for (var node = this; node is not null; node = node.Parent)
        {
            node.OnPropertyChanged(nameof(GpuSize));
            node.OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>Builds the folder tree the scan implies.</summary>
    public static IReadOnlyList<ModNodeViewModel> Build(IEnumerable<DiscoveredBundle> bundles)
    {
        var roots = new List<ModNodeViewModel>();
        var folders = new Dictionary<string, ModNodeViewModel>(StringComparer.Ordinal);

        foreach (var bundle in bundles)
        {
            ModNodeViewModel? parent = null;
            var key = string.Empty;

            foreach (var folder in bundle.RelativeFolders)
            {
                key = key.Length == 0 ? folder : $"{key}/{folder}";
                if (!folders.TryGetValue(key, out var node))
                {
                    node = new ModNodeViewModel(folder, parent);
                    folders[key] = node;
                    if (parent is null) roots.Add(node); else parent.Children.Add(node);
                }

                parent = node;
            }

            var leaf = new ModNodeViewModel(bundle.FileName, parent, bundle);
            if (parent is null) roots.Add(leaf); else parent.Children.Add(leaf);
        }

        // A folder holding exactly one bundle and nothing else is the common
        // case, and showing it as a folder containing one line just adds a row
        // to click through. Fold the two together.
        foreach (var node in folders.Values)
        {
            if (node.Children is [{ IsBundle: true } only]) node.Absorb(only);
            else foreach (var child in node.Children.Where(c => c.IsBundle))
                child.ShowFileName = true;
        }

        return roots;
    }

    /// <summary>Takes on a lone child bundle so the folder becomes the bundle.</summary>
    private void Absorb(ModNodeViewModel only)
    {
        Bundle = only.Bundle;
        Children.Clear();
    }
}
