using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VoidCrewModLauncher.Controls;
using VoidCrewModLauncher.Core.Lobbies;
using VoidCrewModLauncher.Core.Mods;
using VoidCrewModLauncher.Views;

namespace VoidCrewModLauncher.Infrastructure;

public enum ButtonKind
{
    Normal,
    Primary,
    Confirm,
    Cancel,
}

public readonly record struct DialogButton(string Text, ButtonKind Kind = ButtonKind.Normal);

/// <summary>The launcher's pop-ups, drawn like the game's: dark grey, light-blue outline, cut corners.</summary>
public sealed class Dialogs
{
    public Window? Owner { get; set; }

    /// <summary>Shows a message with buttons; returns the index of the button pressed (-1 when closed).</summary>
    public int Ask(string title, string message, params DialogButton[] buttons)
    {
        var text = new TextBlock { Text = message, Style = (Style)Application.Current.Resources["Body"], MaxWidth = 560 };
        return Show(title, text, buttons);
    }

    public void Info(string title, string message) => Ask(title, message, new DialogButton("OK", ButtonKind.Primary));

    public void Error(string title, string message) => Ask(title, message, new DialogButton("OK", ButtonKind.Cancel));

    public bool Confirm(string title, string message, string yes, string no = "CANCEL") =>
        Ask(title, message, new DialogButton(yes, ButtonKind.Primary), new DialogButton(no)) == 0;

    /// <summary>The join dialog: what the lobby needs and what changes for this game. True to go ahead.</summary>
    public bool ConfirmJoin(JoinPlan plan, Func<JoinPlan, ResolvedJoin> resolve, Func<ResolvedJoin, bool> needsRestart, bool gameRunning)
    {
        var window = new JoinWindow(plan, resolve, needsRestart, gameRunning) { Owner = OwnerWindow() };
        return window.ShowDialog() == true;
    }

    /// <summary>The import dialog: which mods of another BepInEx folder to bring in, and how.</summary>
    public List<(ImportCandidate Candidate, bool FromThunderstore)>? PickImports(List<ImportCandidate> candidates, string folder)
    {
        var window = new ImportWindow(candidates, folder) { Owner = OwnerWindow() };
        return window.ShowDialog() == true ? window.Selection : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(OwnerWindow()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? start = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog(OwnerWindow()) == true ? dialog.FolderName : null;
    }

    private Window? OwnerWindow()
    {
        Window? owner = Owner ?? Application.Current?.MainWindow;
        return owner is { IsVisible: true } ? owner : null;
    }

    private int Show(string title, UIElement body, DialogButton[] buttons)
    {
        int result = -1;
        var window = CreateWindow(title, body, out StackPanel buttonRow);
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i].Text,
                Style = (Style)Application.Current.Resources[buttons[i].Kind switch
                {
                    ButtonKind.Primary => "PrimaryButton",
                    ButtonKind.Confirm => "ConfirmButton",
                    ButtonKind.Cancel => "CancelButton",
                    _ => "HexButton",
                }],
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 120,
                IsDefault = i == 0,
                IsCancel = i == buttons.Length - 1 && buttons.Length > 1,
            };
            button.Click += (_, _) =>
            {
                result = index;
                window.Close();
            };
            buttonRow.Children.Add(button);
        }
        window.Owner = OwnerWindow();
        window.ShowDialog();
        return result;
    }

    /// <summary>A borderless pop-up window in the game's style with a title, a body and a row for buttons.</summary>
    public static Window CreateWindow(string title, UIElement body, out StackPanel buttonRow)
    {
        var window = new Window();
        Decorate(window, title, body, out buttonRow);
        return window;
    }

    /// <summary>Turns <paramref name="window"/> into one of the launcher's pop-ups.</summary>
    public static void Decorate(Window window, string title, UIElement body, out StackPanel buttonRow)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = System.Windows.Media.Brushes.Transparent;
        window.ResizeMode = ResizeMode.NoResize;
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        window.Title = title;
        window.FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"];
        window.UseLayoutRounding = true;
        buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };

        var heading = new SpacedText { Value = title, Style = (Style)Application.Current.Resources["H1"], FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
        var rule = new Grid { Style = (Style)Application.Current.Resources["PanelRule"] };
        rule.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)Application.Current.Resources["Line"], VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        rule.Children.Add(new Border { Width = 44, Height = 3, Background = (System.Windows.Media.Brush)Application.Current.Resources["Orange"], HorizontalAlignment = HorizontalAlignment.Center });
        var content = new StackPanel { MinWidth = 380 };
        content.Children.Add(heading);
        content.Children.Add(rule);
        content.Children.Add(body);
        content.Children.Add(buttonRow);
        content.Children.Add(new ContentControl { Style = (Style)Application.Current.Resources["PanelFooter"] });

        var panel = new ShapeBorder { Style = (Style)Application.Current.Resources["PopupPanel"], Padding = new Thickness(26, 20, 26, 22), Margin = new Thickness(12), Child = content };
        panel.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 };
        panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        window.Content = panel;
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
    }

    /// <summary>A pop-up button in one of the game's looks.</summary>
    public static Button MakeButton(string text, ButtonKind kind)
    {
        return new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources[kind switch
            {
                ButtonKind.Primary => "PrimaryButton",
                ButtonKind.Confirm => "ConfirmButton",
                ButtonKind.Cancel => "CancelButton",
                _ => "HexButton",
            }],
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 120,
        };
    }
}
