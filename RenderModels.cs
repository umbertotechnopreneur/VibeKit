using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace WaterRipple;

// Bounded transient render data. No controls are used as a source of settings or state.
internal sealed class EffectFrameState
{
    internal readonly Stopwatch Clock = Stopwatch.StartNew(), StatisticsClock = Stopwatch.StartNew();
    internal readonly List<ActiveRipple> Waves = new(8);
    internal readonly NativeWave[] ShaderWaves = new NativeWave[8];
    internal double NextFrame, NextCapture;
    internal int LastPresents, LastCaptures;

    internal void AddWave(double normalizedX, double normalizedY)
    {
        if (normalizedX < 0 || normalizedY < 0 || normalizedX > 1 || normalizedY > 1) return;
        Waves.Add(new ActiveRipple((float)normalizedX, (float)normalizedY, Clock.Elapsed.TotalSeconds));
        if (Waves.Count > 8) Waves.RemoveAt(0);
    }

    internal void Prepare(AppSettings settings, double now)
    {
        double lifetime = settings.Duration;
        for (int i = Waves.Count - 1; i >= 0; i--) if (now - Waves[i].Created >= lifetime) Waves.RemoveAt(i);
        Array.Clear(ShaderWaves);
        for (int i = 0; i < Waves.Count; i++) ShaderWaves[i] = new NativeWave(Waves[i].X, Waves[i].Y, (float)(now - Waves[i].Created), 1);

    }
    internal readonly record struct ActiveRipple(float X, float Y, double Created);
}
