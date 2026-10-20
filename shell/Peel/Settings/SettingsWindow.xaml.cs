// The settings window: opened from the tray (or by starting Peel.exe again
// while it runs). It edits a copy of SettingsStore.Current and saves it whole.
//
// No {Binding} anywhere: the values are put in and read out in code, which
// keeps the window free of WPF's culture lookups and easy to read top to
// bottom. Which wheels exist and what they are called comes from the engine's
// registry, handed in as rows, so the window knows no family names either.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Input = System.Windows.Input.Keyboard;

namespace Peel.Settings;

/// <summary>A wheel the user can rebind: the engine's modifier name for it and its label.</summary>
internal readonly record struct WheelRow(string EngineKey, string Label);

public partial class SettingsWindow : Window
{
    private readonly Dictionary<string, ComboBox> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<bool> _pauseHotkey;
    private string _hotkey;

    internal SettingsWindow(IReadOnlyList<WheelRow> wheels, Action<bool> pauseHotkey)
    {
        InitializeComponent();
        _pauseHotkey = pauseHotkey;
        var settings = SettingsStore.Current;
        _hotkey = settings.Hotkey;

        foreach (var wheel in wheels)
        {
            AddWheelRow(wheel, settings.KeyForWheel(wheel.EngineKey));
        }

        HotkeyBox.Text = _hotkey;
        HotkeyBox.PreviewKeyDown += OnHotkeyKey;
        HotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            HotkeyBox.Text = "Press the new shortcut...";
            _pauseHotkey(true);
        };
        HotkeyBox.LostKeyboardFocus += (_, _) =>
        {
            HotkeyBox.Text = _hotkey;
            _pauseHotkey(false);
        };
        HotkeyReset.Click += (_, _) =>
        {
            _hotkey = HotkeyChord.Default.ToString();
            HotkeyBox.Text = _hotkey;
        };

        var toFolder = settings.Output == PeelSettings.OutputFolderMode;
        OutputSource.IsChecked = !toFolder;
        OutputFolder.IsChecked = toFolder;
        FolderBox.Text = settings.OutputFolder;
        FolderBox.GotKeyboardFocus += (_, _) => OutputFolder.IsChecked = true;
        ChooseFolder.Click += (_, _) => PickFolder();

        foreach (var name in QualityPresets.Names)
        {
            QualityBox.Items.Add(new ComboBoxItem { Content = QualityPresets.Label(name), Tag = name });
        }
        QualityBox.SelectedIndex = Math.Max(0, QualityPresets.Names.ToList().IndexOf(settings.Quality));

        StartBox.IsChecked = settings.StartWithWindows;

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
        Closed += (_, _) => _pauseHotkey(false);
        Loaded += (_, _) => Log.Write($"settings window shown, {wheels.Count} wheel row(s)");
    }

    private void AddWheelRow(WheelRow wheel, string key)
    {
        var row = WheelRows.RowDefinitions.Count;
        WheelRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock
        {
            Text = $"{wheel.Label} wheel opens with",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 3),
        };
        var box = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
        System.Windows.Automation.AutomationProperties.SetName(box, $"{wheel.Label} wheel key");
        foreach (var name in PeelSettings.ModifierKeys)
        {
            box.Items.Add(name);
        }
        box.SelectedItem = PeelSettings.ModifierKeys.FirstOrDefault(name =>
            string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) ?? wheel.EngineKey;
        Grid.SetRow(label, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        WheelRows.Children.Add(label);
        WheelRows.Children.Add(box);
        _keys[wheel.EngineKey] = box;
    }

    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Input.Modifiers;
        // Tab and Escape on their own keep their usual meaning: move on, or
        // close the window.
        if (modifiers == ModifierKeys.None && key is Key.Tab or Key.Escape)
        {
            return;
        }
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }
        var win = Input.IsKeyDown(Key.LWin) || Input.IsKeyDown(Key.RWin);
        var chord = HotkeyChord.FromPress(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift),
            win,
            (uint)KeyInterop.VirtualKeyFromKey(key));
        if (chord is null)
        {
            HotkeyBox.Text = "Use Ctrl, Alt or Win with a letter, number or key";
            return;
        }
        _hotkey = chord.Value.ToString();
        HotkeyBox.Text = _hotkey;
    }

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where Peel writes its output",
        };
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && System.IO.Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            OutputFolder.IsChecked = true;
        }
    }

    /// <summary>The settings as the window shows them now.</summary>
    internal PeelSettings Collect()
    {
        var modifiers = PeelSettings.DefaultModifiers();
        foreach (var pair in _keys)
        {
            if (pair.Value.SelectedItem is string key)
            {
                modifiers[pair.Key] = key;
            }
        }
        return SettingsStore.Current with
        {
            Modifiers = modifiers,
            Hotkey = _hotkey,
            Output = OutputFolder.IsChecked == true ? PeelSettings.OutputFolderMode : PeelSettings.OutputNextToSource,
            OutputFolder = FolderBox.Text.Trim(),
            Quality = (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? QualityPresets.Balanced,
            StartWithWindows = StartBox.IsChecked == true,
        };
    }

    private void Save()
    {
        var settings = Collect();
        var problem = settings.Problem();
        if (problem is null && settings.Output == PeelSettings.OutputFolderMode
            && !System.IO.Directory.Exists(settings.OutputFolder))
        {
            problem = $"The folder {settings.OutputFolder} does not exist.";
        }
        problem ??= StartupEntry.Set(settings.StartWithWindows, Environment.ProcessPath);
        problem ??= SettingsStore.Save(settings);
        if (problem is not null)
        {
            ProblemText.Text = problem;
            ProblemText.Visibility = Visibility.Visible;
            Log.Write($"settings not saved: {problem}");
            return;
        }
        Close();
    }
}
