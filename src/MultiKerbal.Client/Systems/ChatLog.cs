using System.Collections.Generic;
using UnityEngine;

namespace MultiKerbal.Client.Systems
{
    internal sealed class ChatLog
    {
        private const int MaxLines = 200;

        private readonly List<Line> _lines = new List<Line>();

        public IList<Line> Lines => _lines;

        /// <summary>Cambia con cada línea nueva (para desplazar la vista al final).</summary>
        public int Version { get; private set; }

        public int Unread { get; set; }

        public void Add(string sender, string text, Color color, bool isSystem)
        {
            _lines.Add(new Line { Sender = sender, Text = text, Color = color, IsSystem = isSystem });
            if (_lines.Count > MaxLines)
                _lines.RemoveAt(0);
            Version++;
        }

        public void Clear()
        {
            _lines.Clear();
            Unread = 0;
            Version++;
        }

        public struct Line
        {
            public string Sender;
            public string Text;
            public Color Color;
            public bool IsSystem;
        }
    }
}
