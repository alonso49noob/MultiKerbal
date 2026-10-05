using MultiKerbal.Common.Vessels;

namespace MultiKerbal.Tests;

public sealed class PuppetMotionTests
{
    private const int Kerbin = 1;
    private const double KerbinRadius = 600_000;
    private const double Step = 1.0 / 30.0;

    /// <summary>Un grado de latitud en Kerbin, en metros.</summary>
    private const double MetersPerDegree = KerbinRadius * Math.PI / 180.0;

    /// <summary>Tiempo real en que ya se ha absorbido la corrección del último estado (llegan en el segundo 10).</summary>
    private const double Later = 20;

    private static SurfacePose At(double latitude, double longitude = 0, double altitude = 70, Rotation? rotation = null) => new()
    {
        Latitude = latitude,
        Longitude = longitude,
        Altitude = altitude,
        Rotation = rotation ?? Rotation.Identity,
    };

    private static void Add(PuppetMotion motion, double sampleUt, SurfacePose pose, double ut, double realTime, double warp = 1) =>
        motion.Add(Kerbin, sampleUt, pose, ut, realTime, warp, KerbinRadius);

    [Fact]
    public void SingleState_IsShownAsIs()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(1, 2, 3), 100.1, 0);

        SurfacePose pose = motion.Current(100.1, 0, 1);

        Assert.Equal(1, pose.Latitude, 9);
        Assert.Equal(2, pose.Longitude, 9);
        Assert.Equal(3, pose.Altitude, 9);
    }

    [Fact]
    public void MovingVessel_IsCarriedForwardToLocalTime()
    {
        // 0,001° cada 1/30 s hacia el norte (unos 315 m/s... da igual: es lineal).
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(0.001), 100 + Step, 10);

        // 0,1 s después del último estado (latencia): tres pasos más allá.
        SurfacePose pose = motion.Current(100 + Step + 0.1, Later, 1);

        Assert.Equal(0.004, pose.Latitude, 6);
    }

    [Fact]
    public void Prediction_StopsAfterMaxAhead()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(0.001), 100 + Step, 10);

        // Sin estados nuevos durante 5 s no sigue avanzando: se queda a 0,25 s del último.
        SurfacePose pose = motion.Current(100 + Step + 5, Later, 1);

        Assert.Equal(0.001 + (0.001 * PuppetMotion.MaxAheadRealSeconds / Step), pose.Latitude, 6);
    }

    [Fact]
    public void LocalTimeBehind_InterpolatesBetweenStates()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(0.001), 100 + Step, 10);

        SurfacePose pose = motion.Current(100 + (Step / 2), Later, 1);

        Assert.Equal(0.0005, pose.Latitude, 9);
    }

    [Fact]
    public void FarApartStates_DoNotGiveVelocity()
    {
        // Sobre raíles llega un estado cada 5 s: no hay velocidad que adelantar.
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 105, At(0.001), 105, 10);

        Assert.Equal(0.001, motion.Current(105.2, Later, 1).Latitude, 9);
    }

    [Fact]
    public void InPhysicsWarp_LimitsScaleWithWarp()
    {
        // x4: los estados llegan cada 1/30 s reales, que son 4/30 s de juego.
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0, warp: 4);
        Add(motion, 100 + (4 * Step), At(0.001), 100 + (4 * Step), 10, warp: 4);

        SurfacePose pose = motion.Current(100 + (4 * Step) + (4 * 0.1), Later, 4);

        Assert.Equal(0.004, pose.Latitude, 6);
    }

    [Fact]
    public void Longitude_CrossesAntimeridianTheShortWay()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0, 179.999), 100, 0);
        Add(motion, 100 + Step, At(0, -179.9995), 100 + Step, 10);

        // 0,0015° por paso hacia el este: un paso más allá queda en -179,998, no en la otra punta del planeta.
        SurfacePose pose = motion.Current(100 + (2 * Step), Later, 1);

        Assert.Equal(-179.998, pose.Longitude, 6);
    }

    [Fact]
    public void NewState_IsBlendedInsteadOfJumping()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);

        // Llega un estado 2 m al norte: en ese instante se sigue viendo donde estaba y luego se acerca.
        double twoMeters = 2 / MetersPerDegree;
        Add(motion, 100.5, At(twoMeters), 100.5, 10);

        Assert.Equal(0, motion.Current(100.5, 10, 1).Latitude * MetersPerDegree, 3);
        double halfway = motion.Current(100.5, 10 + (PuppetMotion.CorrectionRealSeconds / 6), 1).Latitude * MetersPerDegree;
        Assert.InRange(halfway, 0.5, 1.5);
        Assert.Equal(2, motion.Current(100.5, 10 + PuppetMotion.CorrectionRealSeconds * 3, 1).Latitude * MetersPerDegree, 2);
    }

    [Fact]
    public void BigJump_IsAppliedAtOnce()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100.5, At(1), 100.5, 10); // unos 10 km

        Assert.Equal(1, motion.Current(100.5, 10, 1).Latitude, 9);
    }

    [Fact]
    public void BigJump_IsNotTakenAsVelocity()
    {
        // Teletransportada 10 km entre dos estados seguidos: no debe seguir "avanzando" a esa velocidad.
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(1), 100 + Step, 10);

        Assert.Equal(1, motion.Current(100 + Step + 0.1, 10, 1).Latitude, 9);
        Assert.Equal(1, motion.Current(100 + Step + 5, Later, 1).Latitude, 9);
    }

    [Fact]
    public void RailsWarp_DoesNotExtrapolate()
    {
        // Dos estados con física justo antes de empezar el warp sobre raíles (warp 0 en las llamadas).
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(0.001), 100 + Step, 10);

        Assert.Equal(0.001, motion.Current(100 + Step + 300, Later, 0).Latitude, 9);
    }

    [Fact]
    public void StatesTakenWithoutWarp_KeepTheirLimitsWhenWarpStarts()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        Add(motion, 100 + Step, At(0.001), 100 + Step, 10);

        // Warp físico x4 después: como mucho 0,25 s de juego por delante, no 1 s.
        SurfacePose pose = motion.Current(100 + Step + 10, Later, 4);

        Assert.Equal(0.001 + (0.001 * PuppetMotion.MaxAheadRealSeconds / Step), pose.Latitude, 6);
    }

    [Fact]
    public void OtherBody_IsAppliedAtOnce()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(0), 100, 0);
        motion.Add(Kerbin + 1, 100.5, At(0.0001), 100.5, 10, 1, KerbinRadius);

        Assert.Equal(0.0001, motion.Current(100.5, 10, 1).Latitude, 9);
    }

    [Fact]
    public void OlderState_IsIgnored()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(1), 100, 0);
        Add(motion, 99, At(5), 100, 0);

        Assert.Equal(100, motion.LatestUniversalTime);
        Assert.Equal(1, motion.Current(100, 0, 1).Latitude, 9);
    }

    [Fact]
    public void Rotation_KeepsTurningAtTheSameRate()
    {
        // Gira 1° por paso alrededor del eje vertical.
        Rotation first = YawDegrees(10);
        Rotation second = YawDegrees(11);
        var motion = new PuppetMotion();
        Add(motion, 100, At(0, rotation: first), 100, 0);
        Add(motion, 100 + Step, At(0, rotation: second), 100 + Step, 10);

        Rotation shown = motion.Current(100 + (3 * Step), Later, 1).Rotation;

        Assert.Equal(13, YawOf(shown), 4);
    }

    [Fact]
    public void Rotation_TakesTheShortWayWhenSignFlips()
    {
        // q y -q son el mismo giro: no debe dar la vuelta entera.
        var motion = new PuppetMotion();
        Add(motion, 100, At(0, rotation: YawDegrees(10)), 100, 0);
        Add(motion, 100 + Step, At(0, rotation: YawDegrees(11).Negated()), 100 + Step, 10);

        Assert.Equal(12, YawOf(motion.Current(100 + (2 * Step), Later, 1).Rotation), 4);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var motion = new PuppetMotion();
        Add(motion, 100, At(1), 100, 0);
        motion.Clear();

        Assert.False(motion.HasState);
        Assert.Equal(double.NegativeInfinity, motion.LatestUniversalTime);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, -180)]
    [InlineData(-180, -180)]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, -180)]
    public void WrapDegrees_StaysInRange(double input, double expected)
    {
        Assert.Equal(expected, PuppetMotion.WrapDegrees(input), 9);
    }

    private static Rotation YawDegrees(double degrees)
    {
        double half = degrees * Math.PI / 360.0;
        return new Rotation(0, Math.Sin(half), 0, Math.Cos(half));
    }

    private static double YawOf(Rotation rotation)
    {
        if (rotation.W < 0)
            rotation = rotation.Negated();
        return Math.Atan2(rotation.Y, rotation.W) * 360.0 / Math.PI;
    }
}
