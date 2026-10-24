using System.Net;
using System.Text;
using System.Text.Json;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.RuntimeTests;

// Evermore fork: Legacy (LegacyService, LegacyRules, LegacyViewModel) against a stand-in for the
// portal's legacy.php that answers what it's told to.
internal static partial class Program
{
    private const string LegacyListingJson = """
        {"cap":16,
         "earned":{"total":23,"hardcore":4},
         "characters":[
           {"guid":12,"name":"Pawster","race":1,"class":1,"gender":0,"level":60,"online":false,
            "hardcore":false,"points":16,"spent":9,"ranks":{"well_rested":5,"diplomat":3,"frequent_flier":1,"Bad-Id":2}},
           {"guid":14,"name":"Ironhide","race":3,"class":1,"gender":0,"level":30,"online":true,
            "hardcore":true,"points":4,"spent":0,"ranks":{}},
           {"guid":15,"name":"Oldhc","race":2,"class":3,"gender":0,"level":12,"online":false,"hardcore":true}],
         "trees":[
           {"id":"adventure","name":"Adventure","icon":"INV_Misc_Map_01",
            "perks":[
              {"id":"well_rested","name":"Well Rested","icon":"spell_nature_sleep","maxRank":5,"row":0,"column":1,
               "requiresTreePoints":0,"requiresPerk":"","description":"Rested experience builds faster.",
               "ranks":["4% faster.","8% faster.","12% faster.","16% faster.","20% faster."]},
              {"id":"diplomat","name":"Diplomat","icon":"inv_misc_note_02","maxRank":3,"row":1,"column":0,
               "requiresTreePoints":5,"requiresPerk":"","description":"Reputation comes faster.","ranks":["5%","10%","15%"]},
              {"id":"frequent_flier","name":"Frequent Flier","icon":"ability_mount_gryphon_01","maxRank":1,"row":1,"column":2,
               "requiresTreePoints":0,"requiresPerk":"well_rested","description":"Cheaper flights.","ranks":["Flights cost 25% less."]},
              {"id":"explorer","name":"Explorer","icon":"","maxRank":2,"row":2,"column":1,
               "requiresTreePoints":9,"requiresPerk":"diplomat","description":"","ranks":["One"]},
              {"id":"Bad-Id","name":"Broken","maxRank":1,"row":3,"column":0}]},
           {"id":"resourcefulness","name":"Resourcefulness","icon":"inv_misc_bag_10",
            "perks":[{"id":"thrifty","name":"Thrifty","icon":"inv_misc_coin_01","maxRank":12,"row":7,"column":5,"ranks":[]}]},
           {"id":"professions","name":"Professions","icon":"trade_engineering",
            "perks":[{"id":"gatherer","name":"Gatherer","icon":"trade_herbalism","maxRank":5,"row":0,"column":0,"ranks":["a","b","c","d","e"]},
                     {"id":"well_rested","name":"Duplicate","maxRank":1,"row":0,"column":1}]}],
         "challenges":[
           {"id":101,"category":"Classes","name":"Warrior Initiate","description":"Reach level 20 with a Warrior.","done":true,"hardcore":false,"by":"Pawster","at":1761300000},
           {"id":102,"category":"Classes","name":"Mage Initiate","description":"Reach level 20 with a Mage.","done":false,"hardcore":false,"by":"","at":0},
           {"id":201,"category":"Dungeons","name":"Deadmines","description":"Kill Van Cleef.","done":true,"hardcore":true,"by":"Ironhide","at":1761000000},
           {"id":301,"category":"","name":"Odd One","description":"","done":false,"hardcore":false,"by":"","at":0}],
         "cachedAt":1761400000}
        """;

    private static async Task LegacyChecks(Action<string, bool> check)
    {
        var api = new Uri("https://portal.test/api/launcher/");
        var session = new LauncherSession(FakeLegacyPortal.Token, "ALICE", DateTimeOffset.UtcNow.AddDays(30));
        LegacyService Service(FakeLegacyPortal portal) => new(api, new HttpClient(portal));
        LegacyViewModel Legacy(FakeLegacyPortal portal) => new(Service(portal), session);

        // The listing.
        var portal = new FakeLegacyPortal();
        var listing = await Service(portal).GetListingAsync(session);
        check("legacy: the listing is read: cap, earned points, characters, three trees, challenges",
            listing is { Cap: 16, EarnedTotal: 23, EarnedHardcore: 4, Characters.Count: 3, Trees.Count: 3, Challenges.Count: 4 }
            && listing.CachedAt == DateTimeOffset.FromUnixTimeSeconds(1761400000));
        check("legacy: the listing is asked for with the launcher login", portal.Authorized == 1 && portal.Unauthorized == 0);
        var pawster = listing!.Characters[0];
        check("legacy: a character is read with its points and saved build",
            pawster is { Guid: 12, Name: "Pawster", Level: 60, Hardcore: false, Points: 16, Spent: 9 }
            && pawster.Ranks.Count == 3 && pawster.Ranks["well_rested"] == 5 && pawster.Ranks["frequent_flier"] == 1);
        check("legacy: a rank on a perk id the server can't have is left out", !pawster.Ranks.ContainsKey("Bad-Id"));
        check("legacy: a hardcore character is marked, with the server's points", listing.Characters[1] is { Hardcore: true, Points: 4, Online: true });
        check("legacy: a hardcore character without points gets the hardcore-earned ones only", listing.Characters[2] is { Hardcore: true, Points: 4 });
        var adventure = listing.Trees[0];
        check("legacy: trees keep their order, name and icon (lower-cased)",
            string.Join(",", listing.Trees.Select(t => t.Id)) == "adventure,resourcefulness,professions" && adventure is { Name: "Adventure", Icon: "inv_misc_map_01" });
        check("legacy: a perk is read with its place, ranks and requirements",
            adventure.Perks[1] is { Id: "diplomat", MaxRank: 3, Row: 1, Column: 0, RequiresTreePoints: 5, RequiresPerk: "" }
            && adventure.Perks[2] is { RequiresPerk: "well_rested" } && adventure.Perks[0].RankTexts.Count == 5 && adventure.Perks[0].RankTexts[4] == "20% faster.");
        check("legacy: a perk with a malformed id is left out", adventure.Perks.Count == 4 && adventure.Perks.All(p => LegacyService.IsPerkId(p.Id)));
        check("legacy: rank texts are padded to the max rank", adventure.Perks[3].RankTexts is ["One", ""]);
        check("legacy: out-of-range rank, row and column are kept in bounds",
            listing.Trees[1].Perks[0] is { MaxRank: LegacyService.MaxRankLimit, Row: 3, Column: 2, RankTexts.Count: LegacyService.MaxRankLimit });
        check("legacy: a perk id used twice is only the first one", listing.Trees[2].Perks.Count == 1);
        check("legacy: challenges are read with who did them and when",
            listing.Challenges[0] is { Id: 101, Category: "Classes", Done: true, Hardcore: false, By: "Pawster" }
            && listing.Challenges[0].At == DateTimeOffset.FromUnixTimeSeconds(1761300000)
            && listing.Challenges[1] is { Done: false, By: "", At: null } && listing.Challenges[2] is { Done: true, Hardcore: true });

        portal.ListingJson = """{"earned":null,"characters":null,"trees":[{"perks":null}],"challenges":[{"id":1,"hardcore":true}]}""";
        listing = await Service(portal).GetListingAsync(session);
        check("legacy: missing fields don't break the listing; a hardcore-done challenge is done",
            listing is { Cap: LegacyService.DefaultCap, EarnedTotal: 0, Characters.Count: 0, Trees.Count: 1 } && listing.Challenges[0].Done);
        portal.Status = 404;
        check("legacy: 404 means the realm has Legacy off", await Service(portal).GetListingAsync(session) is null);
        portal.Status = 503;
        portal.ListingJson = """{"error":"Legacy is closed for maintenance.","code":"closed"}""";
        LauncherApiException? closed = null;
        try { await Service(portal).GetListingAsync(session); } catch (LauncherApiException ex) { closed = ex; }
        check("legacy: 503 is reported with the portal's text", closed is { Status: HttpStatusCode.ServiceUnavailable, Message: "Legacy is closed for maintenance." });
        portal.Status = 200;
        portal.ListingJson = LegacyListingJson;
        LauncherApiException? expired = null;
        try { await Service(portal).GetListingAsync(session with { Token = "stale" }); } catch (LauncherApiException ex) { expired = ex; }
        check("legacy: an expired login is reported as 401", expired?.Status == HttpStatusCode.Unauthorized);

        // The rules, as the server has them.
        listing = (await Service(new FakeLegacyPortal()).GetListingAsync(session))!;
        var trees = listing.Trees;
        LegacyPerk P(string id) => LegacyRules.FindPerk(trees, id)!;
        Dictionary<string, int> B(params (string Id, int Rank)[] ranks) => ranks.ToDictionary(r => r.Id, r => r.Rank);
        check("legacy rules: a perk without requirements can be raised", LegacyRules.CanAdd(trees, B(), P("well_rested"), 16));
        check("legacy rules: not above its max rank", LegacyRules.AddBlocker(trees, B(("well_rested", 5)), P("well_rested"), 16) == "Max rank");
        check("legacy rules: not past the character's points (the cap)",
            LegacyRules.AddBlocker(trees, B(("well_rested", 5), ("gatherer", 5), ("thrifty", 6)), P("thrifty"), 16) == "No points left"
            && LegacyRules.CanAdd(trees, B(("well_rested", 5), ("gatherer", 5), ("thrifty", 5)), P("thrifty"), 16));
        check("legacy rules: hardcore points: 4 to spend means a 5th rank is refused",
            !LegacyRules.CanAdd(trees, B(("well_rested", 4)), P("well_rested"), 4) && LegacyRules.CanAdd(trees, B(("well_rested", 3)), P("well_rested"), 4));
        check("legacy rules: no points at all says so", LegacyRules.AddBlocker(trees, B(), P("gatherer"), 0) == "No Legacy points to spend");
        check("legacy rules: requiresTreePoints counts the tree's other perks",
            LegacyRules.AddBlocker(trees, B(("well_rested", 4)), P("diplomat"), 16) == "Requires 5 points in Adventure"
            && LegacyRules.CanAdd(trees, B(("well_rested", 5)), P("diplomat"), 16));
        check("legacy rules: points in other trees don't count", !LegacyRules.CanAdd(trees, B(("well_rested", 4), ("gatherer", 5)), P("diplomat"), 16));
        check("legacy rules: a perk's own ranks don't count toward its tree requirement",
            LegacyRules.TreeSpent(trees[0], B(("diplomat", 3), ("well_rested", 5)), "diplomat") == 5);
        check("legacy rules: requiresPerk needs that perk at max rank",
            LegacyRules.AddBlocker(trees, B(("well_rested", 4)), P("frequent_flier"), 16) == "Requires Well Rested at 5/5"
            && LegacyRules.CanAdd(trees, B(("well_rested", 5)), P("frequent_flier"), 16));
        check("legacy rules: both requirements at once",
            !LegacyRules.CanAdd(trees, B(("well_rested", 5), ("diplomat", 2), ("frequent_flier", 1)), P("explorer"), 16)
            && LegacyRules.CanAdd(trees, B(("well_rested", 5), ("diplomat", 3), ("frequent_flier", 1)), P("explorer"), 16));
        var full = B(("well_rested", 5), ("diplomat", 3), ("frequent_flier", 1));
        check("legacy rules: a rank another perk needs can't be taken back (no cascade)",
            !LegacyRules.CanRemove(trees, full, P("well_rested"))
            && LegacyRules.RemoveBlocker(trees, full, P("well_rested")) == "Frequent Flier needs it; take that back first"
            && LegacyRules.RemoveBlocker(trees, B(("well_rested", 5), ("diplomat", 1), ("frequent_flier", 0)), P("well_rested")) == "Diplomat needs it; take that back first"
            && LegacyRules.RemoveBlocker(trees, B(("well_rested", 5), ("diplomat", 3), ("frequent_flier", 1), ("explorer", 1)), P("diplomat"))
               == "Explorer needs it; take that back first");
        check("legacy rules: a rank nothing needs can be taken back",
            LegacyRules.CanRemove(trees, full, P("frequent_flier")) && LegacyRules.CanRemove(trees, full, P("diplomat")));
        var deep = B(("well_rested", 5), ("diplomat", 3), ("frequent_flier", 1), ("explorer", 1));
        check("legacy rules: a rank another perk's tree points need can't be taken back",
            LegacyRules.RemoveBlocker(trees, deep, P("frequent_flier")) == "Explorer needs it; take that back first"
            && LegacyRules.CanRemove(trees, deep, P("explorer")) && LegacyRules.CanRemove(trees, B(("well_rested", 5), ("gatherer", 1), ("diplomat", 1)), P("gatherer")));
        check("legacy rules: nothing to take back at rank 0", LegacyRules.RemoveBlocker(trees, B(), P("gatherer")) == "No rank to take back");
        var broken = B(("well_rested", 2), ("diplomat", 3));
        check("legacy rules: a perk already broken doesn't hold anything up (a broken build can be taken apart)",
            LegacyRules.CanRemove(trees, broken, P("well_rested")) && LegacyRules.CanRemove(trees, broken, P("diplomat"))
            && LegacyRules.BuildProblem(trees, broken, 16) == "Diplomat's requirement isn't met any more.");
        check("legacy rules: a valid build has no problem", LegacyRules.BuildProblem(trees, full, 16) is null && LegacyRules.BuildProblem(trees, B(), 0) is null);
        check("legacy rules: over the points is a problem", LegacyRules.BuildProblem(trees, full, 4) == "The build spends 9 points and the character has 4. Take 5 back.");
        check("legacy rules: unknown perk or rank too high is a problem",
            LegacyRules.BuildProblem(trees, B(("nope", 1)), 16) is not null && LegacyRules.BuildProblem(trees, B(("frequent_flier", 2), ("well_rested", 5)), 16) is not null);

        // The build as it's sent.
        var body = LegacyService.BuildBody(B(("well_rested", 5), ("diplomat", 0), ("Bad-Id", 1), ("frequent_flier", 1)));
        check("legacy: a build is sent without 0 ranks or bad ids, in id order",
            JsonSerializer.Serialize(body) == """{"frequent_flier":1,"well_rested":5}""");

        // Saving: every answer the portal can give.
        async Task<(LegacySaveResult Result, FakeLegacyPortal Portal)> Save(int status, string json, IReadOnlyDictionary<string, int>? ranks = null)
        {
            var fake = new FakeLegacyPortal { SaveStatus = status, SaveJson = json };
            return (await Service(fake).SaveAsync(session, 12, ranks ?? full), fake);
        }
        var (saved, savePortal) = await Save(200, """{"character":{"guid":12,"name":"Pawster","level":60,"hardcore":false,"points":16,"spent":9,"ranks":{"well_rested":5,"diplomat":3,"frequent_flier":1}},"message":"Legacy perks saved for Pawster."}""");
        check("legacy: a save posts the character and its whole build as JSON",
            savePortal.LastSave is { } sent && sent.GetProperty("character").GetUInt32() == 12
            && sent.GetProperty("ranks").GetRawText() == """{"diplomat":3,"frequent_flier":1,"well_rested":5}"""
            && savePortal.LastSaveContentType == "application/json");
        check("legacy: 200: saved, with the server's character and message",
            saved is { Outcome: LegacySaveOutcome.Saved, Message: "Legacy perks saved for Pawster.", Character: { Guid: 12, Spent: 9 } });
        var (reset, resetPortal) = await Save(200, """{"character":{"guid":12,"name":"Pawster","points":16,"ranks":{}},"message":"Reset."}""", B());
        check("legacy: an empty build is sent as {} (a reset)", resetPortal.LastSave?.GetProperty("ranks").GetRawText() == "{}" && reset.Character?.Ranks.Count == 0);
        var (combat, _) = await Save(409, """{"error":"combat","message":"Pawster is in combat."}""");
        check("legacy: 409: refused, with the code and the server's message", combat is { Outcome: LegacySaveOutcome.Refused, Code: "combat", Message: "Pawster is in combat." });
        var (bare, _) = await Save(409, """{"error":"points"}""");
        check("legacy: 409 without a message gets one for its code",
            bare is { Outcome: LegacySaveOutcome.Refused, Code: "points", Message: "That build needs more Legacy points than the character has." });
        var (refused, _) = await Save(409, """{"error":"refused"}""");
        check("legacy: an unknown refusal code still says it was refused", refused is { Outcome: LegacySaveOutcome.Refused, Message: "The realm refused the build." });
        check("legacy: refusal messages for every code",
            new[] { "no_character", "combat", "points", "locked", "invalid", "closed" }.Select(c => LegacyService.RefusalMessage(c, null)).Distinct().Count() == 6
            && LegacyService.RefusalMessage("combat", "  In combat.  ") == "In combat.");
        var (busy, _) = await Save(429, """{"error":"Your last save is still going through."}""");
        check("legacy: 429: not tried, with the portal's message", busy is { Outcome: LegacySaveOutcome.NotTried, Message: "Your last save is still going through." });
        var (malformed, _) = await Save(400, """{"error":"That build isn't valid."}""");
        check("legacy: 400: not tried", malformed.Outcome == LegacySaveOutcome.NotTried);
        var (closedSave, _) = await Save(503, """{"error":"Legacy is closed for maintenance.","code":"closed"}""");
        check("legacy: 503 closed: not tried, with the portal's text and the code",
            closedSave is { Outcome: LegacySaveOutcome.NotTried, Message: "Legacy is closed for maintenance.", Code: "closed" });
        var (down, _) = await Save(503, """{"error":"The game server can't be reached."}""");
        check("legacy: 503 unreachable: not tried", down is { Outcome: LegacySaveOutcome.NotTried, Message: "The game server can't be reached.", Code: null });
        var (lost, _) = await Save(502, """{"error":"The game server didn't answer."}""");
        check("legacy: 502: may or may not have been saved", lost is { Outcome: LegacySaveOutcome.Unknown, Message: "The game server didn't answer." });
        var (garbled, _) = await Save(502, "<html>Bad Gateway</html>");
        check("legacy: a proxy's 502 page still counts as maybe saved", garbled is { Outcome: LegacySaveOutcome.Unknown } && garbled.Message.Length > 0);
        var (odd, _) = await Save(200, """{"message":"ok"}""");
        check("legacy: a 200 without the character isn't taken for a save", odd.Outcome == LegacySaveOutcome.Unknown);
        var (logout, _) = await Save(401, """{"error":"Your login has expired. Log in again."}""");
        check("legacy: 401: logged out", logout is { Outcome: LegacySaveOutcome.LoggedOut, Message: "Your login has expired. Log in again." });
        var offline = new FakeLegacyPortal { Offline = true, OfflineError = HttpRequestError.ConnectionError };
        check("legacy: no connection: nothing was sent", (await Service(offline).SaveAsync(session, 12, full)).Outcome == LegacySaveOutcome.NotTried);
        offline.OfflineError = HttpRequestError.Unknown;
        check("legacy: a lost connection: may have been saved", (await Service(offline).SaveAsync(session, 12, full)).Outcome == LegacySaveOutcome.Unknown);

        // The page's view model: the draft.
        portal = new FakeLegacyPortal();
        using (var legacy = Legacy(portal))
        {
            await legacy.LoadAsync();
            LegacyPerkView Perk(string id) => legacy.Trees.SelectMany(t => t.Perks).First(p => p.Perk.Id == id);
            check("legacy page: opens on the online character, a hardcore one with its 4 points",
                legacy.SelectedCharacter?.Name == "Ironhide" && legacy is { IsHardcore: true, Available: 4, Spent: 0, PointsText: "0 / 4" }
                && legacy.HardcoreNote.Contains("only the 4 points earned on hardcore characters", StringComparison.Ordinal));
            check("legacy page: three trees with 12 cells each, perks in their places",
                legacy.Trees.Count == 3 && legacy.Trees.All(t => t.Cells.Count == 12)
                && legacy.Trees[0].Cells[1].Perk?.Perk.Id == "well_rested" && legacy.Trees[0].Cells[3].Perk?.Perk.Id == "diplomat"
                && legacy.Trees[0].Cells[0].Perk is null && legacy.Trees[1].Cells[11].Perk?.Perk.Id == "thrifty");
            check("legacy page: a perk that can't be picked is dimmed, one that can is not",
                Perk("diplomat") is { IsDimmed: true, CanAdd: false } && Perk("well_rested") is { IsDimmed: false, CanAdd: true, RankText: "0/5" });
            for (var i = 0; i < 6; i++) legacy.Add(Perk("well_rested"));
            check("legacy page: a hardcore character stops at its hardcore points",
                legacy is { Spent: 4, PointsText: "4 / 4", HasUnsavedChanges: true, CanSave: true } && Perk("well_rested").Rank == 4
                && Perk("gatherer") is { CanAdd: false, IsDimmed: true });
            check("legacy page: the tooltip has the current and next rank and the requirement",
                Perk("well_rested") is { CurrentRankText: "16% faster.", NextRankText: "20% faster.", RankLine: "Rank 4/5" }
                && Perk("diplomat").RequirementText == "Requires 5 points in Adventure" && Perk("frequent_flier").RequirementText == "Requires Well Rested at 5/5");

            // Switching character with unsaved changes asks first.
            var paws = legacy.Characters.First(c => c.Guid == 12);
            check("legacy page: switching character with unsaved changes asks first",
                !legacy.RequestCharacter(paws) && legacy.IsConfirmingDiscard && legacy.SelectedCharacter?.Guid == 14
                && legacy.DiscardText == "Discard Ironhide's unsaved Legacy perks and switch to Pawster?");
            legacy.CancelDiscard();
            check("legacy page: keep editing keeps the draft", !legacy.IsConfirmingDiscard && legacy.Spent == 4 && legacy.SelectedCharacter?.Guid == 14);
            legacy.RequestCharacter(paws);
            legacy.ConfirmDiscard();
            check("legacy page: discard switches, with that character's saved build as the draft",
                legacy.SelectedCharacter?.Guid == 12 && !legacy.HasUnsavedChanges && legacy is { Spent: 9, Available: 16, IsHardcore: false }
                && Perk("well_rested") is { Rank: 5, IsMaxed: true } && Perk("frequent_flier").Rank == 1);
            check("legacy page: without changes, switching needs no question", legacy.RequestCharacter(legacy.Characters.First(c => c.Guid == 15))
                && legacy.SelectedCharacter?.Guid == 15 && legacy.RequestCharacter(paws) && !legacy.IsConfirmingDiscard);

            // Draft edits.
            check("legacy page: removing a rank others need is refused", !legacy.Remove(Perk("well_rested")) && Perk("well_rested") is { Rank: 5, CanRemove: false });
            check("legacy page: removing a leaf works and marks it changed", legacy.Remove(Perk("frequent_flier")) && Perk("frequent_flier") is { Rank: 0, IsChanged: true } && legacy.HasUnsavedChanges);
            legacy.ResetDraft();
            check("legacy page: UNDO goes back to the saved build", !legacy.HasUnsavedChanges && Perk("frequent_flier").Rank == 1 && !legacy.CanUndo);
            legacy.ClearAll();
            check("legacy page: CLEAR ALL empties the draft, unsaved", legacy is { Spent: 0, HasUnsavedChanges: true, CanSave: true } && portal.Saves == 0);
            legacy.ResetDraft();

            // Leaving the page.
            LauncherPage? left = null;
            legacy.LeaveConfirmed += page => left = page;
            check("legacy page: leaving without changes needs no question", legacy.AskToLeave(LauncherPage.Home) && !legacy.IsConfirmingDiscard);
            legacy.Add(Perk("gatherer"));
            check("legacy page: leaving with changes asks first", !legacy.AskToLeave(LauncherPage.Armory) && legacy.IsConfirmingDiscard
                && legacy.DiscardText.EndsWith("leave the page?", StringComparison.Ordinal));
            legacy.ConfirmDiscard();
            check("legacy page: discard leaves for the page asked for, the draft dropped", left == LauncherPage.Armory && !legacy.HasUnsavedChanges);

            // Saving.
            legacy.Add(Perk("gatherer"));
            legacy.Add(Perk("gatherer"));
            portal.SaveJson = """{"character":{"guid":12,"name":"Pawster","race":1,"class":1,"level":60,"hardcore":false,"points":16,"spent":11,"ranks":{"well_rested":5,"diplomat":3,"frequent_flier":1,"gatherer":2}},"message":"Legacy perks saved for Pawster."}""";
            var listingsBefore = portal.Listings;
            await legacy.SaveAsync();
            check("legacy page: SAVE sends the whole draft for the picked character",
                portal.Saves == 1 && portal.LastSave?.GetProperty("character").GetUInt32() == 12
                && portal.LastSave?.GetProperty("ranks").GetRawText() == """{"diplomat":3,"frequent_flier":1,"gatherer":2,"well_rested":5}""");
            check("legacy page: after a save the returned character is the saved build, nothing asked again",
                !legacy.HasUnsavedChanges && legacy.SelectedCharacter?.Character.Spent == 11 && legacy.Characters.First(c => c.Guid == 12).PointsText == "11 / 16"
                && legacy.StatusMessage == "Legacy perks saved for Pawster." && !legacy.StatusIsError && portal.Listings == listingsBefore);

            // Refusals keep the draft.
            legacy.Add(Perk("gatherer"));
            portal.SaveStatus = 409;
            portal.SaveJson = """{"error":"combat","message":"Pawster is in combat. Try again once the fight is over."}""";
            listingsBefore = portal.Listings;
            await legacy.SaveAsync();
            check("legacy page: an in-combat refusal shows the server's message and keeps the draft",
                legacy is { StatusIsError: true, HasUnsavedChanges: true } && legacy.StatusMessage.StartsWith("Pawster is in combat", StringComparison.Ordinal)
                && Perk("gatherer").Rank == 3 && portal.Listings == listingsBefore);
            portal.SaveJson = """{"error":"points","message":"Pawster has 16 points to spend."}""";
            await legacy.SaveAsync();
            check("legacy page: a points refusal asks for the listing again and still keeps the draft",
                portal.Listings == listingsBefore + 1 && legacy.HasUnsavedChanges && Perk("gatherer").Rank == 3
                && legacy.StatusMessage == "Pawster has 16 points to spend." && legacy.StatusIsError);
            portal.SaveStatus = 502;
            portal.SaveJson = """{"error":"The game server didn't answer."}""";
            listingsBefore = portal.Listings;
            await legacy.SaveAsync();
            check("legacy page: no answer asks for the listing again, the draft kept", portal.Listings == listingsBefore + 1 && Perk("gatherer").Rank == 3
                && legacy.StatusMessage == "The game server didn't answer.");
            portal.SaveStatus = 429;
            portal.SaveJson = """{"error":"Your last save is still going through."}""";
            listingsBefore = portal.Listings;
            await legacy.SaveAsync();
            check("legacy page: 429 doesn't ask again", portal.Listings == listingsBefore && legacy.StatusIsError && legacy.HasUnsavedChanges);
            portal.SaveStatus = 401;
            portal.SaveJson = """{"error":"Your login has expired. Log in again."}""";
            string? expiredMessage = null;
            legacy.LoginExpired += message => expiredMessage = message;
            await legacy.SaveAsync();
            check("legacy page: 401 on a save sends the player back to the login", expiredMessage == "Your login has expired. Log in again.");

            // A refresh with a draft up keeps it.
            await legacy.LoadAsync(keepStatus: true);
            check("legacy page: a refresh keeps the character and the draft", legacy.SelectedCharacter?.Guid == 12 && Perk("gatherer").Rank == 3 && legacy.HasUnsavedChanges);

            // An invalid draft can't be saved.
            legacy.ResetDraft();
            check("legacy page: nothing to save without changes", !legacy.CanSave);

            // Challenges.
            check("legacy page: challenges by category with counts, all first",
                string.Join(",", legacy.Categories.Select(c => $"{c.Title}={c.CountText}")) == "All challenges=2 / 4,Classes=1 / 2,Dungeons=1 / 1,Other=0 / 1"
                && legacy.ChallengesTabText == "2 / 4" && legacy.Categories[2].IsComplete);
            legacy.SelectedCategory = legacy.Categories.First(c => c.Title == "Classes");
            check("legacy page: a category shows its challenges, not done first",
                legacy.Challenges.Select(c => c.Challenge.Id).SequenceEqual([102u, 101u]) && legacy.Challenges[1].ByText.StartsWith("Pawster · ", StringComparison.Ordinal)
                && legacy.Challenges[0].ByText == "");
            legacy.SelectedCategory = legacy.Categories.First(c => c.Title == "Dungeons");
            check("legacy page: a challenge done on a hardcore character is marked", legacy.Challenges.Single() is { Hardcore: true, Done: true });
        }

        // A save while the character went away: the server says so, the listing is asked again.
        portal = new FakeLegacyPortal();
        using (var legacy = Legacy(portal))
        {
            await legacy.LoadAsync();
            var only = legacy.Trees.SelectMany(t => t.Perks).First(p => p.Perk.Id == "well_rested");
            legacy.Add(only);
            portal.SaveStatus = 409;
            portal.SaveJson = """{"error":"no_character","message":"Ironhide isn't on your account any more."}""";
            portal.ListingJson = LegacyListingJson.Replace("\"guid\":14", "\"guid\":99").Replace("\"online\":true", "\"online\":false");
            await legacy.SaveAsync();
            check("legacy page: a character that's gone is replaced by another, and the player is told",
                legacy.SelectedCharacter?.Guid == 12 && legacy.StatusIsError && !legacy.HasUnsavedChanges);
        }

        using (var legacy = Legacy(new FakeLegacyPortal()))
        {
            await legacy.LoadAsync();
            var closes = 0;
            legacy.CloseConfirmed += () => closes++;
            check("legacy page: closing without changes needs no question", legacy.AskToClose() && !legacy.IsConfirmingDiscard);
            legacy.Add(legacy.Trees[2].Perks[0]);
            check("legacy page: closing with changes asks first",
                !legacy.AskToClose() && legacy.IsConfirmingDiscard && legacy.DiscardText.Contains("close Portalkeeper") && closes == 0);
            legacy.CancelDiscard();
            check("legacy page: KEEP EDITING keeps the window and the draft", !legacy.IsConfirmingDiscard && legacy.HasUnsavedChanges && closes == 0);
            legacy.AskToClose();
            legacy.ConfirmDiscard();
            check("legacy page: DISCARD drops the draft and closes", closes == 1 && !legacy.HasUnsavedChanges && !legacy.IsConfirmingDiscard);
        }
        using (var legacy = Legacy(new FakeLegacyPortal
               {
                   SaveJson = """{"character":{"guid":14,"name":"Ironhide","race":3,"class":1,"level":30,"online":true,"hardcore":true,"ranks":{"gatherer":1}},"message":"Saved."}""",
               }))
        {
            await legacy.LoadAsync();
            legacy.Add(legacy.Trees[2].Perks[0]);
            await legacy.SaveAsync();
            check("legacy page: a save answer without points keeps the listing's",
                legacy is { SelectedCharacter.Guid: 14, Available: 4, Spent: 1, HasBuildProblem: false, HasUnsavedChanges: false });
        }
        using (var legacy = Legacy(new FakeLegacyPortal { ListingJson = """{"cap":16,"earned":{"total":1,"hardcore":1},"characters":[],"trees":[],"challenges":[]}""" }))
        {
            await legacy.LoadAsync();
            check("legacy page: one point earned still names the hardcore part", legacy.EarnedText == "1 point earned on your account (1 on hardcore characters)");
        }
        {
            var asked = 0;
            using var legacy = new LegacyViewModel(Service(new FakeLegacyPortal()), session, _ => { asked++; return Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null); });
            await legacy.LoadAsync();
            var first = asked;
            await legacy.LoadAsync();
            check("legacy page: an icon that didn't load is asked for again at the next listing", first > 0 && asked == first * 2);
        }
        using (var legacy = Legacy(new FakeLegacyPortal { Status = 404 }))
        {
            var gone = false;
            legacy.Unavailable += () => gone = true;
            await legacy.LoadAsync();
            check("legacy page: a 404 on a visit says Legacy is off", gone && legacy.HasLoadError && !legacy.ShowLegacy);
        }
        using (var legacy = Legacy(new FakeLegacyPortal { Status = 503, ListingJson = """{"error":"Legacy is closed.","code":"closed"}""" }))
        {
            await legacy.LoadAsync();
            check("legacy page: Legacy that can't open says why", legacy.HasLoadError && legacy.LoadError == "Legacy is closed.");
        }
        using (var legacy = Legacy(new FakeLegacyPortal { ListingJson = """{"cap":16,"earned":{"total":0,"hardcore":0},"characters":[],"trees":[],"challenges":[]}""" }))
        {
            await legacy.LoadAsync();
            check("legacy page: an account without characters is told so", legacy is { HasNoCharacters: true, SelectedCharacter: null, CanSave: false, ShowPerkTools: false });
        }
    }

    private sealed class FakeLegacyPortal : HttpMessageHandler
    {
        public const string Token = "legacy-test-token";
        public int Status = 200;
        public string ListingJson = LegacyListingJson;
        public int SaveStatus = 200;
        public string SaveJson = """{"character":{"guid":12,"name":"Pawster","points":16,"ranks":{}},"message":"Saved."}""";
        public bool Offline;
        public HttpRequestError OfflineError = HttpRequestError.Unknown;
        public int Authorized, Unauthorized, Listings, Saves;
        public JsonElement? LastSave;
        public string? LastSaveContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException(OfflineError, "No route to host.");
            if (request.RequestUri!.AbsolutePath != "/api/launcher/legacy.php") return Json(404, """{"error":"Not found."}""");
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
            {
                Unauthorized++;
                return Json(401, """{"error":"Your login has expired. Log in again."}""");
            }
            Authorized++;
            if (request.Method == HttpMethod.Get)
            {
                Listings++;
                return Json(Status, Status == 404 ? """{"error":"Legacy is off."}""" : ListingJson);
            }
            Saves++;
            LastSaveContentType = request.Content?.Headers.ContentType?.MediaType;
            LastSave = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return Json(SaveStatus, SaveJson);
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
