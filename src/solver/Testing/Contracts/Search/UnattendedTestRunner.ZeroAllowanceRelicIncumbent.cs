using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertZeroAllowanceRelicIncumbentAsync(CombatState live, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<PenNib>().ToMutable());
        await ClearPlayerPilesAsync(player);
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand", Count = 2 });
        await InjectCardAsync(live, player, new() { CardId = "BLOODLETTING", Pile = "Hand" });
        SetEnergy(player, 3);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 12);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        var names = SolverDisplayNames.Capture(live);
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 0, 10)],
            DisableSharedPrimaryIncumbentsForTesting = true,
        };
        policy = policy with { Profile = policy.Profile with
            { BeamWidth = 8, MaxExpandedNodes = 200, SoftTimeBudgetMilliseconds = 10000 } };
        var parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        var partial = await Task.Run(() => CombatBeamSolver.ZeroAllowanceRelicIncumbentProbeForTesting(
            root, names, damage, policy));
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("Zero-allowance bound changed parent/live/RNG.");
        var native = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "BLOODLETTING");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
            queued => queued is PlayCardAction play && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), native),
            () => { if (!native.TryManualPlay(null)) throw new InvalidOperationException("Native HP-loss prefix refused."); },
            deadline.Token);
        await action.CompletionTask.WaitAsync(deadline.Token);
        string expected = ContinuationStamp.CapturePredicted(player, partial.Snapshot.Simulator,
            partial.Turn, root.Forecast, root.StartTurnNumber).StateText;
        if (ContinuationStamp.CaptureLive(live).StateText != expected)
            throw new InvalidOperationException("Native HP-loss prefix full state differs.");
        _completedChecks.Add("ZeroAllowanceRelicIncumbent:ActualCompleteZeroLossGoalSatisfied:LocalAndExternalPublication:StrictlyWorsePruned:EqualHpKept:PositiveAllowanceGrowthTheftKept:NativeHpLossFullState:ParentLiveRng");
    }
}

internal sealed partial class CombatBeamSolver
{
    internal static SearchNode ZeroAllowanceRelicIncumbentProbeForTesting(CombatRootSnapshot root,
        SolverDisplayNames names, BattleDamageSnapshot damage, SearchPolicySnapshot policy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var isolation = SimulationNotificationIsolation.Enter();
        var solver = new CombatBeamSolver(root, names, damage, policy, deadline.Token,
            potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0, minimumPotionUses: 0);
        SearchNode winner = solver.CostRouteForTesting(null, out SearchNode opening);
        if (!winner.Snapshot.AllEnemiesDead || winner.Snapshot.HasRisk
            || winner.Snapshot.CumulativePlayerHpLost != 0 || !winner.Snapshot.RelicCounters.Satisfied
            || !solver._strictHpBoundWithRelicTargets)
            throw new InvalidOperationException("Zero-allowance fixture lacks a complete zero-loss goal victory.");
        solver.TightenPrimarySearchIncumbentAtTurnLayer([winner], 0);
        if (solver._primaryIncumbent is not { StrategicHpDeficit: 0 })
            throw new InvalidOperationException("Zero-allowance relic victory failed to establish the local HP bound.");
        PlanAction bloodletting = solver.PrepareCardActions(opening)
            .Single(candidate => candidate.Action.CardId == "BLOODLETTING").Action;
        SearchNode partial = solver.CostChildForTesting(opening, bloodletting);
        if (partial.Snapshot.CumulativePlayerHpLost != 3 || partial.Snapshot.HasRisk
            || solver.ApplyPrimaryIncumbentBound([partial]).Count != 0
            || solver.ApplyPrimaryIncumbentBound([opening]).Count != 1)
            throw new InvalidOperationException("Zero-allowance relic bound did not preserve strict/equal HP semantics.");
        foreach (SearchPolicySnapshot guarded in new[]
        {
            policy with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 10, 10)] },
            policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("zero_relic_guard") },
            policy with { TheftPolicy = SolverTheftPolicy.PreserveResources },
        })
        {
            var member = new CombatBeamSolver(root, names, damage, guarded, deadline.Token,
                potionPolicyOverride: SolverPotionPolicy.Disabled,
                primaryIncumbent: solver._primaryIncumbent, maximumPotionUses: 0, minimumPotionUses: 0);
            if (member.ApplyPrimaryIncumbentBound([partial]).Count != 1)
                throw new InvalidOperationException("Positive allowance/growth/theft policy lost its branch.");
        }
        var search = new CombatBeamSolver(root, names, damage, policy, deadline.Token,
            potionPolicyOverride: SolverPotionPolicy.Disabled, maximumPotionUses: 0, minimumPotionUses: 0);
        SolverResult result = search.Solve();
        if (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0
            || !result.Snapshot.RelicCounters.Satisfied
            || !CombatSearchCoordinator.AcceptsZeroAllowanceRelicIncumbentForTesting(root, policy, result))
            throw new InvalidOperationException("Zero-allowance relic victory failed external HP-bound admission.");
        return partial;
    }
}

internal static partial class CombatSearchCoordinator
{
    internal static bool AcceptsZeroAllowanceRelicIncumbentForTesting(CombatRootSnapshot root,
        SearchPolicySnapshot policy, SolverResult result)
        => BuildPrimarySearchIncumbent(root, policy, result) is { StrategicHpDeficit: 0 }
            && BuildRefinementPrimarySearchIncumbent(root, policy, null, result) is { StrategicHpDeficit: 0 };
}
