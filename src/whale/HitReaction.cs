using System;

namespace Sts2Whale.Whale;

/// <summary>
/// 受击倾斜：像不倒翁一样往右歪一下再弹回来。
///
/// 这个皮套的头部绑定几乎不响应 FaceAngle（注入 90° 只偏几像素），
/// 所以改用 VTS 的 <c>MoveModelRequest</c> 相对旋转整个模型——视觉上就是被打歪了。
/// 阻尼弹簧：受击加一个角速度，之后靠弹簧+阻尼回正，会有一点过冲。
/// </summary>
internal sealed class HitReaction
{
    private const double Omega = 6.0;        // 弹簧角频率（越大回得越快）
    private const double Zeta = 0.28;        // 阻尼比（越小越晃）
    private const double ImpulseGain = 8.5;  // 1 点伤害 → 多少角速度，标定后 40 点≈40°

    private double _angle;      // 当前倾斜（度，正 = 向右）
    private double _velocity;
    private long _lastMs;

    /// <summary>受击：伤害越高冲得越远，连击会累加，但不超过 maxDegrees。</summary>
    public void Hit(double damage, double degreesPerDamage, double maxDegrees)
    {
        var want = Math.Min(maxDegrees, Math.Max(0, damage) * Math.Max(0.1, degreesPerDamage));
        _velocity += want * ImpulseGain;
        // 已经歪到头了就别再加速，避免卡在 40° 一直不回来
        if (_angle >= maxDegrees && _velocity > 0) _velocity *= 0.2;
    }

    /// <summary>
    /// 推进一帧，返回当前应该设置的**绝对角度**（度）。
    /// 绝对模式没有累积误差，回正就是真的 0。
    /// </summary>
    public double Tick(long nowMs, double maxDegrees, out double currentAngle)
    {
        if (_lastMs == 0) { _lastMs = nowMs; currentAngle = _angle; return 0; }
        var dt = Math.Min(0.1, (nowMs - _lastMs) / 1000.0);
        _lastMs = nowMs;

        // 弹簧回正：a = -ω²x - 2ζωv
        var accel = -Omega * Omega * _angle - 2 * Zeta * Omega * _velocity;
        _velocity += accel * dt;
        _angle += _velocity * dt;

        // 向右为正，最多 40°；允许一点点向左的过冲（弹簧感），但不许往左太多
        _angle = Math.Clamp(_angle, -6, Math.Max(1, maxDegrees));

        if (Math.Abs(_angle) < 0.05 && Math.Abs(_velocity) < 0.5)
        {
            _angle = 0;
            _velocity = 0;
        }

        currentAngle = _angle;
        return _angle;
    }

    /// <summary>弹簧是否已经完全停下（用来触发一次绝对归零）。</summary>
    public bool Settled => _angle == 0 && _velocity == 0;

    /// <summary>外部重置（比如退出战斗）。</summary>
    public void Reset()
    {
        _angle = 0;
        _velocity = 0;
    }
}
