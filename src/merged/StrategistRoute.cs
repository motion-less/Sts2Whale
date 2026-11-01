using System;
using System.Collections.Generic;
using System.Linq;
using SpireStrategist.Mod;

namespace Sts2AgentBridge;

/// <summary>
/// 路线决策交给全局战略助手：直接调它的 <c>RoutePolicy</c>（通过 <c>RouteAdapter</c> 的地图读取），
/// 不依赖地图界面是否打开，也不启用它那套会卡死的 autopilot。
/// </summary>
public static class StrategistRoute
{
    public static readonly bool Enabled =
        System.Environment.GetEnvironmentVariable("STS2_STRATEGIST_DELEGATE") != "0";

    /// <summary>返回它推荐的下一格（col,row）和一句理由；算不出来返回 false。</summary>
    public static bool TryRecommend(out int col, out int row, out string reason)
    {
        col = row = -1;
        reason = null;

        try
        {
            if (!Enabled) return false;
            var service = AdvisorService.Current;
            if (service == null) return false;

            var run = service.State;
            var snapshot = SnapshotReader.Read(run, service.Player);
            if (snapshot.Map?.Error is { } mapError)
            {
                reason = "地图读取失败：" + mapError;
                return false;
            }

            var nodes = RouteAdapter.Nodes(run);
            var economy = RouteAdapter.Economy(service);
            var current = run.CurrentMapPoint ?? run.Map.StartingMapPoint;
            var currentKey = RouteAdapter.Key(current);
            var roots = nodes.FirstOrDefault(n => n.Id == currentKey)?.Children ?? Array.Empty<string>();
            if (roots.Length == 0) return false;

            var eval = SpireStrategist.Core.RoutePolicy.Evaluate(snapshot, nodes,
                roots.Concat(economy.FlightRoots).Distinct().ToArray(), economy);

            var node = eval.RecommendedNode;
            if (string.IsNullOrEmpty(node)) return false;

            var parts = node.Split(',');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out col) || !int.TryParse(parts[1], out row)) return false;

            var option = eval.Options.FirstOrDefault(o => o.Node == node);
            var reasons = option?.Reasons ?? Array.Empty<string>();
            var kind = option?.Kind;
            var top = reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Take(2).ToArray();
            reason = top.Length == 0
                ? $"战略助手选了 {kind}（{node}）"
                : $"战略助手选了 {kind}（{node}）：" + string.Join("，", top);
            return true;
        }
        catch (Exception e)
        {
            reason = "战略助手算路线失败：" + e.GetType().Name + ": " + e.Message;
            return false;
        }
    }
}
