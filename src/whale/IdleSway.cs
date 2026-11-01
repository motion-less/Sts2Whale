using System;

namespace Sts2Whale.Whale;

/// <summary>
/// idle 摆头：从 AI_Vtuber 那版 <c>control_movement()</c> 移植过来的。
///
/// 原来是三根正弦前身的版本看起来像机器人在点头，所以这里保留原来的做法：
/// Perlin 噪声打底 + 每步 50% 概率反向 + 弹簧惯性 + 微抖 + 10% 概率停顿一下。
/// 纯计算，不碰 Godot，10Hz 更新一次。
/// </summary>
internal sealed class IdleSway
{
    private const int StepMs = 100;

    private readonly Random _rng = new();
    private readonly double[] _current = new double[3];   // 噪声算出来的目标
    private readonly double[] _velocity = new double[3];
    private readonly double[] _smooth = new double[3];    // 真正发出去的值（朝目标慢慢追）
    private readonly double[] _prevTarget = new double[3]; // 上一次的目标，用来限速
    private readonly int[] _direction = { 1, 1, 1 };

    private double _t;
    private long _lastStepAt;
    private long _pauseUntil;

    // 三轴的最大幅度（角度）和随机抖动，跟原版一致
    private static readonly double[] Limit = { 15, 20, 25 };
    private static readonly double[] NoiseGain = { 10, 20, 25 };
    private static readonly double[] Jitter = { 3, 5, 8 };

    /// <summary>软限幅的拐点（度）。越大越接近原始幅度。</summary>
    public double SoftLimit { get; set; } = 9;

    /// <summary>每 100ms 目标最多变多少度（防止一步甩太远）。</summary>
    public double MaxStepDegrees { get; set; } = 3.5;

    /// <summary>抬头（FaceAngleY 正方向）的最大值：真人直播几乎不抬头，压得很狠。</summary>
    public double UpLimit { get; set; } = 2.0;

    /// <summary>低头（Y 负方向）的最大值。</summary>
    public double DownLimit { get; set; } = 9.0;

    /// <summary>
    /// 每个 tick 调一次：噪声目标每 100ms 重算一次，输出值每个 tick 朝目标追一点
    /// （<paramref name="smoothing"/> 越小越软、也越迟钝）。
    /// </summary>
    public bool TryStep(long nowMs, double amplitude, double smoothing, out float x, out float y, out float z)
    {
        if (nowMs >= _pauseUntil && (_lastStepAt == 0 || nowMs - _lastStepAt >= StepMs))
        {
            _lastStepAt = nowMs;
            StepTargets(nowMs);
        }

        var k = Math.Clamp(smoothing, 0.02, 1.0);
        for (var i = 0; i < 3; i++) _smooth[i] += (_current[i] - _smooth[i]) * k;

        x = (float)(_smooth[0] * amplitude);
        y = (float)(_smooth[1] * amplitude);
        z = (float)(_smooth[2] * amplitude);
        return true;
    }

    private void StepTargets(long nowMs)
    {
        _t += 0.02;

        for (var i = 0; i < 3; i++)
        {
            if (_rng.NextDouble() < 0.5) _direction[i] *= -1;

            var target = _direction[i] * Noise(_t + i * 100) * NoiseGain[i]
                         + (_rng.NextDouble() * 2 - 1) * Jitter[i];
            target = Math.Clamp(target, -Limit[i], Limit[i]);

            // 微抖，让静止时也不是死的
            target += Noise(_t * 8) * 1.2;

            // 软限幅：大角度被压回去（tanh），小角度几乎不变，避免偶尔甩到 20 度
            // Y 轴（抬头/低头）上单独处理：抬头几乎不出现，低头可以多一点
            double soft;
            if (i == 1) soft = Math.Max(1.0, target >= 0 ? UpLimit : DownLimit);
            else soft = Math.Max(4.0, SoftLimit);
            target = soft * Math.Tanh(target / soft);

            // 限速：一步最多挪 MaxStepDegrees，大摆动拆成几步走
            var step = Math.Max(0.5, MaxStepDegrees);
            target = Math.Clamp(target, _prevTarget[i] - step, _prevTarget[i] + step);
            _prevTarget[i] = target;

            const double inertia = 0.75;
            _velocity[i] = _velocity[i] * inertia + (target - _current[i]) * (1 - inertia);
            _current[i] += _velocity[i];
        }

        // 偶尔停一下，看起来像在"看"什么东西
        if (_rng.NextDouble() < 0.1) _pauseUntil = nowMs + (long)(300 + _rng.NextDouble() * 700);
    }

    // ---------------------------------------------------------------- 1D Perlin

    private readonly double[] _gradients = BuildGradients();

    private static double[] BuildGradients()
    {
        var rng = new Random(20261031);
        var table = new double[256];
        for (var i = 0; i < table.Length; i++) table[i] = rng.NextDouble() * 2 - 1;
        return table;
    }

    /// <summary>一维 Perlin 噪声，输出大致在 -1..1</summary>
    private double Noise(double x)
    {
        var i = (int)Math.Floor(x);
        var f = x - i;
        var u = f * f * f * (f * (f * 6 - 15) + 10); // smootherstep

        var a = _gradients[((i % 256) + 256) % 256];
        var b = _gradients[(((i + 1) % 256) + 256) % 256];

        return a + (b - a) * u;
    }
}
