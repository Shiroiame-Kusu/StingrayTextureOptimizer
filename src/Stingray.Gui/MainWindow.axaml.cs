// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Stingray.Gui.Localization;
using Stingray.Gui.ViewModels;

namespace Stingray.Gui;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
    }

    /// <summary>Renders this window to the path given by --screenshot.</summary>
    internal Task CaptureScreenshotAsync() => Screenshot.CaptureAsync(this, _viewModel);

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = S.OpenBundle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                // Bundles are named by content hash with a .patch_N suffix, so no
                // single glob covers them; offer both and fall back to everything.
                new FilePickerFileType("Stingray bundle") { Patterns = ["*.patch_*"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await _viewModel.LoadAsync(path);
    }

    private async void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = S.PickAFolder,
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await _viewModel.ScanAsync(path);
    }

    private async void OnOptimizeClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.CheckedBundles.Count > 1)
        {
            await OptimizeManyAsync();
            return;
        }

        var confirm = new ConfirmWindow(S.ConfirmHeading, S.ConfirmBody);

        if (await confirm.ShowDialog<bool>(this))
            await _viewModel.OptimizeCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Several bundles at once. Deliberately a different dialog: there is no plan
    /// on screen for these, so the confirmation has to say what will happen.
    /// </summary>
    private async Task OptimizeManyAsync()
    {
        var targets = _viewModel.CheckedBundles;
        var confirm = new ConfirmWindow(S.ConfirmBatchHeading(targets.Count), S.ConfirmBatchBody);

        if (await confirm.ShowDialog<bool>(this))
            await _viewModel.OptimizeManyAsync(targets);
    }
}
