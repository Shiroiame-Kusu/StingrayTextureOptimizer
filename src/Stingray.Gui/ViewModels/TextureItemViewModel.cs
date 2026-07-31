// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Stingray.Core.Dds;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;

namespace Stingray.Gui.ViewModels;

/// <summary>Row model for one texture in the plan grid.</summary>
public sealed partial class TextureItemViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public TextureItemViewModel(TexturePlanItem item, Action onChanged)
    {
        Item = item;
        _onChanged = onChanged;
        _include = item.Include;
        _targetFormat = item.TargetFormat;
    }

    public TexturePlanItem Item { get; }

    public string Name => Item.Texture.Name;
    public string SourceFormat => Item.Texture.SourceFormat.DisplayName();
    public string Content => Item.Analysis.Summary;

    /// <summary>Measured cost of the planned resize, blank when none is planned.</summary>
    public string ResizeCost =>
        Item.TargetWidth == Item.Texture.Width || Item.Analysis.IsSolidColour
            ? string.Empty
            : Item.Analysis.DetailVerdict;
    public string Rationale => Item.Recommendation.Rationale;
    public long CurrentSize => Item.CurrentSize;

    public string Dimensions =>
        Item.TargetWidth == Item.Texture.Width && Item.TargetHeight == Item.Texture.Height
            ? $"{Item.Texture.Width}x{Item.Texture.Height}"
            : $"{Item.Texture.Width}x{Item.Texture.Height} → {Item.TargetWidth}x{Item.TargetHeight}";

    public IReadOnlyList<DxgiFormat> AvailableFormats => TextureEncoder.SupportedTargets;

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
        OnPropertyChanged(nameof(IsRiskyOverride));
        _onChanged();
    }
}
