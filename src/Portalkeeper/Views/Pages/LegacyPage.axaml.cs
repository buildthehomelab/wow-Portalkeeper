using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

/// <summary>
/// The LEGACY page of the main window; its content is the Legacy view model of the MainViewModel
/// it's bound to. A perk takes a rank with a left-click (or Enter, Space, +) and gives one back with
/// a right-click (or −, Delete, Backspace); the − / + beside it do the same. Ctrl+S saves.
/// </summary>
public partial class LegacyPage : UserControl
{
    private MainViewModel? _main;
    private LegacyViewModel? _legacy;
    private bool _syncingCharacter;

    public LegacyPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        // A Button only clicks on the left button; the right one bubbles up here, handled or not.
        AddHandler(PointerReleasedEvent, Perk_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, Page_KeyDown, RoutingStrategies.Tunnel);
    }

    private void Attach(MainViewModel? main)
    {
        if (_main is not null) _main.PropertyChanged -= MainChanged;
        _main = main;
        if (_main is not null) _main.PropertyChanged += MainChanged;
        AttachLegacy(_main?.Legacy);
    }

    private void MainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Legacy)) AttachLegacy(_main?.Legacy);
    }

    private void AttachLegacy(LegacyViewModel? legacy)
    {
        if (ReferenceEquals(_legacy, legacy)) return;
        if (_legacy is not null) _legacy.PropertyChanged -= LegacyChanged;
        _legacy = legacy;
        if (_legacy is not null) _legacy.PropertyChanged += LegacyChanged;
        SyncCharacter();
    }

    private void LegacyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LegacyViewModel.SelectedCharacter):
                SyncCharacter();
                break;
            case nameof(LegacyViewModel.IsConfirmingDiscard) when _legacy is { IsConfirmingDiscard: true }:
                // The question takes the keyboard: KEEP EDITING first, nothing behind it.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => KeepEditingButton.Focus());
                break;
            case nameof(LegacyViewModel.IsConfirmingDiscard):
                // Kept editing: the picker shows the character that's still picked.
                SyncCharacter();
                break;
        }
    }

    /// <summary>The picker follows the view model, never the other way round without asking it.</summary>
    private void SyncCharacter()
    {
        _syncingCharacter = true;
        try { CharacterBox.SelectedItem = _legacy?.SelectedCharacter; }
        finally { _syncingCharacter = false; }
    }

    private void Character_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingCharacter || _legacy is not { } legacy || CharacterBox.SelectedItem is not LegacyCharacterView character) return;
        // With unsaved changes the page asks first; meanwhile the picker shows the picked one.
        if (!legacy.RequestCharacter(character))
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncCharacter);
    }

    private static LegacyPerkView? PerkOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } button && button.Classes.Contains("perk")
            ? button.DataContext as LegacyPerkView
            : null;

    private void Perk_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy && (sender as Control)?.DataContext is LegacyPerkView perk) legacy.Add(perk);
    }

    private void Add_Click(object? sender, RoutedEventArgs e) => Perk_Click(sender, e);

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy && (sender as Control)?.DataContext is LegacyPerkView perk) legacy.Remove(perk);
    }

    private void Perk_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || _legacy is not { } legacy || PerkOf(e.Source) is not { } perk) return;
        legacy.Remove(perk);
        e.Handled = true;
    }

    private async void Page_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_legacy is not { } legacy) return;
        if (e.Key == Key.Escape && legacy.IsConfirmingDiscard)
        {
            legacy.CancelDiscard();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            e.Handled = true;
            if (!legacy.IsConfirmingDiscard) await legacy.SaveAsync();
            return;
        }
        if (PerkOf(e.Source) is not { } perk || legacy.IsConfirmingDiscard) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                legacy.Add(perk);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract or Key.Delete or Key.Back:
                legacy.Remove(perk);
                e.Handled = true;
                break;
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy) await legacy.LoadAsync(keepStatus: legacy.HasUnsavedChanges);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy) await legacy.SaveAsync();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => _legacy?.ResetDraft();

    private void Clear_Click(object? sender, RoutedEventArgs e) => _legacy?.ClearAll();

    private void PerksTab_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy) legacy.ShowChallenges = false;
    }

    private void ChallengesTab_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy) legacy.ShowChallenges = true;
    }

    private void Category_Click(object? sender, RoutedEventArgs e)
    {
        if (_legacy is { } legacy && (sender as Control)?.DataContext is LegacyCategoryView category) legacy.SelectedCategory = category;
    }

    private void KeepEditing_Click(object? sender, RoutedEventArgs e) => _legacy?.CancelDiscard();

    private void Discard_Click(object? sender, RoutedEventArgs e) => _legacy?.ConfirmDiscard();
}
