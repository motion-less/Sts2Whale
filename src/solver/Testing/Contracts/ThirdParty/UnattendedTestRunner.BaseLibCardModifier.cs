using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string BaseLibCardModifierTypeName = "BaseLib.Abstracts.CardModifier";

    private static async Task AssertBaseLibCardModifierBoundaryAsync(
        CombatState combat,
        Player player)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("BaseLib CardModifier 边界测试必须从主线程开始。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("BaseLib CardModifier 边界测试找不到玩家战斗状态。");

        Type modifierBaseType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(BaseLibCardModifierTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null)
            ?? throw new InvalidOperationException("完整 Mod 栈没有加载 BaseLib CardModifier。");
        MethodInfo directModifiers = modifierBaseType.GetMethod(
            "DirectModifiers",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(CardModel)],
            modifiers: null)
            ?? throw new MissingMethodException(BaseLibCardModifierTypeName, "DirectModifiers(CardModel)");
        CardModel[] liveCards = playerState.AllCards
            .Where(card => GetDirectModifiers(directModifiers, card).Count == 0)
            .Take(2)
            .ToArray();
        if (liveCards.Length < 2)
            throw new InvalidOperationException("BaseLib CardModifier 边界测试需要至少两张未修饰战斗卡牌。");
        CardModel owner = liveCards[0];
        CardModel initiallyUnmodified = liveCards[1];
        PropertyInfo ownerProperty = modifierBaseType.GetProperty(
            "Owner",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(BaseLibCardModifierTypeName, "Owner");
        PropertyInfo priorityProperty = modifierBaseType.GetProperty(
            "Priority",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(BaseLibCardModifierTypeName, "Priority");
        FieldInfo amountField = modifierBaseType.GetField(
            "<Amount>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(BaseLibCardModifierTypeName, "<Amount>k__BackingField");

        Type fixtureType = CreateBaseLibCardModifierFixtureType(modifierBaseType);
        FieldInfo storeSaveDataCallbackField = fixtureType.GetField(
            "StoreSaveDataCallback",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(fixtureType.FullName, "StoreSaveDataCallback");
        AbstractModel liveModifier = (AbstractModel)RuntimeHelpers.GetUninitializedObject(fixtureType);
        ownerProperty.SetValue(liveModifier, owner);
        amountField.SetValue(liveModifier, 7);
        IList liveModifiers = GetDirectModifiers(directModifiers, owner);
        liveModifiers.Add(liveModifier);

        try
        {
            if (!combat.IterateHookListeners().Contains(liveModifier))
                throw new InvalidOperationException("BaseLib 没有把测试 CardModifier 登记为实机战斗 listener。");

            LiveCombatStamp liveStampBeforeRoot = LiveCombatStamp.Capture(combat);
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            if (!root.CapturedBaseLibCardModifiers)
                throw new InvalidOperationException("根快照没有识别实机 BaseLib CardModifier subscriber。");
            LiveCombatStamp liveStampAfterRoot = LiveCombatStamp.Capture(combat);
            if (liveStampBeforeRoot != root.LiveStamp || root.LiveStamp != liveStampAfterRoot)
            {
                throw new InvalidOperationException(
                    "BaseLib owner 登记改变了未变化实机状态戳，首个搜索会被错误判定为过期。");
            }

            CombatPredictionSimulator parent = await Task.Run(root.ForkSimulator);
            CombatPredictionSimulator child = await Task.Run(parent.Fork);
            SimPlayerCombatState parentPlayer = parent.State.GetPlayerCombatState(player);
            SimPlayerCombatState childPlayer = child.State.GetPlayerCombatState(player);
            PredictedCard parentCard = parentPlayer.FindCard(owner)
                ?? throw new InvalidOperationException("父预测分支没有找到 CardModifier 的 Owner 卡牌。");
            PredictedCard childCard = childPlayer.FindCard(owner)
                ?? throw new InvalidOperationException("子预测分支没有找到 CardModifier 的 Owner 卡牌。");
            PredictedCard parentUnmodifiedCard = parentPlayer.FindCard(initiallyUnmodified)
                ?? throw new InvalidOperationException("父预测分支没有找到未修饰卡牌。");
            PredictedCard childUnmodifiedCard = childPlayer.FindCard(initiallyUnmodified)
                ?? throw new InvalidOperationException("子预测分支没有找到未修饰卡牌。");
            SimulatedCombatState parentCombat = (SimulatedCombatState)parent.State.CombatState;
            SimulatedCombatState childCombat = (SimulatedCombatState)child.State.CombatState;

            if (ReferenceEquals(parentCard.Preview, childCard.Preview)
                || ReferenceEquals(parentUnmodifiedCard.Preview, childUnmodifiedCard.Preview))
            {
                throw new InvalidOperationException(
                    "BaseLib 根的分支卡牌没有在首次 MutablePreview 前隔离侧表 Owner。");
            }

            AbstractModel parentClone = AssertModifierClone(
                parentCombat,
                parentCard.Preview,
                liveModifier,
                fixtureType,
                ownerProperty,
                amountField);
            AbstractModel childClone = AssertModifierClone(
                childCombat,
                childCard.Preview,
                liveModifier,
                fixtureType,
                ownerProperty,
                amountField);
            if (ReferenceEquals(parentClone, childClone))
                throw new InvalidOperationException("父子预测分支复用了同一个 BaseLib CardModifier。");

            PredictedCard generatedEmpty = childUnmodifiedCard.CreateClone();
            childCombat.RegisterGeneratedCombatCard(generatedEmpty);
            try
            {
                CardModel liveGeneratedEmpty = PredictionUtils.CloneModelForSimulation(initiallyUnmodified);
                liveGeneratedEmpty.DeckVersion = null;
                liveGeneratedEmpty.HasBeenRemovedFromState = false;
                liveGeneratedEmpty._cloneOf = initiallyUnmodified;
                liveGeneratedEmpty.ExhaustOnNextPlay = false;
                string predictedGeneratedEmptyKey = CardChoiceSupport.ChoiceCardKey(generatedEmpty);
                string liveGeneratedEmptyKey = CardChoiceSupport.ChoiceCardKey(liveGeneratedEmpty);
                PlanAction generatedEmptyAction = new(
                    PlanActionKind.PlayCard,
                    playerState.TurnNumber,
                    generatedEmpty.Preview.Id.Entry,
                    CardStateKey: predictedGeneratedEmptyKey);
                if (!predictedGeneratedEmptyKey.EndsWith("|baselib=-", StringComparison.Ordinal)
                    || !string.Equals(
                        predictedGeneratedEmptyKey,
                        liveGeneratedEmptyKey,
                        StringComparison.Ordinal)
                    || !ReferenceEquals(
                        SolverController.FindCardForDeployment(
                            [liveGeneratedEmpty],
                            generatedEmptyAction),
                        liveGeneratedEmpty))
                {
                    throw new InvalidOperationException(
                        "BaseLib 根中新生成的空 modifier 卡没有保持实机部署状态键一致。");
                }
            }
            finally
            {
                childCombat.UnregisterGeneratedCombatCard(generatedEmpty);
            }

            PredictedCard gameplayCloneSource = childCard.Clone();
            gameplayCloneSource.MutablePreview.DeckVersion = childCard.Preview;
            gameplayCloneSource.MutablePreview.HasBeenRemovedFromState = true;
            PredictedCard generatedClone = gameplayCloneSource.CreateClone();
            childCombat.RegisterGeneratedCombatCard(generatedClone);
            try
            {
                IList generatedModifiers = GetDirectModifiers(directModifiers, generatedClone.Preview);
                if (generatedModifiers.Count != 1)
                    throw new InvalidOperationException(
                        $"生成牌 BaseLib modifier 数量：expected=1 actual={generatedModifiers.Count}。");
                if (generatedModifiers[0] is not AbstractModel generatedModifier)
                    throw new InvalidOperationException("生成牌 BaseLib modifier 类型应为 AbstractModel。");
                if (ReferenceEquals(generatedModifier, childClone))
                    throw new InvalidOperationException("生成牌 BaseLib modifier 应有独立对象身份。");
                if (!ReferenceEquals(ownerProperty.GetValue(generatedModifier), generatedClone.Preview))
                    throw new InvalidOperationException("生成牌 BaseLib modifier Owner 应指向新牌。");
                if ((int?)amountField.GetValue(generatedModifier) != 7)
                    throw new InvalidOperationException(
                        $"生成牌 BaseLib modifier Amount：expected=7 actual={amountField.GetValue(generatedModifier)}。");
                if (generatedClone.Preview.DeckVersion != null)
                    throw new InvalidOperationException("生成牌 DeckVersion 应为 null。");
                if (generatedClone.Preview.HasBeenRemovedFromState)
                    throw new InvalidOperationException("生成牌 HasBeenRemovedFromState 应为 false。");
                AssertBaseLibGeneratedCloneLifecycle(
                    combat, playerState, childCombat, childPlayer, owner,
                    generatedClone, generatedModifier, directModifiers, ownerProperty, amountField);

                CardModel unregisteredLiveLike = PredictionUtils.CloneModelForSimulation(owner);
                unregisteredLiveLike.DeckVersion = null;
                unregisteredLiveLike.HasBeenRemovedFromState = false;
                unregisteredLiveLike._cloneOf = owner;
                unregisteredLiveLike.ExhaustOnNextPlay = false;
                IList unregisteredModifiers = GetDirectModifiers(
                    directModifiers,
                    unregisteredLiveLike);
                if (unregisteredModifiers.Count != 0)
                {
                    throw new InvalidOperationException(
                        "未登记实机生成卡在测试挂载 modifier 前已有意外侧表状态。");
                }
                AbstractModel unregisteredModifier = CreateBaseLibCardModifierFixture(
                    fixtureType,
                    unregisteredLiveLike,
                    amount: 7,
                    ownerProperty,
                    amountField);
                unregisteredModifiers.Add(unregisteredModifier);
                try
                {
                    string predictedGeneratedKey = CardChoiceSupport.ChoiceCardKey(generatedClone);
                    string liveGeneratedKey = CardChoiceSupport.ChoiceCardKey(unregisteredLiveLike);
                    CardModel discoveredClone = PredictionUtils.CloneCardStateForSimulation(
                        unregisteredLiveLike);
                    IList discoveredCloneModifiers = GetDirectModifiers(
                        directModifiers,
                        discoveredClone);
                    if (!string.Equals(
                            predictedGeneratedKey,
                            liveGeneratedKey,
                            StringComparison.Ordinal)
                        || discoveredCloneModifiers.Count != 1
                        || discoveredCloneModifiers[0] is not AbstractModel discoveredModifier
                        || ReferenceEquals(discoveredModifier, unregisteredModifier)
                        || !ReferenceEquals(ownerProperty.GetValue(discoveredModifier), discoveredClone)
                        || (int?)amountField.GetValue(discoveredModifier) != 7)
                    {
                        throw new InvalidOperationException(
                            "未登记实机生成卡的 BaseLib modifier 没有被状态键惰性发现并纳入后续克隆。");
                    }
                }
                finally
                {
                    unregisteredModifiers.Remove(unregisteredModifier);
                    ownerProperty.SetValue(unregisteredModifier, null);
                }
            }
            finally
            {
                childCombat.UnregisterGeneratedCombatCard(generatedClone);
            }

            IList parentModifiers = GetDirectModifiers(directModifiers, parentCard.Preview);
            IList childModifiers = GetDirectModifiers(directModifiers, childCard.Preview);
            IList parentInitiallyEmpty = GetDirectModifiers(directModifiers, parentUnmodifiedCard.Preview);
            IList childInitiallyEmpty = GetDirectModifiers(directModifiers, childUnmodifiedCard.Preview);
            if (ReferenceEquals(parentModifiers, childModifiers)
                || ReferenceEquals(parentInitiallyEmpty, childInitiallyEmpty)
                || parentInitiallyEmpty.Count != 0
                || childInitiallyEmpty.Count != 0)
            {
                throw new InvalidOperationException("BaseLib DirectModifiers 侧表没有按预测分支隔离。");
            }
            childUnmodifiedCard.SetCachedFingerprint(1, 2);
            childUnmodifiedCard.OwnerPile?.SetCachedFingerprint(3, 4);
            if (childUnmodifiedCard.TryGetCachedFingerprint(out _, out _)
                || childUnmodifiedCard.OwnerPile?.TryGetCachedFingerprint(out _, out _) == true)
            {
                throw new InvalidOperationException(
                    "BaseLib 外部可变状态仍错误复用了卡牌或牌堆 fingerprint 缓存。");
            }
            StateFingerprint parentOtherFingerprintBefore =
                CombatBeamSolver.CaptureCardStateFingerprintForTesting(parentUnmodifiedCard);
            StateFingerprint childOtherFingerprintBefore =
                CombatBeamSolver.CaptureCardStateFingerprintForTesting(childUnmodifiedCard);
            if (parentOtherFingerprintBefore != childOtherFingerprintBefore)
            {
                throw new InvalidOperationException(
                    "等价 BaseLib 父子卡牌在修改前产生了不同的语义 fingerprint。");
            }
            string parentOtherChoiceKeyBefore = CardChoiceSupport.ChoiceCardKey(parentUnmodifiedCard);
            string childOtherChoiceKeyBefore = CardChoiceSupport.ChoiceCardKey(childUnmodifiedCard);
            ContinuationStamp parentContinuationBefore = ContinuationStamp.CapturePredicted(
                player,
                parent,
                playerState.TurnNumber,
                root.Forecast,
                root.StartTurnNumber);
            ContinuationStamp childContinuationBefore = ContinuationStamp.CapturePredicted(
                player,
                child,
                playerState.TurnNumber,
                root.Forecast,
                root.StartTurnNumber);
            if (!string.Equals(parentOtherChoiceKeyBefore, childOtherChoiceKeyBefore, StringComparison.Ordinal)
                || parentContinuationBefore != childContinuationBefore)
            {
                throw new InvalidOperationException(
                    "等价 BaseLib 父子分支在修改前产生了不同的选牌键或 continuation。");
            }

            AbstractModel childOnlyModifier = CreateBaseLibCardModifierFixture(
                fixtureType,
                childCard.Preview,
                amount: 11,
                ownerProperty,
                amountField);
            AbstractModel firstModifierOnOtherCard = CreateBaseLibCardModifierFixture(
                fixtureType,
                childUnmodifiedCard.Preview,
                amount: 13,
                ownerProperty,
                amountField);
            childInitiallyEmpty.Add(firstModifierOnOtherCard);
            try
            {
                StateFingerprint childOtherFingerprintAfterAdd =
                    CombatBeamSolver.CaptureCardStateFingerprintForTesting(childUnmodifiedCard);
                StateFingerprint parentOtherFingerprintAfterChildAdd =
                    CombatBeamSolver.CaptureCardStateFingerprintForTesting(parentUnmodifiedCard);
                if (childOtherFingerprintAfterAdd == childOtherFingerprintBefore
                    || parentOtherFingerprintAfterChildAdd != parentOtherFingerprintBefore)
                {
                    throw new InvalidOperationException(
                        "BaseLib 首次挂载 CardModifier 没有只改变子分支 fingerprint。");
                }
                string childOtherChoiceKeyAfterAdd = CardChoiceSupport.ChoiceCardKey(childUnmodifiedCard);
                ContinuationStamp childContinuationAfterAdd = ContinuationStamp.CapturePredicted(
                    player,
                    child,
                    playerState.TurnNumber,
                    root.Forecast,
                    root.StartTurnNumber);
                ContinuationStamp parentContinuationAfterChildAdd = ContinuationStamp.CapturePredicted(
                    player,
                    parent,
                    playerState.TurnNumber,
                    root.Forecast,
                    root.StartTurnNumber);
                if (string.Equals(
                        childOtherChoiceKeyAfterAdd,
                        childOtherChoiceKeyBefore,
                        StringComparison.Ordinal)
                    || childContinuationAfterAdd == childContinuationBefore
                    || parentContinuationAfterChildAdd != parentContinuationBefore)
                {
                    throw new InvalidOperationException(
                        "BaseLib 首次挂载 CardModifier 没有进入选牌键或分支 continuation。");
                }
                amountField.SetValue(firstModifierOnOtherCard, 17);
                StateFingerprint childOtherFingerprintAfterAmount =
                    CombatBeamSolver.CaptureCardStateFingerprintForTesting(childUnmodifiedCard);
                string childOtherChoiceKeyAfterAmount = CardChoiceSupport.ChoiceCardKey(childUnmodifiedCard);
                ContinuationStamp childContinuationAfterAmount = ContinuationStamp.CapturePredicted(
                    player,
                    child,
                    playerState.TurnNumber,
                    root.Forecast,
                    root.StartTurnNumber);
                if (childOtherFingerprintAfterAmount == childOtherFingerprintAfterAdd
                    || string.Equals(
                        childOtherChoiceKeyAfterAmount,
                        childOtherChoiceKeyAfterAdd,
                        StringComparison.Ordinal)
                    || childContinuationAfterAmount == childContinuationAfterAdd)
                {
                    throw new InvalidOperationException(
                        "BaseLib CardModifier Amount 变化没有进入 fingerprint、选牌键或 continuation。");
                }
                priorityProperty.SetValue(firstModifierOnOtherCard, 5);
                StateFingerprint childOtherFingerprintAfterPriority =
                    CombatBeamSolver.CaptureCardStateFingerprintForTesting(childUnmodifiedCard);
                string childOtherChoiceKeyAfterPriority = CardChoiceSupport.ChoiceCardKey(childUnmodifiedCard);
                ContinuationStamp childContinuationAfterPriority = ContinuationStamp.CapturePredicted(
                    player,
                    child,
                    playerState.TurnNumber,
                    root.Forecast,
                    root.StartTurnNumber);
                if (childOtherFingerprintAfterPriority == childOtherFingerprintAfterAmount
                    || string.Equals(
                        childOtherChoiceKeyAfterPriority,
                        childOtherChoiceKeyAfterAmount,
                        StringComparison.Ordinal)
                    || childContinuationAfterPriority == childContinuationAfterAmount)
                {
                    throw new InvalidOperationException(
                        "BaseLib CardModifier Priority 变化没有进入 fingerprint、选牌键或 continuation。");
                }

                childModifiers.Add(childOnlyModifier);
                IReadOnlyList<AbstractModel> childListenersAfterAdd =
                    ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
                IReadOnlyList<AbstractModel> parentListenersAfterAdd =
                    ((ICombatPredictionHookListenerSource)parentCombat).HookListeners;
                if (!childListenersAfterAdd.Contains(childOnlyModifier)
                    || !childListenersAfterAdd.Contains(firstModifierOnOtherCard)
                    || parentListenersAfterAdd.Contains(childOnlyModifier)
                    || parentListenersAfterAdd.Contains(firstModifierOnOtherCard))
                {
                    throw new InvalidOperationException(
                        "BaseLib 动态添加或首次挂载 CardModifier 后 listener 没有按分支重建。");
                }

                childModifiers.Remove(childClone);
                IReadOnlyList<AbstractModel> childListenersAfterRemove =
                    ((ICombatPredictionHookListenerSource)childCombat).HookListeners;
                IReadOnlyList<AbstractModel> parentListenersAfterRemove =
                    ((ICombatPredictionHookListenerSource)parentCombat).HookListeners;
                if (childListenersAfterRemove.Contains(childClone)
                    || !childListenersAfterRemove.Contains(childOnlyModifier)
                    || !parentListenersAfterRemove.Contains(parentClone))
                {
                    throw new InvalidOperationException(
                        "BaseLib 动态移除 CardModifier 后 listener 没有精确、隔离地重建。");
                }
            }
            finally
            {
                childModifiers.Remove(childOnlyModifier);
                if (!childModifiers.Contains(childClone))
                    childModifiers.Add(childClone);
                childInitiallyEmpty.Remove(firstModifierOnOtherCard);
                ownerProperty.SetValue(childOnlyModifier, null);
                ownerProperty.SetValue(firstModifierOnOtherCard, null);
                if (CombatBeamSolver.CaptureCardStateFingerprintForTesting(childUnmodifiedCard)
                    != childOtherFingerprintBefore)
                {
                    throw new InvalidOperationException(
                        "BaseLib CardModifier 移除后卡牌 fingerprint 没有恢复。");
                }
                if (!string.Equals(
                        CardChoiceSupport.ChoiceCardKey(childUnmodifiedCard),
                        childOtherChoiceKeyBefore,
                        StringComparison.Ordinal)
                    || ContinuationStamp.CapturePredicted(
                        player,
                        child,
                        playerState.TurnNumber,
                        root.Forecast,
                        root.StartTurnNumber) != childContinuationBefore)
                {
                    throw new InvalidOperationException(
                        "BaseLib CardModifier 移除后选牌键或 continuation 没有恢复。");
                }
            }

            AbstractModel addedDuringSave = CreateBaseLibCardModifierFixture(
                fixtureType,
                owner,
                amount: 19,
                ownerProperty,
                amountField);
            string choiceKeyBeforeReentrantMutation = CardChoiceSupport.ChoiceCardKey(owner);
            int storeSaveDataCallbacks = 0;
            storeSaveDataCallbackField.SetValue(null, (Action)(() =>
            {
                Interlocked.Increment(ref storeSaveDataCallbacks);
                storeSaveDataCallbackField.SetValue(null, null);
                liveModifiers.Add(addedDuringSave);
            }));
            try
            {
                string reentrantChoiceKey = CardChoiceSupport.ChoiceCardKey(owner);
                if (storeSaveDataCallbacks != 1
                    || liveModifiers.Count != 2
                    || !string.Equals(
                        reentrantChoiceKey,
                        choiceKeyBeforeReentrantMutation,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "BaseLib StoreSaveData 重入修改侧表时没有保持回调前的稳定快照。");
                }
            }
            finally
            {
                storeSaveDataCallbackField.SetValue(null, null);
                liveModifiers.Remove(addedDuringSave);
                ownerProperty.SetValue(addedDuringSave, null);
            }
        }
        finally
        {
            storeSaveDataCallbackField.SetValue(null, null);
            liveModifiers.Remove(liveModifier);
            ownerProperty.SetValue(liveModifier, null);
        }
    }

    private static void AssertBaseLibGeneratedCloneLifecycle(
        CombatState liveCombat,
        PlayerCombatState livePlayer,
        SimulatedCombatState predictedCombat,
        SimPlayerCombatState predictedPlayer,
        CardModel liveSource,
        PredictedCard predictedClone,
        AbstractModel predictedModifier,
        MethodInfo directModifiers,
        PropertyInfo ownerProperty,
        FieldInfo amountField)
    {
        LiveCombatStamp liveBefore = LiveCombatStamp.Capture(liveCombat);
        CardModel liveClone = liveSource.CreateClone();
        try
        {
            IList liveModifiers = GetDirectModifiers(directModifiers, liveClone);
            if (liveModifiers.Count != 1
                || liveModifiers[0] is not AbstractModel liveModifier
                || ReferenceEquals(liveModifier, GetDirectModifiers(directModifiers, liveSource)[0])
                || !ReferenceEquals(ownerProperty.GetValue(liveModifier), liveClone)
                || (int?)amountField.GetValue(liveModifier) != 7
                || liveClone.DeckVersion != null
                || liveClone.HasBeenRemovedFromState)
            {
                throw new InvalidOperationException("原生生成牌应独立复制 BaseLib modifier 并重置生成卡字段。");
            }
            if (!string.Equals(
                    CardChoiceSupport.ChoiceCardKey(predictedClone),
                    CardChoiceSupport.ChoiceCardKey(liveClone),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("预测与原生生成牌应具有相同的 BaseLib 状态键。");
            }

            AssertListeners(expectedCount: 0, "created");
            // BaseLib's native subscriber enumerates AllPiles. Scope registration owns
            // the card; pile membership determines when its modifier receives hooks.
            for (int index = 0; index < livePlayer.AllPiles.Count; index++)
            {
                CardPile livePile = livePlayer.AllPiles[index];
                SimCardPile predictedPile = predictedPlayer.AllPiles[index];
                if (livePile.Type != predictedPile.Type)
                    throw new InvalidOperationException("原生与预测战斗牌堆应按相同顺序排列。");
                predictedClone.OwnerPile?.Remove(predictedClone);
                liveClone.Pile?.RemoveInternal(liveClone, silent: true);
                predictedPile.Add(predictedClone);
                livePile.AddInternal(liveClone, silent: true);
                AssertListeners(expectedCount: 1, $"pile={livePile.Type}");
            }
            predictedClone.OwnerPile!.Remove(predictedClone);
            liveClone.Pile!.RemoveInternal(liveClone, silent: true);
            AssertListeners(expectedCount: 0, "removed_from_piles");

            void AssertListeners(int expectedCount, string stage)
            {
                int liveCount = liveCombat.IterateHookListeners()
                    .Count(listener => ReferenceEquals(listener, liveModifier));
                int predictedCount = ((ICombatPredictionHookListenerSource)predictedCombat).HookListeners
                    .Count(listener => ReferenceEquals(listener, predictedModifier));
                if (liveCount != expectedCount || predictedCount != liveCount)
                {
                    throw new InvalidOperationException(
                        $"BaseLib 生成牌 listener 生命周期：stage={stage} " +
                        $"expected={expectedCount} live={liveCount} predicted={predictedCount}。");
                }
            }
        }
        finally
        {
            predictedClone.OwnerPile?.Remove(predictedClone);
            liveClone.Pile?.RemoveInternal(liveClone, silent: true);
            liveCombat.RemoveCard(liveClone);
        }
        if (LiveCombatStamp.Capture(liveCombat) != liveBefore)
            throw new InvalidOperationException("BaseLib 生成牌生命周期合同结束后实机状态应恢复。");
    }

    private static AbstractModel AssertModifierClone(
        SimulatedCombatState combat,
        CardModel expectedOwner,
        AbstractModel liveModifier,
        Type fixtureType,
        PropertyInfo ownerProperty,
        FieldInfo amountField)
    {
        AbstractModel clone = ((ICombatPredictionHookListenerSource)combat).HookListeners
            .Single(listener => listener.GetType() == fixtureType);
        if (ReferenceEquals(clone, liveModifier)
            || !ReferenceEquals(ownerProperty.GetValue(clone), expectedOwner)
            || (int?)amountField.GetValue(clone) != 7)
        {
            throw new InvalidOperationException(
                "BaseLib CardModifier 没有以独立克隆、正确 Owner 和原状态进入预测 listener。");
        }
        return clone;
    }

    private static IList GetDirectModifiers(MethodInfo directModifiers, CardModel card)
        => directModifiers.Invoke(null, [card]) as IList
            ?? throw new InvalidOperationException("BaseLib DirectModifiers 没有返回 IList。");

    private static AbstractModel CreateBaseLibCardModifierFixture(
        Type fixtureType,
        CardModel owner,
        int amount,
        PropertyInfo ownerProperty,
        FieldInfo amountField)
    {
        AbstractModel modifier = (AbstractModel)RuntimeHelpers.GetUninitializedObject(fixtureType);
        ownerProperty.SetValue(modifier, owner);
        amountField.SetValue(modifier, amount);
        return modifier;
    }

    private static Type CreateBaseLibCardModifierFixtureType(Type baseType)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"CombatSolver.BaseLibFixture.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        TypeBuilder type = assembly.DefineDynamicModule("Fixture")
            .DefineType(
                "CombatSolver.UnattendedBaseLibCardModifier",
                TypeAttributes.Public | TypeAttributes.Sealed,
                baseType);
        FieldBuilder storeSaveDataCallback = type.DefineField(
            "StoreSaveDataCallback",
            typeof(Action),
            FieldAttributes.Public | FieldAttributes.Static);
        Type modifierSaveType = baseType.GetNestedType("ModifierSave", BindingFlags.Public)
            ?? throw new MissingMemberException(baseType.FullName, "ModifierSave");
        MethodInfo storeSaveData = baseType.GetMethod(
            "StoreSaveData",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [modifierSaveType],
            modifiers: null)
            ?? throw new MissingMethodException(baseType.FullName, "StoreSaveData(ModifierSave)");
        MethodBuilder storeSaveDataOverride = type.DefineMethod(
            storeSaveData.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void),
            [modifierSaveType]);
        ILGenerator storeSaveDataIl = storeSaveDataOverride.GetILGenerator();
        Label noCallback = storeSaveDataIl.DefineLabel();
        storeSaveDataIl.Emit(OpCodes.Ldsfld, storeSaveDataCallback);
        storeSaveDataIl.Emit(OpCodes.Dup);
        storeSaveDataIl.Emit(OpCodes.Brfalse_S, noCallback);
        storeSaveDataIl.Emit(
            OpCodes.Callvirt,
            typeof(Action).GetMethod(nameof(Action.Invoke))
                ?? throw new MissingMethodException(typeof(Action).FullName, nameof(Action.Invoke)));
        storeSaveDataIl.Emit(OpCodes.Ret);
        storeSaveDataIl.MarkLabel(noCallback);
        storeSaveDataIl.Emit(OpCodes.Pop);
        storeSaveDataIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(storeSaveDataOverride, storeSaveData);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        return type.CreateType();
    }
}
