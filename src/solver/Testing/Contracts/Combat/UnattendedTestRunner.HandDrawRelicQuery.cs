using System.Reflection;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOwnedHandDrawRelicQueriesAsync(CombatState live, Player player)
    {
        // Query contract: seeded native inputs, no claim about replaying their lifecycle.
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        Func<RelicModel>[] factories =
        [
            () => ModelDb.Relic<BagOfPreparation>().ToMutable(),
            () => ModelDb.Relic<BigMushroom>().ToMutable(),
            () => ModelDb.Relic<BoomingConch>().ToMutable(),
            () => ModelDb.Relic<RingOfTheDrake>().ToMutable(),
            () => ModelDb.Relic<RingOfTheSnake>().ToMutable(),
            () => ModelDb.Relic<Pendulum>().ToMutable(),
            () => ModelDb.Relic<Pocketwatch>().ToMutable(),
            () => ModelDb.Relic<PollinousCore>().ToMutable(),
        ];
        PropertyInfo turnProperty = typeof(PlayerCombatState).GetProperty(nameof(PlayerCombatState.TurnNumber))!;
        List<object> rows = [];
        foreach (var create in factories)
        {
            RelicModel relic = create();
            player.AddRelicInternal(relic);
            int[] counters = (relic is Pendulum or Pocketwatch or PollinousCore) ? [0, 2, 3, 4] : [0];
            foreach (int turn in new[] { 1, 2, 3, 4 })
            foreach (int counter in counters)
            {
                turnProperty.SetValue(player.PlayerCombatState!, turn);
                SetCounter(counter);
                CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
                var parent = root.ForkSimulator();
                var combat = (SimulatedCombatState)parent.State.CombatState;
                string parentBefore = DescribeContinuationContractState(parent, root, player);
                int native = Math.Max(0, (int)Hook.ModifyHandDraw(live, player, 5, out _));
                int predicted = PersistentPowerSupport.GetModifiedHandDraw(combat, player, 5);
                if (predicted != native) throw new InvalidOperationException($"{relic.Id} turn{turn}/counter{counter}: native={native}, predicted={predicted}");
                player.PlayerCombatState!.IncrementTurnNumber();
                SetCounter(counter + 1);
                if (PersistentPowerSupport.GetModifiedHandDraw(combat, player, 5) != predicted)
                    throw new InvalidOperationException($"{relic.Id} read advanced live input.");
                var children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
                await Task.WhenAll(children.Select(child => Task.Run(() =>
                {
                    using var isolation = SimulationNotificationIsolation.Enter();
                    if (PersistentPowerSupport.GetModifiedHandDraw((SimulatedCombatState)child.State.CombatState, player, 5) != predicted
                        || DescribeContinuationContractState(child, root, player) != parentBefore)
                        throw new InvalidOperationException("Hand draw Fork state/RNG differed.");
                })));
                if (DescribeContinuationContractState(parent, root, player) != parentBefore)
                    throw new InvalidOperationException("Hand draw query changed frozen parent.");
                rows.Add(new { Relic = relic.Id.Entry, Turn = turn, Counter = counter, Native = native, Predicted = predicted });
            }
            await RelicCmd.Remove(relic);
            void SetCounter(int value)
            {
                if (relic is Pendulum pendulum) pendulum.TurnsSeen = value;
                if (relic is PollinousCore core) core.TurnsSeen = value;
                if (relic is Pocketwatch)
                    typeof(Pocketwatch).GetField("_cardsPlayedLastTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relic, value);
            }
        }
        _writer.WriteGeneratedArtifact("owned-hand-draw-relic-queries.json", rows);
        _completedChecks.Add("HandDraw:EightNativeRelics:68TurnCounterQueries:FrozenRootAfterLiveAdvance:16OwnedForks:FullStateParentRng");
    }
}
