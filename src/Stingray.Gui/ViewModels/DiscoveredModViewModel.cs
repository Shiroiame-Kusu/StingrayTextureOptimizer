// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Stingray.Core.Format;
using Stingray.Gui.Localization;

namespace Stingray.Gui.ViewModels;

/// <summary>One row of the scanned-mods list.</summary>
public sealed partial class DiscoveredModViewModel : ObservableObject
{
    public DiscoveredModViewModel(DiscoveredBundle bundle, int number)
    {
        Bundle = bundle;
        Number = number;
    }

    public DiscoveredBundle Bundle { get; }

    /// <summary>Position in the list, so a long list can be talked about.</summary>
    public int Number { get; }

    public string Path => Bundle.Path;
    public string Name => Bundle.ModName;

    /// <summary>
    /// The bundle file and what it costs. A mod can hold more than one bundle,
    /// so the file name is what tells two rows of the same mod apart.
    /// </summary>
    public string Detail => Bundle.StreamSize > 0
        ? $"{Bundle.FileName} · {Format.Bytes(Bundle.GpuSize)} + {Format.Bytes(Bundle.StreamSize)} {S.StreamedSuffix}"
        : $"{Bundle.FileName} · {Format.Bytes(Bundle.GpuSize)}";

    /// <summary>Whether this is the row whose bundle is currently open.</summary>
    [ObservableProperty] private bool _isOpen;
}
