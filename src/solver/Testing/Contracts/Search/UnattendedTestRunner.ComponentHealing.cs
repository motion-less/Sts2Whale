using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertComponentHealingAsync(CombatState live, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray())
            if (relic is not BurningBlood) await RelicCmd.Remove(relic);
        foreach (PowerModel power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "AGGRESSION", "RUPTURE", "DARK_EMBRACE" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "DEFEND_IRONCLAD", "STRIKE_IRONCLAD", "BASH" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Draw" });
        foreach (string id in new[] { "STOKE", "HEMOKINESIS", "FISTICUFFS" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Discard" });
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        InjectPotionForTest(player, "COLORLESS_POTION");
        InjectPotionForTest(player, "BOTTLED_POTENTIAL");
        SetEnergy(player, 10);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.UsesComponentHealingCertificate || root.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("Component root rejected: " + root.ComponentHealingRejection);
        CombatPredictionSimulator parent = root.ForkSimulator();
        string before = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        int Bound(CombatPredictionSimulator sim, bool potions = true, int? maximum = null)
            => StrategicHpRecoveryBound.ComponentHealingUpperBound(sim, player, 6, potions, maximum);
        using (SimulationNotificationIsolation.Enter())
        {
            if (Bound(parent) != 6 || Bound(parent.Fork()) != 6)
                throw new InvalidOperationException("Zero recovery plus Burning Blood lost across Fork.");
            foreach (PileType pile in new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust })
            {
                var child = parent.Fork();
                child.AddGeneratedCardToCombat(PredictedCard.Create(ModelDb.Card<Feed>(), player), pile,
                    player, resultKind: CardGenerationResultKind.Fixed);
                if (Bound(child) != int.MaxValue)
                    throw new InvalidOperationException("Unknown source accepted in " + pile);
            }
            var attachment = parent.Fork();
            PredictedCard card = attachment.State.GetPlayerCombatState(player).Hand.Cards[0];
            attachment.Afflict<Hexed>(card, 1);
            if (Bound(attachment) != int.MaxValue)
                throw new InvalidOperationException("Unknown attachment accepted.");
            var revival = parent.Fork();
            ((SimulatedCombatState)revival.State.CombatState).SetAmount<ReattachPower>(player.Creature, 25);
            if (Bound(revival) != int.MaxValue)
                throw new InvalidOperationException("Enemy-only recovery accepted for player.");
            if (DescribeContinuationContractState(parent, root, player) != before
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Inspection changed parent/live/RNG.");
        }
        _completedChecks.Add("ComponentHealing:ZeroRecovery:AllFourPiles:UnknownAttachment:WrongOwner:Fork:ParentLiveRngIsolation");

        CombatPredictionSimulator SimUse(CombatPredictionSimulator source, int slot, PlanCardChoice? choice)
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            var sim = source.Fork(); var combat = (SimulatedCombatState)sim.State.CombatState;
            PotionModel potion = combat.GetPotionAtSlot(player, slot)!;
            combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
            try
            {
                int history = sim.History.Entries.Count;
                if (!PotionExecutionSupport.Prepare(sim, combat, potion, slot, player.Creature)
                    || !PotionExecutionSupport.Complete(sim, combat, potion, player.Creature, choice,
                        history, new ForkableSet<uint>())
                    || !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                    throw new InvalidOperationException("Potion suspended unexpectedly.");
            }
            finally { combat.EndActionChoices(); }
            return sim;
        }
        var discover = SimUse(parent, 0, null);
        PotionModel colorless = player.GetPotionAtSlotIndex(0)!;
        PlanCardChoice selected = CardChoiceSupport.BuildChoices(PotionChoiceSupport.GetSpec(discover, colorless),
            SolverDisplayNames.Capture(live), 256, 256).First(choice => choice.Cards.Count > 0)
            with { SourceId = "COLORLESS_POTION" };
        CombatPredictionSimulator first = SimUse(parent, 0, selected);
        CombatPredictionSimulator both = SimUse(first, 1, null);
        if (Bound(first) != 6 || Bound(both) != 6 || Bound(both.Fork()) != 6)
            throw new InvalidOperationException("Native generated closure or shuffle lost its bound.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var session = NativeChoiceRuntime.Begin(live, player, "test:component-healing");
        session.SetPlanAndStartDriving(NGame.Instance!, [selected], deadline.Token);
        foreach (var (slot, expectedSim) in new[] { (0, first), (1, both) })
        {
            PotionModel potion = player.GetPotionAtSlotIndex(slot)!;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is UsePotionAction use && use.PotionIndex == (uint)slot
                    && ReferenceEquals(use.Player, player),
                () => potion.EnqueueManualUse(player.Creature), deadline.Token);
            if (slot == 0)
                await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
            else await action.CompletionTask.WaitAsync(deadline.Token);
            string expected = ContinuationStamp.CapturePredicted(player, expectedSim, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText;
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException("Potion full native state mismatch at slot " + slot);
        }
        if (DescribeContinuationContractState(parent, root, player) != before)
            throw new InvalidOperationException("Native use changed frozen parent.");
        _completedChecks.Add("ComponentHealing:ColorlessNativeFilterAndChoice:BottledPotentialNativeShuffleDraw:TwoFullNativeStates:Fork:FrozenParent");

        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 10);
        await PowerCmd.Apply<RegenPower>(new ThrowingPlayerChoiceContext(), player.Creature, 5,
            player.Creature, null);
        InjectPotionForTest(player, "REGEN_POTION");
        InjectPotionForTest(player, "REGEN_POTION");
        CombatRootSnapshot regenRoot = CombatRootSnapshot.Capture(live);
        var regenParent = regenRoot.ForkSimulator();
        if (!regenRoot.UsesComponentHealingCertificate || Bound(regenParent) != 126
            || Bound(regenParent, false) != 21 || Bound(regenParent, maximum: 0) != 21)
            throw new InvalidOperationException("Active Regen plus two stacked doses/cap/ban bound incorrect.");
        var used = SimUse(regenParent, 0, null);
        var usedBoth = SimUse(used, 1, null);
        if (Bound(used, maximum: 1) != 61 || Bound(used, maximum: 2) != 126
            || Bound(usedBoth, maximum: 2) != 126 || Bound(regenParent) != 126)
            throw new InvalidOperationException("Explicit potion count or Regen stacking changed parent.");
        foreach (var (slot, expectedSim) in new[] { (0, used), (1, usedBoth) })
        {
            PotionModel potion = player.GetPotionAtSlotIndex(slot)!;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is UsePotionAction use && use.PotionIndex == (uint)slot
                    && ReferenceEquals(use.Player, player),
                () => potion.EnqueueManualUse(player.Creature), deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            string expected = ContinuationStamp.CapturePredicted(player, expectedSim, regenRoot.StartTurnNumber,
                regenRoot.Forecast, regenRoot.StartTurnNumber).StateText;
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException("Stacked Regen native state mismatch at slot " + slot);
        }
        _completedChecks.Add("ComponentHealing:ActiveRegen:TwoDoses:Stacking:Ban:ZeroCap:SpentCap:BranchPotionHistory:TwoFullNativeStates");
        await InjectCardAsync(live, player, new() { CardId = "FEED", Pile = "Exhaust", TreatAsDeckCard = true });
        CombatRootSnapshot unknownRoot = CombatRootSnapshot.Capture(live);
        if (unknownRoot.UsesComponentHealingCertificate || !root.UsesComponentHealingCertificate
            || DescribeContinuationContractState(parent, root, player) != before)
            throw new InvalidOperationException("Unknown exhausted initial source or immutable root guard failed.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        CombatRootSnapshot deckRoot = CombatRootSnapshot.Capture(live);
        if (deckRoot.UsesComponentHealingCertificate
            || deckRoot.ComponentHealingRejection?.StartsWith("root-listener:") != true)
            throw new InvalidOperationException("Unknown source lost when absent from all combat piles: "
                + deckRoot.ComponentHealingRejection);
        _completedChecks.Add("ComponentHealing:UnknownInitialExhaustGrowth:PermanentDeckAbsentFromCombat:FrozenRoot");
    }
}
