using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Detailed metadata for an engine builtin function across AirStrike 3D and its sequel trilogy.
    /// </summary>
    public sealed class EngineBuiltin
    {
        public string Name { get; set; }
        public string Signature { get; set; }
        public string ReturnType { get; set; }
        public string Category { get; set; }
        public bool IsSequelOnly { get; set; }
        public string Description { get; set; }

        public string EngineTarget => IsSequelOnly ? "Sequel (AS2 / Gulf / OpenAirStrike)" : "Original AirStrike 3D + Sequel";
        public string ShortTag => IsSequelOnly ? "[Sequel]" : "[AS3D]";

        public string TooltipText => string.Format(
            "{0}\r\nCompatibility: {1}\r\nCategory: {2} | Returns: {3}\r\n\r\n{4}",
            Signature, EngineTarget, Category, ReturnType, Description);
    }
}
