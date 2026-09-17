namespace MultiKerbal.Client.UI
{
    internal enum LabelCategory
    {
        Ship,
        Probe,
        Relay,
        Station,
        Base,
        Lander,
        Rover,
        Plane,
        Eva,
        Flag,
        Science,
        Debris,
    }

    internal sealed class LabelCategoryInfo
    {
        public LabelCategoryInfo(LabelCategory category, string key, string text, bool shownByDefault = true)
        {
            Category = category;
            Key = key;
            Text = text;
            ShownByDefault = shownByDefault;
        }

        public LabelCategory Category { get; }

        /// <summary>Nombre en settings.cfg.</summary>
        public string Key { get; }

        public string Text { get; }

        public bool ShownByDefault { get; }
    }

    /// <summary>Sobre qué naves de otros jugadores se dibuja la etiqueta, por tipo de nave (como los filtros del mapa).</summary>
    internal sealed class LabelFilter
    {
        /// <summary>En el orden (y el índice) de <see cref="LabelCategory"/>.</summary>
        public static readonly LabelCategoryInfo[] Categories =
        {
            new LabelCategoryInfo(LabelCategory.Ship, "ships", "Naves"),
            new LabelCategoryInfo(LabelCategory.Probe, "probes", "Sondas y satélites"),
            new LabelCategoryInfo(LabelCategory.Relay, "relays", "Relés"),
            new LabelCategoryInfo(LabelCategory.Station, "stations", "Estaciones"),
            new LabelCategoryInfo(LabelCategory.Base, "bases", "Bases"),
            new LabelCategoryInfo(LabelCategory.Lander, "landers", "Módulos de aterrizaje"),
            new LabelCategoryInfo(LabelCategory.Rover, "rovers", "Rovers"),
            new LabelCategoryInfo(LabelCategory.Plane, "planes", "Aviones"),
            new LabelCategoryInfo(LabelCategory.Eva, "eva", "Kerbals en EVA"),
            new LabelCategoryInfo(LabelCategory.Flag, "flags", "Banderas"),
            new LabelCategoryInfo(LabelCategory.Science, "science", "Experimentos desplegados"),
            new LabelCategoryInfo(LabelCategory.Debris, "debris", "Escombros", false),
        };

        private readonly bool[] _shown = new bool[Categories.Length];

        public LabelFilter()
        {
            foreach (LabelCategoryInfo info in Categories)
                _shown[(int)info.Category] = info.ShownByDefault;
        }

        /// <summary>Interruptor general: apagado no se dibuja ninguna etiqueta.</summary>
        public bool Enabled { get; set; } = true;

        public bool this[LabelCategory category]
        {
            get => _shown[(int)category];
            set => _shown[(int)category] = value;
        }

        public bool Shows(VesselType type) => Enabled && this[CategoryOf(type)];

        /// <summary>Marca o desmarca todos los tipos. Devuelve si algo cambió.</summary>
        public bool SetAll(bool shown)
        {
            bool changed = false;
            for (int i = 0; i < _shown.Length; i++)
            {
                changed |= _shown[i] != shown;
                _shown[i] = shown;
            }

            return changed;
        }

        public static LabelCategory CategoryOf(VesselType type)
        {
            switch (type)
            {
                case VesselType.Ship:
                    return LabelCategory.Ship;
                case VesselType.Probe:
                    return LabelCategory.Probe;
                case VesselType.Relay:
                    return LabelCategory.Relay;
                case VesselType.Station:
                    return LabelCategory.Station;
                case VesselType.Base:
                    return LabelCategory.Base;
                case VesselType.Lander:
                    return LabelCategory.Lander;
                case VesselType.Rover:
                    return LabelCategory.Rover;
                case VesselType.Plane:
                    return LabelCategory.Plane;
                case VesselType.EVA:
                    return LabelCategory.Eva;
                case VesselType.Flag:
                    return LabelCategory.Flag;
                case VesselType.DeployedScienceController:
                case VesselType.DeployedSciencePart:
                    return LabelCategory.Science;
                default:
                    // Escombros, piezas sueltas y cualquier tipo que KSP añada.
                    return LabelCategory.Debris;
            }
        }
    }
}
