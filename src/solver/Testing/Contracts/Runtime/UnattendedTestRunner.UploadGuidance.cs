using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertUploadGuidance(CombatState combat, Player player)
    {
        var combatField = AccessTools.Field(typeof(SolverController), "_combat");
        var searchField = AccessTools.Field(typeof(SolverController), "_search");
        object oldCombat = combatField.GetValue(null)!;
        object? oldSearch = searchField.GetValue(null);
        var oldMocks = AssemblyInfo.MockTypes;
        string oldLanguage = LocManager.Instance.Language;
        var recording = CombatReplayRecording.Pending
            ?? throw new InvalidOperationException("Native combat recording was not started.");
        var incompleteField = AccessTools.Field(typeof(CombatReplayRecording), "_incompleteReason");
        object? oldIncomplete = incompleteField.GetValue(recording);
        var record = AccessTools.Method(typeof(CombatReplayRecording), "Record");
        var mod = new Mod { path = "upload-content", manifest = new ModManifest
            { id = "UploadContent", name = "Upload Content", affectsGameplay = false } };
        Exception failure = new InvalidOperationException("ordinary failure in content combat");
        NGame host = NGame.Instance!;
        try
        {
            AssemblyInfo.MockTypes = oldMocks == null ? [] : new(oldMocks);
            SolverCombatSession native = new();
            combatField.SetValue(null, native);
            // A registered content type outside this combat does not change guidance.
            AssemblyInfo.MockTypes[typeof(UnadaptedContentModel)] = (mod, false);
            var absentMonster = ModelDb.All.OfType<MonsterModel>()
                .First(monster => monster.GetType() != combat.Enemies[0].Monster!.GetType());
            AssemblyInfo.MockTypes[absentMonster.GetType()] = (mod, false);
            native.UploadPolicy.Observe(combat);
            if (!SolverController.AllowsPlayerUploadGuidance)
                throw new InvalidOperationException("Absent content changed native combat guidance.");

            SolverSearchSession NewSearch() => new(1, combat, LiveCombatStamp.Capture(combat), false)
                { StartTurnNumber = player.PlayerCombatState!.TurnNumber };
            void Input(string origin) => record.Invoke(recording, [new CombatReplayEvent(), origin, null, null]);
            incompleteField.SetValue(recording, "upload_guidance_contract");
            var manual = NewSearch();
            searchField.SetValue(null, manual);
            Input("solver");
            Input("system");
            if (manual.PlayerInputObserved) throw new InvalidOperationException("Solver/system input was counted as manual.");
            Input("player");
            if (!manual.PlayerInputObserved) throw new InvalidOperationException("Manual input was lost after recording became incomplete.");
            SolverController.RecordSearchResultStale(manual);
            if (native.BugReportIssues.RequiresPlayerUpload || SolverController.BugReportUploadRecommended
                || native.BugReportIssues.Snapshot().Single().Kind != CombatBugReportIssueKind.ManualSearchResultStale)
                throw new InvalidOperationException("Manual stale result requested an upload.");

            foreach (string language in new[] { "eng", "zhs", "zht" })
            {
                LocManager.Instance.SetLanguage(language);
                AssertPrompt(SolverController.FormatSearchResultStale(true), false);
                AssertPrompt(SolverController.FormatSearchResultStale(false), true);
                foreach (string text in FailureTexts()) AssertPrompt(text, true);
            }

            var next = NewSearch();
            searchField.SetValue(null, next);
            Input("system");
            if (next.PlayerInputObserved) throw new InvalidOperationException("Manual history leaked into a new search.");
            SolverController.RecordSearchResultStale(next);
            if (!native.BugReportIssues.RequiresPlayerUpload || !SolverController.BugReportUploadRecommended)
                throw new InvalidOperationException("Unexplained stale result lost its upload guidance.");
            SolverController.RecordSearchResultStale(manual);
            if (!native.BugReportIssues.RequiresPlayerUpload)
                throw new InvalidOperationException("Manual stale result cleared an existing diagnostic issue.");

            AbstractModel[] content = [player.Character, player.PlayerCombatState!.Hand.Cards[0],
                player.Relics[0], combat.Enemies[0].Monster!];
            foreach (AbstractModel model in content)
            {
                SolverCombatSession session = new();
                combatField.SetValue(null, session);
                AssemblyInfo.MockTypes[model.GetType()] = (mod, false);
                session.UploadPolicy.Observe(combat);
                AssemblyInfo.MockTypes.Remove(model.GetType());
                session.UploadPolicy.Observe(combat);
                session.BugReportIssues.RecordFailure(CombatBugReportIssueKind.SearchFailure, failure);
                session.ManualRouteImprovementDetected = true;
                session.ReplanCounts[ReplanCause.StateMismatch] = 1;
                if (SolverController.AllowsPlayerUploadGuidance || SolverController.BugReportUploadRecommended
                    || session.BugReportIssues.Snapshot().Single().Kind != CombatBugReportIssueKind.SearchFailure)
                    throw new InvalidOperationException("Content combat lost its original failure or requested an upload.");
                foreach (string language in new[] { "eng", "zhs", "zht" })
                {
                    LocManager.Instance.SetLanguage(language);
                    foreach (string text in FailureTexts())
                    {
                        AssertPrompt(text, false);
                        if (!text.Contains(failure.Message, StringComparison.Ordinal))
                            throw new InvalidOperationException("Content guidance hid the original failure.");
                    }
                    AssertPrompt(SolverController.FormatSearchResultStale(false), false);
                }
                SolverOverlay.Show(host, "upload guidance contract");
                SolverOverlay.RefreshControls();
                var banner = (Control)AccessTools.Field(typeof(SolverOverlay), "_feedbackBanner").GetValue(null)!;
                var label = (Label)AccessTools.Field(typeof(SolverOverlay), "_feedbackBannerLabel").GetValue(null)!;
                if (banner.Visible) AssertPrompt(label.Text, false);
                SolverOverlay.ShowFullAutoStoppedAfterWorseRecalculation(1, 0, 1);
                AssertPrompt(SolverOverlay.SearchSummaryTextForTesting!, false);
                SolverOverlay.ShowFullAutoStoppedAtLiveRisk(1, 0, 1, false);
                AssertPrompt(SolverOverlay.SearchSummaryTextForTesting!, false);
            }
            combatField.SetValue(null, new SolverCombatSession());
            if (!SolverController.AllowsPlayerUploadGuidance)
                throw new InvalidOperationException("Content upload policy leaked into a new combat.");
            _completedChecks.Add("UploadGuidance:ManualSearchInput:RecordingIncomplete:PerSearch:NativeStale:ExistingIssue:Character:Card:Relic:Monster:FourFailures:Feedback:LiveRisk:eng-zhs-zht");
        }
        finally
        {
            incompleteField.SetValue(recording, oldIncomplete);
            searchField.SetValue(null, oldSearch);
            combatField.SetValue(null, oldCombat);
            AssemblyInfo.MockTypes = oldMocks;
            LocManager.Instance.SetLanguage(oldLanguage);
        }

        IEnumerable<string> FailureTexts()
        {
            yield return SolverController.FormatSearchSetupFailure(failure);
            yield return SolverController.FormatSearchFailureForTesting(failure, false);
            yield return SolverController.FormatSearchFailureForTesting(failure, true);
            yield return SolverController.FormatDeploymentFailure(failure);
            yield return SolverController.FormatTurnSetupFailure(failure, true);
        }
        static void AssertPrompt(string text, bool expected)
        {
            bool present = text.Contains(SolverUiTokens.BugReportUploadInstruction, StringComparison.Ordinal)
                || text.Contains(SolverUiTokens.ParallelSearchFailureInstruction, StringComparison.Ordinal);
            if (present != expected) throw new InvalidOperationException($"Upload guidance mismatch: {text}");
        }
    }
}
