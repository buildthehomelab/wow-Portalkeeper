using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <param name="Points">What the character may spend: min(cap, points earned on the account), or on hardcore characters only for a hardcore one.</param>
/// <param name="Ranks">Its saved build: perk id to rank, only ranks above 0.</param>
/// <param name="PointsMissing">The answer didn't name the points (a save's answer then keeps the listing's).</param>
public sealed record LegacyCharacter(uint Guid, string Name, int Race, int Class, int Gender, int Level, bool Online,
    bool Hardcore, int Points, int Spent, IReadOnlyDictionary<string, int> Ranks, bool PointsMissing = false);

/// <param name="Row">0..3, top to bottom.</param>
/// <param name="Column">0..2, left to right.</param>
/// <param name="RequiresTreePoints">Points spent in the perk's tree (not counting this perk) before it can be raised.</param>
/// <param name="RequiresPerk">A perk that has to be at its max rank first, or empty.</param>
/// <param name="RankTexts">RankTexts[i] is what rank i+1 does.</param>
public sealed record LegacyPerk(string Id, string Name, string Icon, int MaxRank, int Row, int Column,
    int RequiresTreePoints, string RequiresPerk, string Description, IReadOnlyList<string> RankTexts);

public sealed record LegacyTree(string Id, string Name, string Icon, IReadOnlyList<LegacyPerk> Perks);

/// <param name="Done">Some character on the account did it.</param>
/// <param name="Hardcore">Some hardcore character did it: it counts for hardcore characters too.</param>
/// <param name="By">The first character that did it, or empty.</param>
/// <param name="At">When that was, or null.</param>
public sealed record LegacyChallenge(uint Id, string Category, string Name, string Description, bool Done, bool Hardcore,
    string By, DateTimeOffset? At);

/// <param name="EarnedTotal">Legacy points earned on the whole account.</param>
/// <param name="EarnedHardcore">The part of them earned on hardcore characters.</param>
public sealed record LegacyListing(int Cap, int EarnedTotal, int EarnedHardcore, IReadOnlyList<LegacyCharacter> Characters,
    IReadOnlyList<LegacyTree> Trees, IReadOnlyList<LegacyChallenge> Challenges, DateTimeOffset CachedAt);

public enum LegacySaveOutcome
{
    /// <summary>Saved; <see cref="LegacySaveResult.Character"/> is the character as the server has it now.</summary>
    Saved,
    /// <summary>The game server said no (409): in combat, over its points, a requirement not met. Nothing changed.</summary>
    Refused,
    /// <summary>The portal turned it away (400, 429, 503, no connection) before asking the game server. Nothing changed.</summary>
    NotTried,
    /// <summary>The login is no longer good (401).</summary>
    LoggedOut,
    /// <summary>No answer (502, a lost connection): it may or may not have been saved.</summary>
    Unknown,
}

/// <param name="Code">The game server's reason for a refusal (409: no_character, combat, points, locked, invalid, refused), or "closed" (503).</param>
public sealed record LegacySaveResult(LegacySaveOutcome Outcome, string Message, LegacyCharacter? Character = null, string? Code = null);

/// <summary>
/// Evermore fork: the realm's Legacy (the portal's api/launcher/legacy.php, in front of mod-legacy).
/// Challenges done anywhere on the account earn Legacy points; each character spends up to the cap
/// in three perk trees. The listing is asked for when the LEGACY page opens, after a save and on
/// REFRESH; a save sends the character's whole build and the server checks all of it.
/// </summary>
public sealed partial class LegacyService
{
    public const string Endpoint = "legacy.php";
    /// <summary>The cap when the server doesn't name one.</summary>
    public const int DefaultCap = 16;
    /// <summary>The highest rank a perk can have (the portal refuses more).</summary>
    public const int MaxRankLimit = 9;
    /// <summary>The most perks a build may name (the portal refuses more).</summary>
    public const int MaxRanksInBuild = 40;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public LegacyService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    [GeneratedRegex("^[a-z_]{1,40}$")]
    private static partial Regex PerkIdPattern();

    /// <summary>A perk id the server and the portal accept.</summary>
    public static bool IsPerkId(string? id) => id is not null && PerkIdPattern().IsMatch(id);

    /// <summary>
    /// The account's listing. Null when the realm has no Legacy (404). Throws
    /// <see cref="LauncherApiException"/> with the portal's message for anything else it refused
    /// (503: the game server can't be reached or Legacy is closed; 401: the login expired), and
    /// HttpRequestException / TaskCanceledException / JsonException when it couldn't be asked.
    /// </summary>
    public async Task<LegacyListing?> GetListingAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ListingBody>(cancellationToken)
            ?? throw new InvalidDataException("Legacy sent an empty answer.");
        return Parse(body);
    }

    /// <summary>
    /// Saves <paramref name="ranks"/> as the character's whole build (an empty one resets it). Never
    /// throws for an answer or a lost connection: the result says what happened.
    /// </summary>
    public async Task<LegacySaveResult> SaveAsync(LauncherSession session, uint characterGuid, IReadOnlyDictionary<string, int> ranks,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            using var request = Authorized(HttpMethod.Post, session.Token);
            request.Content = JsonContent.Create(new SaveRequest(characterGuid, BuildBody(ranks)));
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
        {
            return new LegacySaveResult(LegacySaveOutcome.NotTried, "Couldn't reach the realm. Nothing was saved; try again in a moment.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new LegacySaveResult(LegacySaveOutcome.Unknown,
                "The realm didn't answer. Your perks may or may not have been saved; the page shows what the realm has now.");
        }

        using (response)
        {
            SaveBody? body = null;
            try { body = await response.Content.ReadFromJsonAsync<SaveBody>(cancellationToken); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { }
            var message = (body?.Message ?? "").Trim();
            var error = (body?.Error ?? "").Trim();

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK when body?.Character is { Guid: > 0 } character:
                    return new LegacySaveResult(LegacySaveOutcome.Saved, message.Length > 0 ? message : "Legacy perks saved.", ParseCharacter(character));
                case HttpStatusCode.OK:
                    // A 200 without the character: not what the portal sends. Don't guess either way.
                    return new LegacySaveResult(LegacySaveOutcome.Unknown,
                        "The realm sent an answer Portalkeeper doesn't understand. The page shows what the realm has now.");
                case HttpStatusCode.Conflict:
                    return new LegacySaveResult(LegacySaveOutcome.Refused, RefusalMessage(error, message), null, error.Length > 0 ? error : null);
                case HttpStatusCode.Unauthorized:
                    return new LegacySaveResult(LegacySaveOutcome.LoggedOut, PortalMessage(error, message, "Your login has expired. Log in again."));
                case HttpStatusCode.TooManyRequests:
                    return new LegacySaveResult(LegacySaveOutcome.NotTried,
                        PortalMessage(error, message, "That's a lot of saves in a row. Wait a moment, then save again."));
                case HttpStatusCode.BadGateway:
                    return new LegacySaveResult(LegacySaveOutcome.Unknown,
                        PortalMessage(error, message, "The game server didn't answer. Your perks may or may not have been saved; the page shows what the realm has now."));
                case HttpStatusCode.ServiceUnavailable:
                    // 503's "error" is the player's text and "code" says "closed" (409's "error" is the code).
                    var closed = body?.Code == "closed" || error == "closed";
                    return new LegacySaveResult(LegacySaveOutcome.NotTried,
                        PortalMessage(error, message, closed ? RefusalMessage("closed", "") : "The realm can't be reached right now. Nothing was saved; try again in a minute."),
                        null, closed ? "closed" : null);
                case >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError:
                    return new LegacySaveResult(LegacySaveOutcome.NotTried,
                        PortalMessage(error, message, $"The realm answered {(int)response.StatusCode} {response.ReasonPhrase}. Nothing was saved."));
                default:
                    // Other 5xx (a proxy's 504) could have come after the game server was asked.
                    return new LegacySaveResult(LegacySaveOutcome.Unknown,
                        PortalMessage(error, message, $"The realm answered {(int)response.StatusCode} {response.ReasonPhrase}. Your perks may or may not have been saved."));
            }
        }
    }

    /// <summary>The build as the portal takes it: known-good ids, ranks 1..9, nothing at 0.</summary>
    public static Dictionary<string, int> BuildBody(IReadOnlyDictionary<string, int> ranks) =>
        ranks.Where(r => r.Value > 0 && IsPerkId(r.Key))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToDictionary(r => r.Key, r => Math.Min(r.Value, MaxRankLimit), StringComparer.Ordinal);

    /// <summary>The server's message for a refusal, or one for its code when it sent none.</summary>
    public static string RefusalMessage(string? code, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
        return (code ?? "").Trim() switch
        {
            "closed" => "Legacy is closed on the realm right now. Nothing was saved; try again later.",
            "no_character" => "That character isn't on your account any more.",
            "combat" => "That character is in combat. Save again once the fight is over.",
            "points" => "That build needs more Legacy points than the character has.",
            "locked" => "A perk in that build is still locked: its requirement isn't met.",
            "invalid" => "The realm didn't accept that build: a perk or rank it doesn't know.",
            _ => "The realm refused the build.",
        };
    }

    /// <summary>The portal's words: its message, else its error when that's a sentence rather than a code.</summary>
    private static string PortalMessage(string error, string message, string fallback) =>
        message.Length > 0 ? message
        : error.Length > 0 && !IsCode(error) ? error
        : error.Length > 0 && RefusalMessage(error, "") is var known && known != RefusalMessage("", "") ? known
        : fallback;

    private static bool IsCode(string text) => text.All(c => c is >= 'a' and <= 'z' or '_');

    private static LegacyListing Parse(ListingBody body)
    {
        var cap = body.Cap is > 0 and <= 1000 ? body.Cap.Value : DefaultCap;
        var total = Math.Max(0, body.Earned?.Total ?? 0);
        var hardcore = Math.Clamp(body.Earned?.Hardcore ?? 0, 0, total);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var trees = (body.Trees ?? []).OfType<TreeBody>()
            .Select((t, index) => new LegacyTree(
                string.IsNullOrWhiteSpace(t.Id) ? "tree" + index : t.Id.Trim(),
                (t.Name ?? "").Trim(), Icon(t.Icon),
                (t.Perks ?? []).OfType<PerkBody>().Where(p => IsPerkId(p.Id) && seen.Add(p.Id!)).Select(ParsePerk).ToArray()))
            .ToArray();
        var characters = (body.Characters ?? []).OfType<CharacterBody>().Where(c => c.Guid > 0)
            .Select(c => ParseCharacter(c, cap, total, hardcore)).ToArray();
        var challenges = (body.Challenges ?? []).OfType<ChallengeBody>()
            .Select(c => new LegacyChallenge(c.Id, (c.Category ?? "").Trim(), (c.Name ?? "").Trim(), (c.Description ?? "").Trim(),
                c.Done || c.Hardcore, c.Hardcore, (c.By ?? "").Trim(), Time(c.At)))
            .ToArray();
        var cachedAt = body.CachedAt ?? body.CachedAtSnake;
        return new LegacyListing(cap, total, hardcore, characters, trees, challenges,
            cachedAt is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(cachedAt.Value) : DateTimeOffset.UtcNow);
    }

    private static LegacyPerk ParsePerk(PerkBody p)
    {
        var max = Math.Clamp(p.MaxRank ?? 1, 1, MaxRankLimit);
        var texts = (p.Ranks ?? []).Select(t => (t ?? "").Trim()).Take(max).ToList();
        while (texts.Count < max) texts.Add("");
        var requires = (p.RequiresPerk ?? "").Trim();
        return new LegacyPerk(p.Id!, string.IsNullOrWhiteSpace(p.Name) ? p.Id! : p.Name.Trim(), Icon(p.Icon), max,
            Math.Clamp(p.Row, 0, 3), Math.Clamp(p.Column, 0, 2), Math.Max(0, p.RequiresTreePoints),
            IsPerkId(requires) && requires != p.Id ? requires : "", (p.Description ?? "").Trim(), texts);
    }

    private static LegacyCharacter ParseCharacter(CharacterBody c) => ParseCharacter(c, null, null, null);

    /// <summary>
    /// A character; its points are the server's, kept within the cap. A character without them (an
    /// older server) gets what the rule gives: hardcore characters only count hardcore points.
    /// </summary>
    private static LegacyCharacter ParseCharacter(CharacterBody c, int? cap, int? total, int? hardcore)
    {
        var ranks = (c.Ranks ?? []).Where(r => IsPerkId(r.Key) && r.Value > 0)
            .ToDictionary(r => r.Key, r => Math.Min(r.Value, MaxRankLimit), StringComparer.Ordinal);
        var limit = cap ?? DefaultCap;
        var points = c.Points is int given ? Math.Clamp(given, 0, cap ?? int.MaxValue)
            : Math.Min(limit, Math.Max(0, (c.Hardcore ? hardcore : total) ?? 0));
        return new LegacyCharacter(c.Guid, (c.Name ?? "").Trim(), c.Race, c.Class, c.Gender, c.Level, c.Online, c.Hardcore,
            points, ranks.Values.Sum(), ranks, c.Points is null);
    }

    private static string Icon(string? icon) =>
        string.Concat((icon ?? "").Trim().ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'));

    private static DateTimeOffset? Time(long at) =>
        at is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(at) : null;

    private HttpRequestMessage Authorized(HttpMethod method, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<LauncherApiException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        SaveBody? body = null;
        try { body = await response.Content.ReadFromJsonAsync<SaveBody>(cancellationToken); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        var message = PortalMessage((body?.Error ?? "").Trim(), (body?.Message ?? "").Trim(), "");
        return new LauncherApiException(response.StatusCode, message.Length > 0
            ? message
            : $"Legacy answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private sealed record CharacterBody(
        [property: JsonPropertyName("guid")] uint Guid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("class")] int Class,
        [property: JsonPropertyName("gender")] int Gender,
        [property: JsonPropertyName("level")] int Level,
        [property: JsonPropertyName("online")] bool Online,
        [property: JsonPropertyName("hardcore")] bool Hardcore,
        [property: JsonPropertyName("points")] int? Points,
        [property: JsonPropertyName("spent")] int? Spent,
        [property: JsonPropertyName("ranks")] Dictionary<string, int>? Ranks);
    private sealed record PerkBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("maxRank")] int? MaxRank,
        [property: JsonPropertyName("row")] int Row,
        [property: JsonPropertyName("column")] int Column,
        [property: JsonPropertyName("requiresTreePoints")] int RequiresTreePoints,
        [property: JsonPropertyName("requiresPerk")] string? RequiresPerk,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("ranks")] string?[]? Ranks);
    private sealed record TreeBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("icon")] string? Icon,
        [property: JsonPropertyName("perks")] PerkBody?[]? Perks);
    private sealed record ChallengeBody(
        [property: JsonPropertyName("id")] uint Id,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("done")] bool Done,
        [property: JsonPropertyName("hardcore")] bool Hardcore,
        [property: JsonPropertyName("by")] string? By,
        [property: JsonPropertyName("at")] long At);
    private sealed record EarnedBody(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("hardcore")] int Hardcore);
    private sealed record ListingBody(
        [property: JsonPropertyName("cap")] int? Cap,
        [property: JsonPropertyName("earned")] EarnedBody? Earned,
        [property: JsonPropertyName("characters")] CharacterBody?[]? Characters,
        [property: JsonPropertyName("trees")] TreeBody?[]? Trees,
        [property: JsonPropertyName("challenges")] ChallengeBody?[]? Challenges,
        [property: JsonPropertyName("cachedAt")] long? CachedAt,
        [property: JsonPropertyName("cached_at")] long? CachedAtSnake);
    private sealed record SaveRequest(
        [property: JsonPropertyName("character")] uint Character,
        [property: JsonPropertyName("ranks")] Dictionary<string, int> Ranks);
    private sealed record SaveBody(
        [property: JsonPropertyName("character")] CharacterBody? Character,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("code")] string? Code);
}

/// <summary>
/// The Legacy rules the server checks, for the launcher's draft: a perk is raised only below its
/// max rank, with a point left, with <see cref="LegacyPerk.RequiresTreePoints"/> spent in its tree
/// (its own ranks don't count) and, when it names one, <see cref="LegacyPerk.RequiresPerk"/> at max
/// rank. A rank is taken back only when no other perk's requirement it meets would break: the draft
/// refuses rather than cascading, so what's on screen is always what the player picked.
/// </summary>
public static class LegacyRules
{
    public static int Spent(IReadOnlyDictionary<string, int> ranks) => ranks.Values.Where(v => v > 0).Sum();

    public static int Rank(IReadOnlyDictionary<string, int> ranks, string perkId) =>
        ranks.TryGetValue(perkId, out var rank) ? Math.Max(0, rank) : 0;

    /// <summary>What's spent in the tree, leaving <paramref name="except"/> out (empty: all of it).</summary>
    public static int TreeSpent(LegacyTree tree, IReadOnlyDictionary<string, int> ranks, string except = "") =>
        tree.Perks.Where(p => p.Id != except).Sum(p => Rank(ranks, p.Id));

    public static LegacyPerk? FindPerk(IReadOnlyList<LegacyTree> trees, string id) =>
        trees.SelectMany(t => t.Perks).FirstOrDefault(p => p.Id == id);

    public static LegacyTree? TreeOf(IReadOnlyList<LegacyTree> trees, LegacyPerk perk) =>
        trees.FirstOrDefault(t => t.Perks.Contains(perk));

    /// <summary>The perk's requirements hold for the build (its rank and points aside).</summary>
    public static bool RequirementsMet(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk)
    {
        var tree = TreeOf(trees, perk);
        if (tree is not null && TreeSpent(tree, ranks, perk.Id) < perk.RequiresTreePoints) return false;
        if (perk.RequiresPerk.Length == 0) return true;
        var required = FindPerk(trees, perk.RequiresPerk);
        // A requirement on a perk the realm doesn't list can't be met.
        return required is not null && Rank(ranks, required.Id) >= required.MaxRank;
    }

    /// <summary>Why a rank can't be added, or null when it can.</summary>
    public static string? AddBlocker(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk, int points)
    {
        if (Rank(ranks, perk.Id) >= perk.MaxRank) return "Max rank";
        var tree = TreeOf(trees, perk);
        if (tree is not null && TreeSpent(tree, ranks, perk.Id) < perk.RequiresTreePoints)
            return $"Requires {perk.RequiresTreePoints} {PointWord(perk.RequiresTreePoints)} in {TreeName(tree)}";
        if (perk.RequiresPerk.Length > 0)
        {
            var required = FindPerk(trees, perk.RequiresPerk);
            if (required is null) return "Requires a perk the realm doesn't list";
            if (Rank(ranks, required.Id) < required.MaxRank) return $"Requires {required.Name} at {required.MaxRank}/{required.MaxRank}";
        }
        if (Spent(ranks) >= points) return points <= 0 ? "No Legacy points to spend" : "No points left";
        return null;
    }

    public static bool CanAdd(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk, int points) =>
        AddBlocker(trees, ranks, perk, points) is null;

    /// <summary>
    /// The perks whose requirements hold now and wouldn't once a rank of <paramref name="perk"/> is
    /// taken back. Empty: the rank can go. (Perks already broken, after the realm changed a tree,
    /// don't hold anything up, so a broken build can always be taken apart.)
    /// </summary>
    public static IReadOnlyList<LegacyPerk> BrokenByRemoving(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk)
    {
        if (Rank(ranks, perk.Id) <= 0) return [];
        var after = With(ranks, perk.Id, Rank(ranks, perk.Id) - 1);
        return trees.SelectMany(t => t.Perks)
            .Where(p => p.Id != perk.Id && Rank(ranks, p.Id) > 0 && RequirementsMet(trees, ranks, p) && !RequirementsMet(trees, after, p))
            .ToArray();
    }

    /// <summary>Why a rank can't be taken back, or null when it can.</summary>
    public static string? RemoveBlocker(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk)
    {
        if (Rank(ranks, perk.Id) <= 0) return "No rank to take back";
        var broken = BrokenByRemoving(trees, ranks, perk);
        return broken.Count == 0 ? null : $"{string.Join(", ", broken.Select(p => p.Name))} {(broken.Count == 1 ? "needs" : "need")} it; take {(broken.Count == 1 ? "that" : "those")} back first";
    }

    public static bool CanRemove(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, LegacyPerk perk) =>
        RemoveBlocker(trees, ranks, perk) is null;

    /// <summary>What's wrong with the build as a whole, as the server would see it, or null when it can be saved.</summary>
    public static string? BuildProblem(IReadOnlyList<LegacyTree> trees, IReadOnlyDictionary<string, int> ranks, int points)
    {
        foreach (var (id, rank) in ranks)
        {
            if (rank <= 0) continue;
            var perk = FindPerk(trees, id);
            if (perk is null) return "The build has a perk the realm no longer lists. Clear it and pick again.";
            if (rank > perk.MaxRank) return $"{perk.Name} is above its max rank ({perk.MaxRank}).";
            if (!RequirementsMet(trees, ranks, perk)) return $"{perk.Name}'s requirement isn't met any more.";
        }
        var spent = Spent(ranks);
        if (spent > points) return $"The build spends {spent} points and the character has {points}. Take {spent - points} back.";
        return null;
    }

    /// <summary>A copy of the build with one perk at a new rank (0 drops it).</summary>
    public static Dictionary<string, int> With(IReadOnlyDictionary<string, int> ranks, string perkId, int rank)
    {
        var copy = ranks.Where(r => r.Value > 0).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        if (rank > 0) copy[perkId] = rank;
        else copy.Remove(perkId);
        return copy;
    }

    public static bool SameBuild(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        var left = a.Where(r => r.Value > 0).ToArray();
        var right = b.Where(r => r.Value > 0).ToArray();
        return left.Length == right.Length && left.All(r => b.TryGetValue(r.Key, out var rank) && rank == r.Value);
    }

    private static string TreeName(LegacyTree tree) => tree.Name.Length > 0 ? tree.Name : "this tree";
    private static string PointWord(int count) => count == 1 ? "point" : "points";
}
