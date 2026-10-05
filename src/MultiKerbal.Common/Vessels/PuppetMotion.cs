using System;

namespace MultiKerbal.Common.Vessels
{
    /// <summary>Posición sobre el cuerpo (latitud/longitud en grados, altitud en metros) y rotación relativa a él.</summary>
    public struct SurfacePose
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public Rotation Rotation;
    }

    /// <summary>Cuaternión con lo justo para mover marionetas (el código compartido no puede usar el de Unity).</summary>
    public struct Rotation
    {
        public static readonly Rotation Identity = new Rotation(0, 0, 0, 1);

        public double X;
        public double Y;
        public double Z;
        public double W;

        public Rotation(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static Rotation operator *(Rotation a, Rotation b) => new Rotation(
            (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
            (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
            (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
            (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));

        public static double Dot(Rotation a, Rotation b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);

        /// <summary>La inversa de un cuaternión unitario.</summary>
        public Rotation Inverse() => new Rotation(-X, -Y, -Z, W);

        public Rotation Negated() => new Rotation(-X, -Y, -Z, -W);

        public Rotation Normalized()
        {
            double length = Math.Sqrt(Dot(this, this));
            return length > 1e-12 ? new Rotation(X / length, Y / length, Z / length, W / length) : Identity;
        }

        /// <summary>
        /// El giro <paramref name="delta"/> aplicado una fracción <paramref name="t"/> de veces (0 = nada, 1 = entero,
        /// más de 1 = sigue girando igual). Siempre por el camino corto.
        /// </summary>
        public static Rotation Scale(Rotation delta, double t)
        {
            delta = delta.Normalized();
            if (delta.W < 0)
                delta = delta.Negated();

            double half = Math.Acos(Math.Min(1.0, delta.W));
            double sinHalf = Math.Sin(half);
            if (sinHalf < 1e-9)
                return Identity;

            double scaled = half * t;
            double k = Math.Sin(scaled) / sinHalf;
            return new Rotation(delta.X * k, delta.Y * k, delta.Z * k, Math.Cos(scaled));
        }
    }

    /// <summary>
    /// Movimiento suave de una nave ajena entre los estados que llegan por la red.
    /// <list type="bullet">
    /// <item>Con los dos últimos estados se calcula hacia dónde va y se adelanta hasta el UT local: así se compensa la
    /// latencia, igual que con las órbitas, y la nave no se queda unos metros por detrás.</item>
    /// <item>Cuando llega un estado nuevo, lo que difiere de lo que se estaba mostrando se reparte en unas décimas de
    /// segundo en lugar de saltar de golpe.</item>
    /// </list>
    /// Los límites van en tiempo real: en warp físico se multiplican por el warp, porque el UT corre más deprisa.
    /// En warp sobre raíles (warp 0 en las llamadas) no se adelanta nada: la nave empaquetada no se mueve sobre la
    /// superficie, y con ese warp un estado de hace un instante se adelantaría kilómetros.
    /// </summary>
    public sealed class PuppetMotion
    {
        /// <summary>Lo más que se adelanta una nave desde su último estado (latencia y algún estado perdido).</summary>
        internal const double MaxAheadRealSeconds = 0.25;

        /// <summary>Estados más separados que esto no sirven para saber la velocidad (iba sobre raíles o se perdieron muchos).</summary>
        internal const double MaxGapRealSeconds = 0.5;

        /// <summary>Tiempo en que se absorbe casi toda la corrección (constante de tiempo de un tercio).</summary>
        internal const double CorrectionRealSeconds = 0.3;

        /// <summary>Correcciones mayores se aplican de golpe: la nave se ha movido de verdad (reaparece, cambia de piloto...).</summary>
        internal const double MaxCorrectionMeters = 50.0;

        private Sample _previous;
        private Sample _latest;
        private SurfacePose _correction;
        private Rotation _rotationCorrection = Rotation.Identity;
        private double _correctionRealTime = double.NegativeInfinity;

        /// <summary>Hay al menos un estado.</summary>
        public bool HasState => _latest != null;

        /// <summary>UT del último estado añadido.</summary>
        public double LatestUniversalTime => _latest != null ? _latest.UniversalTime : double.NegativeInfinity;

        public void Clear()
        {
            _previous = null;
            _latest = null;
            _rotationCorrection = Rotation.Identity;
            _correction = default;
            _correctionRealTime = double.NegativeInfinity;
        }

        /// <summary>
        /// Añade un estado recibido. Los que no son posteriores al último se ignoran (UDP puede desordenar).
        /// </summary>
        /// <param name="universalTime">UT local ahora.</param>
        /// <param name="realTime">Reloj real local en segundos (el de las correcciones).</param>
        /// <param name="warpRate">Warp físico actual (1 sin warp; 0 en warp sobre raíles).</param>
        /// <param name="bodyRadius">Radio del cuerpo, para medir la corrección en metros.</param>
        public void Add(int bodyIndex, double sampleUniversalTime, SurfacePose pose, double universalTime, double realTime, double warpRate, double bodyRadius)
        {
            if (_latest != null && sampleUniversalTime <= _latest.UniversalTime)
                return;

            bool hadState = _latest != null && _latest.BodyIndex == bodyIndex;
            SurfacePose shown = hadState ? Current(universalTime, realTime, warpRate) : default;

            var sample = new Sample(bodyIndex, sampleUniversalTime, pose, warpRate);
            _previous = hadState ? _latest : null;
            _latest = sample;

            _correction = default;
            _rotationCorrection = Rotation.Identity;
            _correctionRealTime = double.NegativeInfinity;
            if (!hadState)
                return;

            SurfacePose predicted = Predict(universalTime, warpRate);
            var correction = new SurfacePose
            {
                Latitude = shown.Latitude - predicted.Latitude,
                Longitude = WrapDegrees(shown.Longitude - predicted.Longitude),
                Altitude = shown.Altitude - predicted.Altitude,
            };

            if (DistanceMeters(correction, predicted.Latitude, bodyRadius) > MaxCorrectionMeters)
            {
                // El salto no es una velocidad: hasta el próximo estado no se adelanta nada.
                _previous = null;
                return;
            }

            Rotation shownRotation = shown.Rotation;
            if (Rotation.Dot(shownRotation, predicted.Rotation) < 0)
                shownRotation = shownRotation.Negated();

            _correction = correction;
            _rotationCorrection = (shownRotation * predicted.Rotation.Inverse()).Normalized();
            _correctionRealTime = realTime;
        }

        /// <summary>Lo que hay que mostrar ahora: el estado adelantado hasta el UT local más lo que queda de corrección.</summary>
        public SurfacePose Current(double universalTime, double realTime, double warpRate)
        {
            SurfacePose pose = Predict(universalTime, warpRate);
            double elapsed = realTime - _correctionRealTime;
            if (elapsed < 0)
                elapsed = 0;

            double remaining = Math.Exp(-elapsed * 3.0 / CorrectionRealSeconds);
            if (remaining < 1e-3)
                return pose;

            pose.Latitude += _correction.Latitude * remaining;
            pose.Longitude = WrapDegrees(pose.Longitude + (_correction.Longitude * remaining));
            pose.Altitude += _correction.Altitude * remaining;
            pose.Rotation = (Rotation.Scale(_rotationCorrection, remaining) * pose.Rotation).Normalized();
            return pose;
        }

        /// <summary>El último estado movido hasta <paramref name="universalTime"/> con la velocidad de los dos últimos.</summary>
        private SurfacePose Predict(double universalTime, double warpRate)
        {
            if (_latest == null)
                return new SurfacePose { Rotation = Rotation.Identity };

            SurfacePose latest = _latest.Pose;
            if (_previous == null || warpRate <= 0)
                return latest;

            // Dos estados tomados sin warp no se adelantan con los límites de un warp que llega después.
            double rate = Math.Min(Math.Max(1.0, warpRate), Math.Max(_previous.WarpRate, _latest.WarpRate));
            double gap = _latest.UniversalTime - _previous.UniversalTime;
            if (gap <= 0 || gap > MaxGapRealSeconds * rate)
                return latest;

            // Hacia atrás (UT local algo por detrás del de quien la pilota) se interpola entre los dos estados.
            double ahead = Math.Max(-gap, Math.Min(MaxAheadRealSeconds * rate, universalTime - _latest.UniversalTime));
            double t = ahead / gap;

            SurfacePose previous = _previous.Pose;
            Rotation previousRotation = previous.Rotation;
            if (Rotation.Dot(previousRotation, latest.Rotation) < 0)
                previousRotation = previousRotation.Negated();

            Rotation step = latest.Rotation * previousRotation.Inverse();
            return new SurfacePose
            {
                Latitude = latest.Latitude + ((latest.Latitude - previous.Latitude) * t),
                Longitude = WrapDegrees(latest.Longitude + (WrapDegrees(latest.Longitude - previous.Longitude) * t)),
                Altitude = latest.Altitude + ((latest.Altitude - previous.Altitude) * t),
                Rotation = (Rotation.Scale(step, t) * latest.Rotation).Normalized(),
            };
        }

        /// <summary>Lleva un ángulo a [-180, 180): la longitud salta de 180 a -180 al cruzar el antimeridiano.</summary>
        internal static double WrapDegrees(double degrees)
        {
            double wrapped = (degrees + 180.0) % 360.0;
            if (wrapped < 0)
                wrapped += 360.0;
            return wrapped - 180.0;
        }

        private static double DistanceMeters(SurfacePose delta, double latitude, double bodyRadius)
        {
            const double toRadians = Math.PI / 180.0;
            double north = delta.Latitude * toRadians * bodyRadius;
            double east = delta.Longitude * toRadians * bodyRadius * Math.Cos(latitude * toRadians);
            return Math.Sqrt((north * north) + (east * east) + (delta.Altitude * delta.Altitude));
        }

        private sealed class Sample
        {
            public Sample(int bodyIndex, double universalTime, SurfacePose pose, double warpRate)
            {
                BodyIndex = bodyIndex;
                UniversalTime = universalTime;
                Pose = pose;
                WarpRate = Math.Max(1.0, warpRate);
            }

            public int BodyIndex { get; }

            /// <summary>Warp físico local al recibirlo (1 sin warp).</summary>
            public double WarpRate { get; }

            public double UniversalTime { get; }

            public SurfacePose Pose { get; }
        }
    }
}
