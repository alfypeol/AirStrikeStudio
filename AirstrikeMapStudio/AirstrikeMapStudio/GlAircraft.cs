using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Aircraft starting-height rules
        // ═════════════════════════════════════════════════════════════════
        // The starting world Z of an aircraft is not stored in the .hsc map.
        // It is set by the aircraft's script (the `init` routine writes to
        // self.f5[2]). Scripts fall into three families:
        //
        //   • Flying:  self.f5[2] = 100
        //   • Landing: self.f5[2] = TerrainHeight(...) + 7
        //   • Other:   self.f5[2] = <something else> (no rule)
        //
        // Detection order:
        //   1. Known SHA-256 script hashes (exact match for stock scripts)
        //   2. Script text / symbol inspection (for custom or edited scripts)
        //   3. Script filename heuristics (e.g. "land", "takeoff", "stand")
        //   4. Default to Flying (world Z 100) for helics.obj and planes.obj
        //      so unhashed variants (such as helic_big2_green) do not sink
        //      into the terrain.
        //
        // Return value semantics (consumed by GlScenery.ObjectZ):
        //   0 = terrain height
        //   1 = world Z 100 (flying)
        //   2 = terrain height + 7 (landing)
        //
        // Cache: keyed by "type|script", so two aircraft with the same
        // effective script share an entry.
        // ═════════════════════════════════════════════════════════════════

        private const int AircraftHeightTerrain = 0;
        private const int AircraftHeightFlying = 1;
        private const int AircraftHeightLanding = 2;

        private readonly Dictionary<string, int> aircraftRules =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private string HeightKey(Placement p)
        {
            return map.Types[p.Type] + "|" + (p.Script ?? "");
        }

        /// <summary>
        /// Returns the starting-height rule for an aircraft object: 0 = terrain,
        /// 1 = world Z 100, 2 = terrain + 7. Sets <paramref name="note"/> to a
        /// human-readable explanation.
        /// </summary>
        private int AircraftRule(Assets assets, Placement p, string name, ref string note)
        {
            if (!assets.Definitions.TryGetValue(name, out var def))
                return AircraftHeightTerrain;

            // Only aircraft families are subject to these rules. A tank whose
            // script happens to hash-match must not be pushed up to Z 100.
            string family = Path.GetFileName(def.Source);
            bool isAircraftFamily =
                string.Equals(family, "helics.obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(family, "planes.obj", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(family, "bosses.obj", StringComparison.OrdinalIgnoreCase) &&
                 (name.IndexOf("helic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  name.IndexOf("plane", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  name.IndexOf("zepp", StringComparison.OrdinalIgnoreCase) >= 0));

            if (!isAircraftFamily)
                return AircraftHeightTerrain;

            // Effective script: the map override if present, otherwise the
            // definition's script. This is what the game will actually run.
            string script = string.IsNullOrWhiteSpace(p.Script) ? def.Script : p.Script;
            if (string.IsNullOrWhiteSpace(script))
            {
                note += " Starting pose: world Z 100 (aircraft default; no script assigned).";
                return AircraftHeightFlying;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(assets.Resolve(script));
                string hash;
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

                // ── 1. Exact SHA-256 matches for verified stock scripts ────

                // Kamikaze (from helicopter scripts).
                if (hash == "539c0230c89d38d6a9add6d4a62135481c04c1cb4d665342073454f23f94b9f3"
                 || hash == "f34b5b6223e7b30cf27264f323c2a3e5424e32d05d059a4318d09d996f2e764d")
                {
                    note += " Starting pose: world Z 100 from inspected kamikaze script; " +
                            "movement and death burst not simulated.";
                    return AircraftHeightFlying;
                }

                // helic3.
                if (hash == "641d27b46c94881f0bf0d67c55024140f6848046a679771b00d662c3d2e612ec")
                {
                    note += " Starting pose: world Z 100 from inspected helic3 script; " +
                            "flight animation not simulated.";
                    return AircraftHeightFlying;
                }

                // helic3 takeoff sequence (Landing: terrain + 7).
                if (hash == "403e6827942872061f965903c4b4b1dafe38b60a9526c9ce50a33f29e9d495db")
                {
                    note += " Approximate landed start: terrain +7 from inspected helic3 " +
                            "takeoff script; climb not simulated.";
                    return AircraftHeightLanding;
                }

                // Flying: helicopter initialization (broad list).
                if (hash == "02acf9983261baf8a055ead790a923cf34888b7dd15cd95131468e2b2adfd655"
                 || hash == "1943e27c9ba275d1c68ba67e40c23e05161ebc72449cf48d6f341a15d90dd712"
                 || hash == "22eb7f67bd556dde8b2b0ed3b3f718bfa42bfeb5c07fb3eead64f0b073aad193"
                 || hash == "4dbdb91bf434f1871b90d6190be42d5e2cdc5b443ef460d819bf3195cbb9bb01"
                 || hash == "5f6541350b1d344b456127ba757013671ce154782f5b5fb537be2e1f44906290"
                 || hash == "62a66e3665b0a5b611397287be71ea16970bef1125fd3203811a331c8b6f9af5"
                 || hash == "641d27b46c94881f0bf0d67c55024140f6848046a679771b00d662c3d2e612ec"
                 || hash == "651d2f645e939cd154639b7117d47d12878e811c0299c56940c994c31bf7d6ef"
                 || hash == "7cf00c654b112047d3aee7414d23d3d9b2ae48e772cfe1cfd8bd9da2996d1d07"
                 || hash == "c695f09ada54e78fe47b06902b84b96a6dcd3874c4c052d74c30fbb258b44023"
                 || hash == "d41b8b0fe55e999626631843350a78489a1c16a48901cdee74f89c0098a47c91")
                {
                    note += " Starting pose: world Z 100; inspected helicopter initialization. " +
                            "Flight not simulated.";
                    return AircraftHeightFlying;
                }

                // Landing: helicopter initialization (broad list).
                if (hash == "242432a9a9b2f4a32db19a1189c18a2f2d1169f9314fbd0b82294e38547a1463"
                 || hash == "2c61dae939032e2c54fc1eda687ef69f09983ac9e811eaf9de6a9915d4d0944e"
                 || hash == "31028512d5016a76d47d435761cf87f1846ece27d30a1e80120fb638d83c4ad8"
                 || hash == "403e6827942872061f965903c4b4b1dafe38b60a9526c9ce50a33f29e9d495db"
                 || hash == "578fb7f5f227ffc0f55ac34d00f97c708571897031fee7a9f0a79ea2dae06cc7"
                 || hash == "632bdfeab9b5e694ac34ca306ef9c06b143330e25cdb8619fbaa389274d749b5"
                 || hash == "a44b6724496587f4e1a0f3fcf815ed7cf6ce97f9cf1e310c7b198721a56d1980"
                 || hash == "bef44799d50ea5dca9d2ccc8b31d25be073fff2d9916e2c2c7956a9614b970df")
                {
                    note += " Approximate landed start: terrain +7; inspected helicopter " +
                            "initialization. Takeoff not simulated.";
                    return AircraftHeightLanding;
                }

                // Flying: helicopter initialization (specific hashes).
                if (hash == "9adbbaba0461046639518b9602e57d754c63341681d873427968f4cdef9ab2cd"
                 || hash == "5aeebb65962286851455e716283dbfd0fff66849664c21c30a03ec0b8b7667a3"
                 || hash == "b1dcc9fe0f9b472b935c408b0db72adaea2fba56d54e4e07781c8d43f19da2ab")
                {
                    note += " Starting pose: world Z 100 from inspected helicopter " +
                            "initialization; flight not simulated.";
                    return AircraftHeightFlying;
                }

                // Flying: plane initialization.
                if (hash == "c0cd007e11ebdf383f4a450a4b2aaed282b799b87323ba546f869078c74eec94"
                 || hash == "d7d93d03dea090ea6560eb71fb048f32dea70fadde3293c808efd2798a66b58f"
                 || hash == "d0a8542ee5f710eb29b1288270ef57c2fca4e3de093581e0c12dc1af67678170"
                 || hash == "2f397682ed5dbebf57e4c03d369ccb9cbb17f8c010a6db8b16301daffa3a872f"
                 || hash == "f4a1abc80795f45daa975a2707ea3af6764d5e875578b6f91f4b7fa02925915b"
                 || hash == "4cbbabbf5fe9a535ffd3a11e230e6d5fad0a63d77bb9a249899cfb5361ff4448"
                 || hash == "2fffb815446c1dde3cb603bbbe8049898a532ae98bb8f99242f2556106877c9c")
                {
                    note += " Starting pose: world Z 100 from inspected plane initialization; " +
                            "movement and banking not simulated.";
                    return AircraftHeightFlying;
                }

                // ── 2. Content & filename inspection for unhashed/edited scripts ──
                string text = Encoding.ASCII.GetString(bytes);
                if (Regex.IsMatch(text, @"f5\s*\[\s*2\s*\]\s*=\s*[^;]*TerrainHeight", RegexOptions.IgnoreCase))
                {
                    note += " Approximate landed start: terrain +7 from script inspection; " +
                            "takeoff not simulated.";
                    return AircraftHeightLanding;
                }
                if (Regex.IsMatch(text, @"f5\s*\[\s*2\s*\]\s*=\s*100\b", RegexOptions.IgnoreCase))
                {
                    note += " Starting pose: world Z 100 from script inspection; " +
                            "flight not simulated.";
                    return AircraftHeightFlying;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // ── 3. Filename heuristic for landed/parked scripts ───────────
            string scriptName = Path.GetFileNameWithoutExtension(script);
            if (scriptName.IndexOf("land", StringComparison.OrdinalIgnoreCase) >= 0
             || scriptName.IndexOf("takeoff", StringComparison.OrdinalIgnoreCase) >= 0
             || scriptName.IndexOf("stand", StringComparison.OrdinalIgnoreCase) >= 0
             || scriptName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                note += " Approximate landed start: terrain +7 from script name heuristic; " +
                        "takeoff not simulated.";
                return AircraftHeightLanding;
            }

            // ── 4. Default aircraft fallback: airborne (world Z 100) ──────
            note += " Starting pose: world Z 100 (aircraft default for effective script); " +
                    "flight not simulated.";
            return AircraftHeightFlying;
        }
    }
}