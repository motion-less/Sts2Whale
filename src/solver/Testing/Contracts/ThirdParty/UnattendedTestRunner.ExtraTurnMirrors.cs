using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class ExtraTurnOrderModel : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => true;
        public readonly List<int> Observed = [];
        public override bool ShouldTakeExtraTurn(Player player)
        {
            Observed.Add(ReadNative(player));
            return false;
        }
        public override Task AfterTakingExtraTurn(Player player)
        {
            Observed.Add(ReadNative(player));
            return Task.CompletedTask;
        }
        private static int ReadNative(Player player)
            => (player.Creature.GetPower<AmbergrisPower>()?.Amount ?? 0)
                + ((bool)AccessTools.Field(typeof(PaelsEye), "_usedThisCombat")
                    .GetValue(player.Relics.OfType<PaelsEye>().Single())! ? 100 : 0);
    }

    private static AbstractModel[] _extraTurnNativeListeners = [];
    private static AbstractModel[] _extraTurnPredictedListeners = [];
    private static bool ExtraTurnNativeListeners(ref IEnumerable<AbstractModel> __result)
    {
        __result = _extraTurnNativeListeners;
        return false;
    }
    private static bool ExtraTurnPredictedListeners(ref IReadOnlyList<AbstractModel> __result)
    {
        __result = _extraTurnPredictedListeners;
        return false;
    }

    private async Task AssertExtraTurnMirrorsAsync(CombatState combat, Player player)
    {
        ExtraTurnMirrors.RegisterShouldTakeExtraTurn<ExtraTurnOrderModel>((model, context) =>
        {
            model.Observed.Add(ReadPredicted(context));
            return false;
        });
        ExtraTurnMirrors.RegisterAfterTakingExtraTurn(typeof(ExtraTurnOrderModel), (model, context) =>
            ((ExtraTurnOrderModel)model).Observed.Add(ReadPredicted(context)));
        var reader = ModelDb.All.OfType<ExtraTurnOrderModel>().Single();
        await ClearOrderedEffectFixtureAsync(combat, player);
        await InjectRelicAsync(player, new() { RelicId = "PAELS_EYE" });
        PaelsEye relic = player.Relics.OfType<PaelsEye>().Single();

        for (int position = 0; position < 3; position++)
        {
            foreach (PowerModel power in player.Creature.Powers.ToArray())
                await PowerCmd.Remove(power);
            AccessTools.Field(typeof(PaelsEye), "_usedThisCombat").SetValue(relic, false);
            await InjectPowerAsync(combat, player, new() { PowerId = "AMBERGRIS_POWER", Target = "Player", Amount = 1 });
            CombatPredictionSimulator parent = CombatRootSnapshot.Capture(combat).ForkSimulator();
            CombatPredictionSimulator child = parent.Fork();
            var shadow = (SimulatedCombatState)child.State.CombatState;
            var initial = CaptureActual(combat, player, combat.Enemies[0]);
            var native = new List<AbstractModel> { player.Creature.GetPower<AmbergrisPower>()!, relic };
            var predicted = new List<AbstractModel>
            {
                shadow.GetPower<AmbergrisPower>(player.Creature)!, shadow.RelicsOf(player).OfType<PaelsEye>().Single()
            };
            native.Insert(position, reader);
            predicted.Insert(position, reader);
            _extraTurnNativeListeners = native.ToArray();
            _extraTurnPredictedListeners = predicted.ToArray();
            Harmony patch = new("CombatSolver.Testing.ExtraTurnOrder." + _request.RunId);
            patch.Patch(AccessTools.Method(typeof(Hook), "IterateCombatHookListeners"),
                prefix: new HarmonyMethod(typeof(UnattendedTestRunner), nameof(ExtraTurnNativeListeners)));
            patch.Patch(AccessTools.Method(typeof(SimulatedCombatState), "GetMirroredHookListeners"),
                prefix: new HarmonyMethod(typeof(UnattendedTestRunner), nameof(ExtraTurnPredictedListeners)));
            try
            {
                reader.Observed.Clear();
                bool predictedTurn = HookMirrors.ShouldTakeExtraTurn(child, shadow, player);
                int[] predictedShould = reader.Observed.ToArray();
                reader.Observed.Clear();
                bool actualTurn = Hook.ShouldTakeExtraTurn(combat, player);
                if (!actualTurn || predictedTurn != actualTurn || !predictedShould.SequenceEqual(reader.Observed)
                    || predictedShould.Length != (position == 0 ? 1 : 0))
                    throw new InvalidOperationException($"Extra-turn qualification order differs at position {position}.");
                reader.Observed.Clear();
                if (!HookMirrors.AfterTakingExtraTurn(child, shadow, player))
                    throw new InvalidOperationException("Extra-turn fixture unexpectedly requested a choice.");
                int[] predictedAfter = reader.Observed.ToArray();
                var expected = CaptureSimulated(child, shadow, player, combat.Enemies[0]);
                AssertSnapshotEqual(initial, CaptureActual(combat, player, combat.Enemies[0]),
                    _request.ScenarioId, "LiveIsolation");
                AssertSnapshotEqual(initial, CaptureSimulated(parent,
                    (SimulatedCombatState)parent.State.CombatState, player, combat.Enemies[0]),
                    _request.ScenarioId, "ForkIsolation");
                reader.Observed.Clear();
                await Hook.AfterTakingExtraTurn(combat, player);
                int expectedObservation = position switch { 0 => 1, 1 => 0, _ => 100 };
                if (!predictedAfter.SequenceEqual(reader.Observed)
                    || !predictedAfter.SequenceEqual([expectedObservation]))
                    throw new InvalidOperationException($"Extra-turn consumption order differs at position {position}: "
                        + $"predicted={string.Join(',', predictedAfter)}, native={string.Join(',', reader.Observed)}.");
                AssertSnapshotEqual(expected, CaptureActual(combat, player, combat.Enemies[0]),
                    _request.ScenarioId, "NativeExtraTurn");
            }
            finally
            {
                patch.UnpatchAll(patch.Id);
                _extraTurnNativeListeners = [];
                _extraTurnPredictedListeners = [];
                reader.Observed.Clear();
            }
        }
        try
        {
            ExtraTurnMirrors.RegisterShouldTakeExtraTurn<ExtraTurnOrderModel>((_, _) => false);
            throw new InvalidOperationException("Late extra-turn registration was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("before root capture", StringComparison.Ordinal)) { }
        _completedChecks.Add("ExtraTurnMirrors:NativeOrder:ThreePositions:ShortCircuit:FullState:Fork:LiveIsolation:Sealed");

        static int ReadPredicted(ExtraTurnMirrorContext context)
            => context.Combat.GetAmount<AmbergrisPower>(context.Player.Creature)
                + (context.Combat.IsPaelsEyeUnused(context.Combat.RelicsOf(context.Player).OfType<PaelsEye>().Single()) ? 0 : 100);
    }
}
