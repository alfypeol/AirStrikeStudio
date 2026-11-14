namespace AirstrikeStudio
{
    /// <summary>
    /// Conversions between map-grid coordinates and world-space coordinates.
    /// </summary>
    /// <remarks>
    /// Two coordinate systems coexist in .hsc data:
    ///
    ///   • <b>Spawn</b> grid Y <c>y</c>  → world Y <c>y*40 - 20</c>
    ///   • <b>Route</b> grid Y <c>y</c>  → world Y <c>y*40 + 20</c>
    ///
    /// They differ by one cell on the Y axis. This is not a bug; the game's
    /// map editor has always placed object spawns half a cell below their
    /// route's first waypoint. X and Z have no such offset.
    ///
    /// Every function here must be used in the matching context:
    ///
    ///   • <see cref="SpawnX"/> / <see cref="SpawnY"/> for object placements
    ///   • <see cref="RouteX"/> / <see cref="RouteY"/> for waypoints
    ///   • <see cref="SpawnGridX"/> / <see cref="SpawnGridY"/> to inverse-map
    ///     a world-space hit back to grid cells for placement
    ///
    /// Mixing them up (e.g., using <see cref="RouteY"/> when placing an
    /// object) shifts the object by one cell relative to the terrain mesh.
    ///
    /// <c>SpawnGridY</c> uses <c>+20</c> while <c>SpawnGridX</c> uses <c>-20</c>
    /// because the Y offset is negative: <c>world = y*40 - 20</c> inverts to
    /// <c>y = (world + 20) / 40</c>. X has no offset, so <c>SpawnGridX</c> is
    /// the plain inverse: <c>x = (world - 20) / 40</c>. This asymmetry is
    /// intentional and matches the original editor's math.
    /// </remarks>
    public static class MapWorld
    {
        public const double TileSize = 40.0;

        /// <summary>World X of a spawn placed at grid X <paramref name="x"/>.</summary>
        public static double SpawnX(double x) => x * TileSize + 20;

        /// <summary>World Y of a spawn placed at grid Y <paramref name="y"/>.</summary>
        public static double SpawnY(double y) => y * TileSize - 20;

        /// <summary>World X of a waypoint placed at grid X <paramref name="x"/>.</summary>
        public static double RouteX(double x) => x * TileSize + 20;

        /// <summary>World Y of a waypoint placed at grid Y <paramref name="y"/>.</summary>
        public static double RouteY(double y) => y * TileSize + 20;

        /// <summary>Grid X of a spawn whose world X is <paramref name="world"/>.</summary>
        public static int SpawnGridX(double world) => (int)System.Math.Round((world - 20) / TileSize);

        /// <summary>Grid Y of a spawn whose world Y is <paramref name="world"/>.</summary>
        public static int SpawnGridY(double world) => (int)System.Math.Round((world + 20) / TileSize);

        /// <summary>Grid X of a waypoint whose world X is <paramref name="world"/>.</summary>
        public static int RouteGridX(double world) => (int)System.Math.Round((world - 20) / TileSize);

        /// <summary>Grid Y of a waypoint whose world Y is <paramref name="world"/>.</summary>
        public static int RouteGridY(double world) => (int)System.Math.Round((world - 20) / TileSize);
    }
}