// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Stingray.Core.Dds;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Stingray.Gui.Localization;

namespace Stingray.Gui.ViewModels;

/// <summary>A target format with a readable label for the dropdown.</summary>
public sealed record FormatChoice(DxgiFormat Value)
{
    public override string ToString() => Value.DisplayName();
}

/// <summary>Row model for one texture in the plan grid.</summary>
public sealed partial class TextureItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public TextureItemViewModel(TexturePlanItem item, Action onChanged,
                                string modName = "", string modOwner = "")
    {
        Item = item;
        _onChanged = onChanged;
        _include = item.Include;
        _targetFormat = item.TargetFormat;
        ModName = modName;
        ModOwner = modOwner;
    }

    /// <summary>
    /// Which bundle this row came from. Only worth anything when several were
    /// analysed at once, and then it is the only thing that says which is which.
    /// </summary>
    public string ModName { get; }

    /// <summary>The mod that bundle belongs to, empty when it is one in its own right.</summary>
    public string ModOwner { get; }

    /// <summary>
    /// The mod, which is what you are looking for when scanning the column: an
    /// option's own name is meaningless without it, and a bundle that is a mod
    /// in its own right is the answer already.
    /// </summary>
    public string ModTitle => ModOwner.Length > 0 ? ModOwner : ModName;

    /// <summary>Which option of that mod, when it has options to tell apart.</summary>
    public string ModDetail => ModOwner.Length > 0 ? ModName : string.Empty;

    public bool HasModDetail => ModDetail.Length > 0;

    /// <summary>Both in full, since the column is too narrow for either.</summary>
    public string ModTooltip => HasModDetail ? $"{ModTitle} › {ModDetail}" : ModTitle;

    public TexturePlanItem Item { get; }

    public string Name => Item.Texture.Name;
    public string SourceFormat => Item.Texture.SourceFormat.DisplayName();
    public string Content => S.Content(Item.Analysis);

    /// <summary>Measured cost of the planned resize, blank when none is planned.</summary>
    public string ResizeCost =>
        Item.TargetWidth == Item.Texture.Width || Item.Analysis.IsSolidColour
            ? string.Empty
            : S.DetailVerdict(Item.Analysis.DetailHeadroomDb);
    public string Rationale => Item.Recommendation.Rationale;
    public long CurrentSize => Item.CurrentSize;

    /// <summary>
    /// Square textures are written as a single number: "4096x4096 → 2048x2048"
    /// is mostly redundant and does not fit the column.
    /// </summary>
    public string Dimensions
    {
        get
        {
            var square = Item.Texture.Width == Item.Texture.Height;
            var from = square ? $"{Item.Texture.Width}²" : $"{Item.Texture.Width}×{Item.Texture.Height}";
            if (Item.TargetWidth == Item.Texture.Width && Item.TargetHeight == Item.Texture.Height)
                return from;

            var targetSquare = Item.TargetWidth == Item.TargetHeight;
            var to = targetSquare ? $"{Item.TargetWidth}²" : $"{Item.TargetWidth}×{Item.TargetHeight}";
            return $"{from} → {to}";
        }
    }

    public IReadOnlyList<FormatChoice> AvailableFormats { get; } =
        [.. TextureEncoder.SupportedTargets.Select(f => new FormatChoice(f))];

    /// <summary>Readable name for the grid; the enum's own ToString is not one.</summary>
    public string TargetFormatName => Item.TargetFormat.DisplayName();

    public FormatChoice SelectedFormat
    {
        get => new(TargetFormat);
        set => TargetFormat = value.Value;
    }

    /// <summary>Whether the format dropdown applies to this row at all.</summary>
    public bool SupportsFormatChange => Item.SupportsFormatChange;

    public string FormatChangeHint =>
        Item.SupportsFormatChange
            ? S.FormatChangeFree
            : S.FormatChangeFixed(Item.Texture.MipMapCount);

    [ObservableProperty]
    private bool _include;

    [ObservableProperty]
    private DxgiFormat _targetFormat;

    public long PredictedSize => Item.PredictedSize;
    public long Saving => Item.Saving;

    // Formatted here rather than through converters: the grid is the only consumer
    // and this keeps the XAML free of converter plumbing.
    public string CurrentSizeText => Format.Bytes(CurrentSize);
    public string PredictedSizeText => Format.Bytes(PredictedSize);
    public string SavingText => Format.Bytes(Saving);

    /// <summary>Flags a manual choice that undoes the analyser's recommendation.</summary>
    public bool IsRiskyOverride =>
        Include && !Item.IsRecommended && Item.Recommendation.IsLossless;

    partial void OnIncludeChanged(bool value)
    {
        Item.Include = value;
        Refresh();
    }

    partial void OnTargetFormatChanged(DxgiFormat value)
    {
        Item.TargetFormat = value;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(PredictedSize));
        OnPropertyChanged(nameof(Saving));
        OnPropertyChanged(nameof(PredictedSizeText));
        OnPropertyChanged(nameof(SavingText));
        OnPropertyChanged(nameof(Dimensions));
        OnPropertyChanged(nameof(ResizeCost));
        OnPropertyChanged(nameof(TargetFormatName));
        OnPropertyChanged(nameof(SelectedFormat));
        OnPropertyChanged(nameof(IsRiskyOverride));
        _onChanged();
    }
}
