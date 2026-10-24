using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>
/// Evermore fork: the LEGACY page. Challenges done on the account earn Legacy points; the picked
/// character spends up to the cap in three perk trees (<see cref="LegacyService"/>). Picking is a
/// draft: nothing reaches the realm until SAVE, which sends the whole build. UNDO goes back to what
/// the realm has, CLEAR ALL empties the draft (saved only with SAVE), and switching character or
/// leaving the page with changes asks first. A refusal ("in combat") is shown and the draft stays.
/// The rules the draft follows are <see cref="LegacyRules"/>, the server's own.
/// </summary>
public sealed class LegacyViewModel : ViewModelBase, IDisposable
{
    private readonly LegacyService _service;
    private readonly LauncherSession _session;
    private readonly Func<string, Task<Bitmap?>>? _loadIcon;
    private readonly Dictionary<string, Task<Bitmap?>> _icons = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closed = new();
    private readonly List<LegacyChallenge> _challenges = [];
    private IReadOnlyList<LegacyTree> _trees = [];
    private Dictionary<string, int> _draft = new(StringComparer.Ordinal);
    private int _loadVersion;

    private bool _isLoading;
    private bool _isSaving;
    private bool _hasListing;
    private string _loadError = "";
    private string _statusMessage = "";
    private bool _statusIsError;
    private int _cap = LegacyService.DefaultCap;
    private int _earnedTotal;
    private int _earnedHardcore;
    private bool _showChallenges;
    private LegacyCharacterView? _selectedCharacter;
    private LegacyCategoryView? _selectedCategory;
    private LegacyCharacterView? _pendingCharacter;
    private LauncherPage? _pendingPage;
    private bool _pendingClose;

    /// <param name="loadIcon">Fetches a WoW icon by its file name (the armory's icon cache); null draws none.</param>
    public LegacyViewModel(LegacyService service, LauncherSession session, Func<string, Task<Bitmap?>>? loadIcon = null)
    {
        _service = service;
        _session = session;
        _loadIcon = loadIcon;
    }

    /// <summary>The portal says the login is no longer good (401), with its message.</summary>
    public event Action<string>? LoginExpired;
    /// <summary>The portal answered 404: the realm turned Legacy off.</summary>
    public event Action? Unavailable;
    /// <summary>The player chose to drop the draft and go to this page.</summary>
    public event Action<LauncherPage>? LeaveConfirmed;
    /// <summary>The player chose to drop the draft and close Portalkeeper.</summary>
    public event Action? CloseConfirmed;

    public ObservableCollection<LegacyCharacterView> Characters { get; } = [];
    public ObservableCollection<LegacyTreeView> Trees { get; } = [];
    public ObservableCollection<LegacyCategoryView> Categories { get; } = [];
    public ObservableCollection<LegacyChallengeView> Challenges { get; } = [];

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) NotifyBusy(); } }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) { NotifyBusy(); UpdateDraft(); } } }
    public bool IsBusy => _isLoading || _isSaving;
    public bool CanRefresh => !IsBusy;

    public bool HasListing { get => _hasListing; private set { if (SetProperty(ref _hasListing, value)) NotifyContent(); } }
    public string LoadError { get => _loadError; private set { if (SetProperty(ref _loadError, value)) NotifyContent(); } }
    public bool HasLoadError => _loadError.Length > 0 && !_hasListing;
    public bool ShowFirstLoad => _isLoading && !_hasListing && _loadError.Length == 0;
    public bool ShowLegacy => _hasListing;
    public bool HasCharacters => Characters.Count > 0;
    public bool HasNoCharacters => _hasListing && Characters.Count == 0;

    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool HasStatus => _statusMessage.Length > 0;

    // ---------------------------------------------------------
    // Tabs
    // ---------------------------------------------------------

    public bool ShowChallenges
    {
        get => _showChallenges;
        set
        {
            if (!SetProperty(ref _showChallenges, value)) return;
            OnPropertyChanged(nameof(ShowPerks));
            OnPropertyChanged(nameof(ShowPerkTools));
        }
    }
    public bool ShowPerks => !_showChallenges;
    /// <summary>SAVE, UNDO and CLEAR ALL: on the perks tab, with a character.</summary>
    public bool ShowPerkTools => !_showChallenges && _selectedCharacter is not null;

    public int ChallengesDone => _challenges.Count(c => c.Done);
    public int ChallengesTotal => _challenges.Count;
    public string ChallengesTabText => $"{ChallengesDone} / {ChallengesTotal}";

    public LegacyCategoryView? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value)) return;
            foreach (var category in Categories) category.IsSelected = ReferenceEquals(category, value);
            FilterChallenges();
        }
    }
    public bool HasNoChallenges => _hasListing && Challenges.Count == 0;

    // ---------------------------------------------------------
    // Points
    // ---------------------------------------------------------

    public int Cap => _cap;
    public int EarnedTotal => _earnedTotal;
    public int EarnedHardcore => _earnedHardcore;
    public IReadOnlyDictionary<string, int> Draft => _draft;
    public IReadOnlyDictionary<string, int> Saved => _selectedCharacter?.Character.Ranks ?? new Dictionary<string, int>();
    public int Spent => LegacyRules.Spent(_draft);
    /// <summary>What the picked character may spend: hardcore characters only have hardcore-earned points.</summary>
    public int Available => _selectedCharacter?.Character.Points ?? 0;
    public string PointsText => $"{Spent} / {Available}";
    public string PointsCaption => $"spent / available · cap {_cap}";
    public bool IsOverPoints => Spent > Available;
    public string EarnedText => (_earnedTotal == 1 ? "1 point earned on your account" : $"{_earnedTotal} points earned on your account")
        + (_earnedHardcore > 0 ? $" ({_earnedHardcore} on hardcore characters)" : "");
    public bool IsHardcore => _selectedCharacter?.IsHardcore == true;
    public string HardcoreNote => IsHardcore
        ? $"Hardcore: only the {_earnedHardcore} {(_earnedHardcore == 1 ? "point" : "points")} earned on hardcore characters count for {_selectedCharacter!.Name}."
        : "";

    public bool HasUnsavedChanges => _selectedCharacter is not null && !LegacyRules.SameBuild(_draft, Saved);
    /// <summary>What stops the draft from being saved, or empty.</summary>
    public string BuildProblem => _selectedCharacter is null ? "" : LegacyRules.BuildProblem(_trees, _draft, Available) ?? "";
    public bool HasBuildProblem => BuildProblem.Length > 0;
    public bool CanSave => HasUnsavedChanges && !HasBuildProblem && !IsBusy;
    public bool CanUndo => HasUnsavedChanges && !_isSaving;
    public bool CanClear => _selectedCharacter is not null && _draft.Count > 0 && !_isSaving;
    public bool CanEdit => _selectedCharacter is not null && !_isSaving;
    public string UnsavedText => HasUnsavedChanges ? "Unsaved changes" : "";

    // ---------------------------------------------------------
    // The character
    // ---------------------------------------------------------

    public LegacyCharacterView? SelectedCharacter => _selectedCharacter;

    /// <summary>
    /// Picks another character. With unsaved changes it asks first (<see cref="IsConfirmingDiscard"/>)
    /// and returns false: the picker goes back to the character that's still picked.
    /// </summary>
    public bool RequestCharacter(LegacyCharacterView? character)
    {
        if (character is null || ReferenceEquals(character, _selectedCharacter) || character.Guid == _selectedCharacter?.Guid) return true;
        if (_isSaving) return false;
        if (HasUnsavedChanges)
        {
            _pendingPage = null;
            _pendingClose = false;
            _pendingCharacter = character;
            NotifyDiscard();
            return false;
        }
        Select(character);
        return true;
    }

    /// <summary>The page is being left: true when it can go now, false when it asks first.</summary>
    public bool AskToLeave(LauncherPage page)
    {
        if (!HasUnsavedChanges) return true;
        _pendingCharacter = null;
        _pendingClose = false;
        _pendingPage = page;
        NotifyDiscard();
        return false;
    }

    /// <summary>The window is being closed: true when it can close now, false when it asks first.</summary>
    public bool AskToClose()
    {
        if (!HasUnsavedChanges) return true;
        _pendingCharacter = null;
        _pendingPage = null;
        _pendingClose = true;
        NotifyDiscard();
        return false;
    }

    public bool IsConfirmingDiscard => _pendingCharacter is not null || _pendingPage is not null || _pendingClose;
    public string DiscardTitle => "UNSAVED CHANGES";
    public string DiscardText => _selectedCharacter is null ? ""
        : _pendingCharacter is not null ? $"Discard {_selectedCharacter.Name}'s unsaved Legacy perks and switch to {_pendingCharacter.Name}?"
        : _pendingClose ? $"Discard {_selectedCharacter.Name}'s unsaved Legacy perks and close Portalkeeper?"
        : $"Discard {_selectedCharacter.Name}'s unsaved Legacy perks and leave the page?";

    public void CancelDiscard()
    {
        _pendingCharacter = null;
        _pendingPage = null;
        _pendingClose = false;
        NotifyDiscard();
    }

    public void ConfirmDiscard()
    {
        var character = _pendingCharacter;
        var page = _pendingPage;
        var close = _pendingClose;
        _pendingCharacter = null;
        _pendingPage = null;
        _pendingClose = false;
        NotifyDiscard();
        if (character is null && page is null && !close) return;
        ResetDraft();
        if (character is not null && Characters.Contains(character)) Select(character);
        if (page is { } leave) LeaveConfirmed?.Invoke(leave);
        if (close) CloseConfirmed?.Invoke();
    }


    private void Select(LegacyCharacterView character)
    {
        _selectedCharacter = character;
        _draft = Copy(character.Character.Ranks);
        SetStatus("", false);
        OnPropertyChanged(nameof(SelectedCharacter));
        OnPropertyChanged(nameof(IsHardcore));
        OnPropertyChanged(nameof(HardcoreNote));
        OnPropertyChanged(nameof(ShowPerkTools));
        UpdateDraft();
    }

    // ---------------------------------------------------------
    // The draft
    // ---------------------------------------------------------

    /// <summary>Left-click: one rank more, when the rules allow it.</summary>
    public bool Add(LegacyPerkView perk)
    {
        if (!CanEdit || !LegacyRules.CanAdd(_trees, _draft, perk.Perk, Available)) return false;
        _draft = LegacyRules.With(_draft, perk.Perk.Id, LegacyRules.Rank(_draft, perk.Perk.Id) + 1);
        UpdateDraft();
        return true;
    }

    /// <summary>Right-click: one rank less, unless another perk needs it (taken back first).</summary>
    public bool Remove(LegacyPerkView perk)
    {
        if (!CanEdit || !LegacyRules.CanRemove(_trees, _draft, perk.Perk)) return false;
        _draft = LegacyRules.With(_draft, perk.Perk.Id, LegacyRules.Rank(_draft, perk.Perk.Id) - 1);
        UpdateDraft();
        return true;
    }

    /// <summary>An empty build; saved only with SAVE.</summary>
    public void ClearAll()
    {
        if (!CanEdit) return;
        _draft = new(StringComparer.Ordinal);
        UpdateDraft();
    }

    /// <summary>Back to what the realm has for the character.</summary>
    public void ResetDraft()
    {
        if (_isSaving) return;
        _draft = Copy(Saved);
        UpdateDraft();
    }

    private void UpdateDraft()
    {
        foreach (var tree in Trees) tree.Update(_trees, _draft, Saved, Available, CanEdit);
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(Spent));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(PointsText));
        OnPropertyChanged(nameof(IsOverPoints));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedText));
        OnPropertyChanged(nameof(BuildProblem));
        OnPropertyChanged(nameof(HasBuildProblem));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanEdit));
    }

    // ---------------------------------------------------------
    // The portal
    // ---------------------------------------------------------

    /// <summary>Asks the portal for the listing. A save's message stays up when <paramref name="keepStatus"/>.</summary>
    public async Task LoadAsync(bool keepStatus = false)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        if (!keepStatus) SetStatus("", false);
        try
        {
            var listing = await _service.GetListingAsync(_session, _closed.Token);
            if (version != _loadVersion) return;
            if (listing is null)
            {
                LoadError = "This realm has no Legacy.";
                HasListing = false;
                Unavailable?.Invoke();
                return;
            }
            Apply(listing);
            LoadError = "";
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            if (version != _loadVersion) return;
            ShowLoadError(ex.Message, keepStatus);
            LoginExpired?.Invoke(ex.Message);
        }
        catch (LauncherApiException ex)
        {
            if (version == _loadVersion) ShowLoadError(ex.Message, keepStatus);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException
            or InvalidOperationException or NotSupportedException)
        {
            if (_closed.IsCancellationRequested || version != _loadVersion) return;
            ShowLoadError(UserErrorService.Format(ex, "Couldn't reach Legacy"), keepStatus);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private void ShowLoadError(string message, bool afterSave)
    {
        if (!_hasListing) LoadError = message;
        else if (afterSave && HasStatus) SetStatus(_statusMessage + " The page couldn't be refreshed: " + message, _statusIsError);
        else SetStatus(message, true);
    }

    /// <summary>SAVE: the whole draft for the picked character. A refusal keeps the draft.</summary>
    public async Task SaveAsync()
    {
        var character = _selectedCharacter;
        if (character is null || !CanSave) return;
        var build = LegacyService.BuildBody(_draft);
        IsSaving = true;
        SetStatus(build.Count == 0 ? $"Clearing {character.Name}'s perks..." : $"Saving {character.Name}'s perks...", false);
        LegacySaveResult result;
        try { result = await _service.SaveAsync(_session, character.Guid, build, _closed.Token); }
        catch (OperationCanceledException) { return; }
        finally { IsSaving = false; }

        switch (result.Outcome)
        {
            case LegacySaveOutcome.Saved when result.Character is { } saved:
                ApplySaved(saved);
                SetStatus(result.Message, false);
                return;
            case LegacySaveOutcome.LoggedOut:
                SetStatus(result.Message, true);
                LoginExpired?.Invoke(result.Message);
                return;
            case LegacySaveOutcome.NotTried:
                SetStatus(result.Message, true);
                return;
            case LegacySaveOutcome.Refused when result.Code is "combat":
                // Nothing else changed: try again after the fight, the draft is still here.
                SetStatus(result.Message, true);
                return;
            default:
                // Refused for points / locked / no_character, or no answer: what the realm has now, draft kept.
                SetStatus(result.Message, true);
                break;
        }
        if (_closed.IsCancellationRequested) return;
        await LoadAsync(keepStatus: true);
    }

    /// <summary>The character the server answered a save with: its build is now the saved one and the draft.</summary>
    private void ApplySaved(LegacyCharacter saved)
    {
        var index = Characters.ToList().FindIndex(c => c.Guid == saved.Guid);
        // An answer that doesn't name the points (null) keeps what the listing said.
        if (saved.PointsMissing && index >= 0) saved = saved with { Points = Characters[index].Character.Points, PointsMissing = false };
        var view = new LegacyCharacterView(saved);
        if (index >= 0) Characters[index] = view;
        else Characters.Add(view);
        if (_selectedCharacter?.Guid == saved.Guid) Select(view);
        else UpdateDraft();
    }

    private void SetStatus(string message, bool error)
    {
        StatusIsError = error;
        StatusMessage = message;
    }

    private void Apply(LegacyListing listing)
    {
        var selectedGuid = _selectedCharacter?.Guid;
        var keptDraft = HasUnsavedChanges ? _draft : null;

        _cap = listing.Cap;
        _earnedTotal = listing.EarnedTotal;
        _earnedHardcore = listing.EarnedHardcore;
        _trees = listing.Trees;

        Trees.Clear();
        foreach (var tree in listing.Trees)
        {
            var view = new LegacyTreeView(tree);
            Trees.Add(view);
            _ = LoadIconAsync(tree.Icon, bitmap => view.IconImage = bitmap);
            foreach (var perk in view.Perks) _ = LoadIconAsync(perk.Perk.Icon, bitmap => perk.IconImage = bitmap);
        }

        // Asked about mid-switch: the list is new, so is the question.
        _pendingCharacter = null;
        Characters.Clear();
        foreach (var character in listing.Characters
                     .OrderByDescending(c => c.Level).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Characters.Add(new LegacyCharacterView(character));

        _challenges.Clear();
        _challenges.AddRange(listing.Challenges);
        var selectedCategory = _selectedCategory?.Key;
        Categories.Clear();
        Categories.Add(new LegacyCategoryView(LegacyCategoryView.AllKey, "All challenges", ChallengesDone, ChallengesTotal));
        foreach (var group in _challenges.GroupBy(c => c.Category.Length > 0 ? c.Category : "Other"))
            Categories.Add(new LegacyCategoryView(group.Key, group.Key, group.Count(c => c.Done), group.Count()));
        _selectedCategory = null;
        SelectedCategory = Categories.FirstOrDefault(c => c.Key == selectedCategory) ?? Categories[0];

        HasListing = true;

        var same = Characters.FirstOrDefault(c => c.Guid == selectedGuid);
        _selectedCharacter = same ?? Characters.FirstOrDefault(c => c.Online) ?? Characters.FirstOrDefault();
        _draft = same is not null && keptDraft is not null ? keptDraft : Copy(Saved);
        if (selectedGuid is not null && same is null && keptDraft is not null)
            SetStatus("That character isn't on your account any more; its unsaved perks are gone.", true);

        OnPropertyChanged(nameof(SelectedCharacter));
        OnPropertyChanged(nameof(Cap));
        OnPropertyChanged(nameof(EarnedTotal));
        OnPropertyChanged(nameof(EarnedHardcore));
        OnPropertyChanged(nameof(EarnedText));
        OnPropertyChanged(nameof(PointsCaption));
        OnPropertyChanged(nameof(IsHardcore));
        OnPropertyChanged(nameof(HardcoreNote));
        OnPropertyChanged(nameof(ShowPerkTools));
        OnPropertyChanged(nameof(ChallengesDone));
        OnPropertyChanged(nameof(ChallengesTotal));
        OnPropertyChanged(nameof(ChallengesTabText));
        NotifyDiscard();
        UpdateDraft();
        NotifyContent();
    }

    private void FilterChallenges()
    {
        Challenges.Clear();
        var key = _selectedCategory?.Key ?? LegacyCategoryView.AllKey;
        // Not done first is how the game lists them too; each half in the realm's order.
        foreach (var challenge in _challenges
                     .Where(c => key == LegacyCategoryView.AllKey || (c.Category.Length > 0 ? c.Category : "Other") == key)
                     .OrderBy(c => c.Done))
            Challenges.Add(new LegacyChallengeView(challenge));
        OnPropertyChanged(nameof(HasNoChallenges));
    }

    private async Task LoadIconAsync(string icon, Action<Bitmap?> set)
    {
        if (_loadIcon is null || icon.Length == 0) return;
        if (!_icons.TryGetValue(icon, out var load)) _icons[icon] = load = _loadIcon(icon);
        Bitmap? bitmap;
        try { bitmap = await load; }
        catch (Exception) { bitmap = null; }
        // A failed load (no connection, a half-written cache file) is tried again at the next listing.
        if (bitmap is null)
        {
            if (_icons.TryGetValue(icon, out var cached) && ReferenceEquals(cached, load)) _icons.Remove(icon);
            return;
        }
        if (!_closed.IsCancellationRequested) set(bitmap);
    }

    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> ranks) =>
        ranks.Where(r => r.Value > 0).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

    private void NotifyDiscard()
    {
        OnPropertyChanged(nameof(IsConfirmingDiscard));
        OnPropertyChanged(nameof(DiscardText));
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(CanSave));
    }

    private void NotifyContent()
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowFirstLoad));
        OnPropertyChanged(nameof(ShowLegacy));
        OnPropertyChanged(nameof(HasCharacters));
        OnPropertyChanged(nameof(HasNoCharacters));
        OnPropertyChanged(nameof(HasNoChallenges));
    }

    public void Dispose()
    {
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();
        _closed.Dispose();
        foreach (var load in _icons.Values)
            if (load.IsCompletedSuccessfully) load.Result?.Dispose();
        _icons.Clear();
    }
}

public sealed class LegacyCharacterView(LegacyCharacter character)
{
    public LegacyCharacter Character { get; } = character;
    public uint Guid => Character.Guid;
    public string Name => Character.Name.Length > 0 ? Character.Name : "Character " + Character.Guid;
    public bool Online => Character.Online;
    public bool IsHardcore => Character.Hardcore;
    public string Detail => $"Level {Character.Level} {ArmoryNames.RaceName(Character.Race)} {ArmoryNames.ClassName(Character.Class)}";
    /// <summary>The saved build's points, as the picker shows them.</summary>
    public string PointsText => $"{Character.Spent} / {Character.Points}";
    public IBrush ClassBrush { get; } = ArmoryNames.ClassBrush(character.Class);
}

/// <summary>One perk tree: its perks on a grid of rows and three columns.</summary>
public sealed class LegacyTreeView : ViewModelBase
{
    public const int Columns = 3;
    private Bitmap? _iconImage;
    private int _spent;

    public LegacyTreeView(LegacyTree tree)
    {
        Tree = tree;
        Perks = tree.Perks.Select(p => new LegacyPerkView(p, Title)).ToArray();
        // Rows 0..3 at least; a cell is a perk or empty. Two perks on one cell (a realm's mistake)
        // aren't drawn over each other: the second goes in the next free cell.
        var rows = Math.Max(4, tree.Perks.Count == 0 ? 0 : tree.Perks.Max(p => p.Row) + 1);
        var cells = new LegacyPerkView?[rows * Columns];
        foreach (var perk in Perks)
        {
            var index = perk.Perk.Row * Columns + perk.Perk.Column;
            while (index < cells.Length && cells[index] is not null) index++;
            if (index >= cells.Length)
            {
                Array.Resize(ref cells, cells.Length + Columns);
            }
            cells[index] = perk;
        }
        Cells = cells.Select(p => new LegacyCellView(p)).ToArray();
    }

    public LegacyTree Tree { get; }
    public string Title => Tree.Name.Length > 0 ? Tree.Name : Tree.Id;
    public string TitleUpper => Title.ToUpperInvariant();
    public IReadOnlyList<LegacyPerkView> Perks { get; }
    public IReadOnlyList<LegacyCellView> Cells { get; }
    public Bitmap? IconImage { get => _iconImage; set => SetProperty(ref _iconImage, value); }
    public int Spent { get => _spent; private set { if (SetProperty(ref _spent, value)) OnPropertyChanged(nameof(SpentText)); } }
    public string SpentText => _spent == 1 ? "1 point" : $"{_spent} points";

    public void Update(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> draft, IReadOnlyDictionary<string, int> saved, int points, bool canEdit)
    {
        Spent = LegacyRules.TreeSpent(Tree, draft);
        foreach (var perk in Perks) perk.Update(trees, draft, saved, points, canEdit);
    }
}

public sealed class LegacyCellView(LegacyPerkView? perk)
{
    public LegacyPerkView? Perk { get; } = perk;
    public bool HasPerk => Perk is not null;
}

/// <summary>One perk's icon on its tree, with the draft's rank and what it would take to raise it.</summary>
public sealed class LegacyPerkView(LegacyPerk perk, string treeName) : ViewModelBase
{
    private static readonly IBrush AvailableBrush = Brush.Parse("#40FF40");
    private static readonly IBrush MaxedBrush = Brush.Parse("#FFD100");
    private static readonly IBrush LockedBrush = Brush.Parse("#9D9D9D");
    private static readonly IBrush MetBrush = Brush.Parse("#8EE68E");
    private static readonly IBrush UnmetBrush = Brush.Parse("#FF5555");
    private Bitmap? _iconImage;
    private int _rank;
    private int _savedRank;
    private bool _canAdd;
    private bool _canRemove;
    private string _addBlocker = "";
    private string _removeBlocker = "";
    private bool _requirementMet = true;

    public LegacyPerk Perk { get; } = perk;
    public string TreeName { get; } = treeName;
    public string Name => Perk.Name;
    public string Description => Perk.Description;
    public int MaxRank => Perk.MaxRank;
    public Bitmap? IconImage { get => _iconImage; set { if (SetProperty(ref _iconImage, value)) OnPropertyChanged(nameof(ShowInitial)); } }
    /// <summary>Without a picture the perk's first letter stands in.</summary>
    public bool ShowInitial => _iconImage is null;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public int Rank => _rank;
    public int SavedRank => _savedRank;
    public string RankText => $"{_rank}/{MaxRank}";
    public bool IsActive => _rank > 0;
    public bool IsMaxed => _rank >= MaxRank;
    /// <summary>Not picked and can't be: drawn dimmed.</summary>
    public bool IsDimmed => _rank == 0 && !_canAdd;
    public bool IsChanged => _rank != _savedRank;
    public bool CanAdd => _canAdd;
    public bool CanRemove => _canRemove;
    public IBrush RankBrush => IsMaxed ? MaxedBrush : _canAdd ? AvailableBrush : _rank > 0 ? MaxedBrush : LockedBrush;
    public IBrush FrameBrush => RankBrush;

    // The tooltip.
    public string RankLine => $"Rank {_rank}/{MaxRank}";
    public string CurrentRankText => _rank > 0 ? Perk.RankTexts.ElementAtOrDefault(_rank - 1) ?? "" : "";
    public bool HasCurrentRank => _rank > 0 && CurrentRankText.Length > 0;
    public string NextRankText => _rank < MaxRank ? Perk.RankTexts.ElementAtOrDefault(_rank) ?? "" : "";
    public bool HasNextRank => _rank < MaxRank && NextRankText.Length > 0;
    public string NextRankLabel => _rank == 0 ? "Rank 1:" : "Next rank:";
    /// <summary>"Requires 5 points in Adventure and Well Rested at 5/5", or empty.</summary>
    public string RequirementText => _requirementText;
    public bool HasRequirement => _requirementText.Length > 0;
    public IBrush RequirementBrush => _requirementMet ? MetBrush : UnmetBrush;
    /// <summary>Why it can't be raised (or lowered) right now, or empty.</summary>
    public string BlockerText => !_canAdd && !IsMaxed && _addBlocker.Length > 0 && !_addBlocker.StartsWith("Requires", StringComparison.Ordinal)
        ? _addBlocker
        : _rank > 0 && !_canRemove && _removeBlocker.Length > 0 ? _removeBlocker : "";
    public bool HasBlocker => BlockerText.Length > 0;
    public string HintText => "Left-click (or +) adds a rank · right-click (or −) takes one back";
    public string AutomationName => $"{Name}, rank {_rank} of {MaxRank}";

    private string _requirementText = "";

    public void Update(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> draft, IReadOnlyDictionary<string, int> saved, int points, bool canEdit)
    {
        _rank = LegacyRules.Rank(draft, Perk.Id);
        _savedRank = LegacyRules.Rank(saved, Perk.Id);
        _addBlocker = LegacyRules.AddBlocker(trees, draft, Perk, points) ?? "";
        _removeBlocker = LegacyRules.RemoveBlocker(trees, draft, Perk) ?? "";
        _canAdd = canEdit && _addBlocker.Length == 0;
        _canRemove = canEdit && _removeBlocker.Length == 0;
        _requirementMet = LegacyRules.RequirementsMet(trees, draft, Perk);
        _requirementText = Requirement(trees, Perk, TreeName);
        foreach (var name in new[]
                 {
                     nameof(Rank), nameof(SavedRank), nameof(RankText), nameof(IsActive), nameof(IsMaxed), nameof(IsDimmed),
                     nameof(IsChanged), nameof(CanAdd), nameof(CanRemove), nameof(RankBrush), nameof(FrameBrush), nameof(RankLine),
                     nameof(CurrentRankText), nameof(HasCurrentRank), nameof(NextRankText), nameof(HasNextRank), nameof(NextRankLabel),
                     nameof(RequirementText), nameof(HasRequirement), nameof(RequirementBrush), nameof(BlockerText), nameof(HasBlocker), nameof(AutomationName),
                 })
            OnPropertyChanged(name);
    }

    private static string Requirement(IReadOnlyList<LegacyTree> trees, LegacyPerk perk, string treeName)
    {
        var parts = new List<string>();
        if (perk.RequiresTreePoints > 0) parts.Add($"{perk.RequiresTreePoints} {(perk.RequiresTreePoints == 1 ? "point" : "points")} in {treeName}");
        if (perk.RequiresPerk.Length > 0)
            parts.Add(LegacyRules.FindPerk(trees, perk.RequiresPerk) is { } required
                ? $"{required.Name} at {required.MaxRank}/{required.MaxRank}"
                : "a perk the realm doesn't list");
        return parts.Count == 0 ? "" : "Requires " + string.Join(" and ", parts);
    }
}

/// <summary>A category of challenges, with how many of them the account has done.</summary>
public sealed class LegacyCategoryView(string key, string title, int done, int total) : ViewModelBase
{
    public const string AllKey = "\u0001all";
    private bool _isSelected;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public int Done { get; } = done;
    public int Total { get; } = total;
    public string CountText => $"{Done} / {Total}";
    public bool IsComplete => Total > 0 && Done == Total;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class LegacyChallengeView(LegacyChallenge challenge)
{
    public LegacyChallenge Challenge { get; } = challenge;
    public string Name => Challenge.Name.Length > 0 ? Challenge.Name : "Challenge " + Challenge.Id;
    public string Description => Challenge.Description;
    public bool Done => Challenge.Done;
    public bool NotDone => !Challenge.Done;
    public bool Hardcore => Challenge.Hardcore;
    public string Category => Challenge.Category;
    /// <summary>"Pawster · 24 Oct 2026", or empty when nobody has done it.</summary>
    public string ByText => !Challenge.Done ? ""
        : string.Join(" · ", new[]
        {
            Challenge.By,
            Challenge.At?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "",
        }.Where(s => s.Length > 0));
    public bool HasBy => ByText.Length > 0;
    public string HardcoreTip => "Done on a hardcore character: it counts for hardcore characters too.";
}
