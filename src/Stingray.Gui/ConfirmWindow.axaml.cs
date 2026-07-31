// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Stingray.Gui;

/// <summary>Modal confirmation for the one destructive action in the app.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => AvaloniaXamlLoader.Load(this);

    public ConfirmWindow(string heading, string body) : this()
    {
        this.FindControl<TextBlock>("HeadingText")!.Text = heading;
        this.FindControl<TextBlock>("BodyText")!.Text = body;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
