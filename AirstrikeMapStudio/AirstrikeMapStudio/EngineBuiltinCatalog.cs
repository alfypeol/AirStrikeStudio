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
    /// Master repository containing all 101 script built-in functions discovered in the engine.
    /// Original AirStrike 3D: 85 built-in functions.
    /// AirStrike II / Gulf Thunder: 101 built-in functions (16 sequel extensions).
    /// </summary>
    public static class EngineBuiltinCatalog
    {
        public static readonly List<EngineBuiltin> All = new List<EngineBuiltin>
        {
            new EngineBuiltin { Name = "debug", Signature = "debug()", ReturnType = "void", Category = "Math / Debug", IsSequelOnly = false, Description = "Prints debug output / script trace message to console or debugger." },
            new EngineBuiltin { Name = "random", Signature = "random([max])", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns pseudorandom float between 0.0 and max (or 0.0..1.0)." },
            new EngineBuiltin { Name = "crandom", Signature = "crandom()", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns centered pseudorandom float between -1.0 and +1.0." },
            new EngineBuiltin { Name = "sin", Signature = "sin(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric sine of angle in radians." },
            new EngineBuiltin { Name = "cos", Signature = "cos(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric cosine of angle in radians." },
            new EngineBuiltin { Name = "tan", Signature = "tan(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric tangent of angle in radians." },
            new EngineBuiltin { Name = "atan", Signature = "atan(x)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates arc tangent of x in radians." },
            new EngineBuiltin { Name = "atan2", Signature = "atan2(y, x)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Calculates arc tangent of (y, x) in radians with quadrant disambiguation." },
            new EngineBuiltin { Name = "abs", Signature = "abs(val)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns absolute value of number." },
            new EngineBuiltin { Name = "min", Signature = "min(a, b)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns minimum of two values." },
            new EngineBuiltin { Name = "max", Signature = "max(a, b)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns maximum of two values." },
            new EngineBuiltin { Name = "lerp", Signature = "lerp(a, b, t)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Linear interpolation between a and b by factor t (0.0..1.0)." },
            new EngineBuiltin { Name = "copysign", Signature = "copysign(x, y)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Returns magnitude of x with the sign of y." },
            new EngineBuiltin { Name = "floor", Signature = "floor(val)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Returns largest integer value less than or equal to val." },
            new EngineBuiltin { Name = "floor2", Signature = "floor2(val, step)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Snaps val down to nearest step interval." },
            new EngineBuiltin { Name = "fmod", Signature = "fmod(x, y)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Floating-point remainder of x divided by y." },
            new EngineBuiltin { Name = "vec_copy", Signature = "vec_copy(dst, src)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Copies 3D vector components from src into dst." },
            new EngineBuiltin { Name = "vec_add", Signature = "vec_add(dst, a, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector addition: dst = a + b." },
            new EngineBuiltin { Name = "vec_sub", Signature = "vec_sub(dst, a, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector subtraction: dst = a - b." },
            new EngineBuiltin { Name = "vec_ma", Signature = "vec_ma(dst, a, scale, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector multiply-accumulate: dst = a + scale * b." },
            new EngineBuiltin { Name = "vec_length", Signature = "vec_length(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Returns Euclidean length/magnitude of 3D vector v." },
            new EngineBuiltin { Name = "vec_norm", Signature = "vec_norm(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Normalizes vector in-place to unit length and returns original length." },
            new EngineBuiltin { Name = "vec_scale", Signature = "vec_scale(v, s)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Scales 3D vector components in-place by scalar s." },
            new EngineBuiltin { Name = "vec_setlen", Signature = "vec_setlen(v, newLen)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Sets 3D vector to specified length preserving direction." },
            new EngineBuiltin { Name = "vec_toyaw", Signature = "vec_toyaw(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Calculates yaw angle in degrees from directional vector." },
            new EngineBuiltin { Name = "vec_toangles", Signature = "vec_toangles(v, outAngles)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Converts directional vector into Euler pitch/yaw/roll angles." },
            new EngineBuiltin { Name = "ClearAxis", Signature = "ClearAxis(matrix)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Resets 3x3 orientation rotation matrix to identity." },
            new EngineBuiltin { Name = "AnglesToAxis", Signature = "AnglesToAxis(angles, matrix)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Converts Euler angles into 3x3 rotation transformation matrix." },
            new EngineBuiltin { Name = "create", Signature = "create(defName, posVec)", ReturnType = "entity", Category = "Entity", IsSequelOnly = false, Description = "Spawns entity, weapon projectile, or particle effect at position." },
            new EngineBuiltin { Name = "remove", Signature = "remove(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Despawns and deletes entity from the game world." },
            new EngineBuiltin { Name = "activate", Signature = "activate(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Activates dormant or sleeping entity (enables AI/script execution)." },
            new EngineBuiltin { Name = "deactivate", Signature = "deactivate(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Deactivates entity without removing it from memory." },
            new EngineBuiltin { Name = "getentity", Signature = "getentity(id)", ReturnType = "entity", Category = "Entity", IsSequelOnly = false, Description = "Resolves entity pointer/handle by object ID." },
            new EngineBuiltin { Name = "setskin", Signature = "setskin(entity, skinName)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Swaps texture skin of entity in real-time." },
            new EngineBuiltin { Name = "SetModel", Signature = "SetModel(modelName)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Replaces entity 3D model mesh with new asset." },
            new EngineBuiltin { Name = "SetFlag", Signature = "SetFlag(flagBit, entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Sets internal entity state flag (e.g. FL_ONWATER, FL_NODRAW)." },
            new EngineBuiltin { Name = "ClearFlag", Signature = "ClearFlag(flagBit, entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Clears internal entity state flag bit." },
            new EngineBuiltin { Name = "AttachEntity", Signature = "AttachEntity(child, parent, tagName, linkAngles)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Attaches child entity to parent model attachment tag." },
            new EngineBuiltin { Name = "DetachEntity", Signature = "DetachEntity(child)", ReturnType = "void", Category = "Attach", IsSequelOnly = true, Description = "Detaches child entity from its parent attachment point." },
            new EngineBuiltin { Name = "AttachActivate", Signature = "AttachActivate(tagName)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Activates attached sub-entities bound to named tag." },
            new EngineBuiltin { Name = "AttachDeactivate", Signature = "AttachDeactivate(tagName)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Deactivates attached sub-entities bound to named tag." },
            new EngineBuiltin { Name = "AttachCallback", Signature = "AttachCallback(tagName, msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Dispatches event callback to all entities attached to named tag." },
            new EngineBuiltin { Name = "ParentCallback", Signature = "ParentCallback(msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Sends callback notification upward to parent entity." },
            new EngineBuiltin { Name = "callback", Signature = "callback(target, msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Dispatches script callback event directly to target entity." },
            new EngineBuiltin { Name = "move", Signature = "move(speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity forward along orientation heading at given speed." },
            new EngineBuiltin { Name = "movex", Signature = "movex(dx)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world X axis." },
            new EngineBuiltin { Name = "movey", Signature = "movey(dy)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world Y axis." },
            new EngineBuiltin { Name = "movez", Signature = "movez(dz)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world Z axis." },
            new EngineBuiltin { Name = "rotate", Signature = "rotate(speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Applies continuous rotational angular velocity." },
            new EngineBuiltin { Name = "rotatex", Signature = "rotatex(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments pitch rotation around X axis." },
            new EngineBuiltin { Name = "rotatey", Signature = "rotatey(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments yaw rotation around Y axis." },
            new EngineBuiltin { Name = "rotatez", Signature = "rotatez(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments roll rotation around Z axis." },
            new EngineBuiltin { Name = "sleep", Signature = "sleep(seconds)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Latent yield: pauses script execution for specified duration." },
            new EngineBuiltin { Name = "RotateTo", Signature = "RotateTo(targetVec, speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Rotates entity smoothly toward target direction at speed." },
            new EngineBuiltin { Name = "lRotateTo", Signature = "lRotateTo(targetVec, speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear straight rotation toward target direction." },
            new EngineBuiltin { Name = "RotateToClamp", Signature = "RotateToClamp(targetVec, mask, maxAngle, minAngle)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Clamped rotation toward target bounded by min/max limits." },
            new EngineBuiltin { Name = "lRotateToClamp", Signature = "lRotateToClamp(targetVec, mask, maxAngle, minAngle)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear clamped rotation bounded by angle limits." },
            new EngineBuiltin { Name = "MoveToNextWP", Signature = "MoveToNextWP(orient)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Advances unit along waypoint route toward next waypoint (latent)." },
            new EngineBuiltin { Name = "lMoveToNextWP", Signature = "lMoveToNextWP(orient)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Moves unit linearly toward next waypoint along route." },
            new EngineBuiltin { Name = "RotateToNextWP", Signature = "RotateToNextWP()", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Rotates unit orientation to face next waypoint." },
            new EngineBuiltin { Name = "lRotateToNextWP", Signature = "lRotateToNextWP()", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear rotational step toward next waypoint." },
            new EngineBuiltin { Name = "GetWaypointDelay", Signature = "GetWaypointDelay(wpIdx)", ReturnType = "float", Category = "Movement", IsSequelOnly = false, Description = "Returns pause/delay duration configured on waypoint." },
            new EngineBuiltin { Name = "TerrainHeight", Signature = "TerrainHeight(x, y)", ReturnType = "float", Category = "Environment", IsSequelOnly = false, Description = "Returns terrain elevation at specified (X, Y) coordinates." },
            new EngineBuiltin { Name = "WaterHeight", Signature = "WaterHeight(x, y)", ReturnType = "float", Category = "Environment", IsSequelOnly = true, Description = "Returns water surface level at specified (X, Y) coordinates." },
            new EngineBuiltin { Name = "TerraMorph", Signature = "TerraMorph(posVec, radius, depth)", ReturnType = "void", Category = "Environment", IsSequelOnly = true, Description = "Real-time terrain deformation: creates crater or trench at position." },
            new EngineBuiltin { Name = "Shoot", Signature = "Shoot(weaponName, tagName, dirVec)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Fires weapon projectile from tag in specified direction." },
            new EngineBuiltin { Name = "Damage", Signature = "Damage(target, amount)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Deals direct damage to target entity." },
            new EngineBuiltin { Name = "RadialDamage", Signature = "RadialDamage(posVec, radius, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Deals area-of-effect blast damage with distance falloff." },
            new EngineBuiltin { Name = "RadialDamagePlayer", Signature = "RadialDamagePlayer(posVec, radius, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = true, Description = "Deals radial shockwave damage tailored specifically to player." },
            new EngineBuiltin { Name = "PlaceLight", Signature = "PlaceLight(posVec, colorVec, radius)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Spawns dynamic point light source in 3D world." },
            new EngineBuiltin { Name = "CameraQuake", Signature = "CameraQuake(intensity)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Triggers screen-shake camera vibration impact effect." },
            new EngineBuiltin { Name = "LockTarget", Signature = "LockTarget()", ReturnType = "entity", Category = "Combat", IsSequelOnly = false, Description = "Acquires and locks targeting reticle onto nearest target." },
            new EngineBuiltin { Name = "IsValidTarget", Signature = "IsValidTarget(target)", ReturnType = "float", Category = "Combat", IsSequelOnly = false, Description = "Returns 1 if target is valid, alive, and in range; 0 otherwise." },
            new EngineBuiltin { Name = "TraceLine", Signature = "TraceLine(startVec, endVec)", ReturnType = "entity", Category = "Combat", IsSequelOnly = false, Description = "Raycasts line-of-sight and returns first hit entity." },
            new EngineBuiltin { Name = "TraceLineDamage", Signature = "TraceLineDamage(startVec, endVec, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Instant laser/bullet trace dealing beam damage along line." },
            new EngineBuiltin { Name = "Lightning", Signature = "Lightning(startVec, endVec)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Spawns electric lightning bolt effect between coordinates." },
            new EngineBuiltin { Name = "StartSound", Signature = "StartSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Plays one-shot sound effect at entity location." },
            new EngineBuiltin { Name = "StartLoopingSound", Signature = "StartLoopingSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Starts continuous looping audio sample on entity." },
            new EngineBuiltin { Name = "StopLoopingSound", Signature = "StopLoopingSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Stops active looping sound effect on entity." },
            new EngineBuiltin { Name = "RespawnPlayer", Signature = "RespawnPlayer()", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Respawns the player helicopter owning calling entity." },
            new EngineBuiltin { Name = "EndLevel", Signature = "EndLevel()", ReturnType = "void", Category = "Game", IsSequelOnly = false, Description = "Triggers mission victory sequence and transitions to next level." },
            new EngineBuiltin { Name = "GameOver", Signature = "GameOver()", ReturnType = "void", Category = "Game", IsSequelOnly = true, Description = "Triggers immediate mission failure / game over sequence." },
            new EngineBuiltin { Name = "PushPlayer", Signature = "PushPlayer(forceVec)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Applies impulse force vector pushing player helicopter." },
            new EngineBuiltin { Name = "G_AddPowerUp", Signature = "G_AddPowerUp(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Adds bonus power-up item charges to player inventory." },
            new EngineBuiltin { Name = "G_UsePowerUp", Signature = "G_UsePowerUp()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Consumes active player power-up and returns success." },
            new EngineBuiltin { Name = "G_GetPowerUp", Signature = "G_GetPowerUp()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries currently active power-up type." },
            new EngineBuiltin { Name = "G_SetPowerUpCount", Signature = "G_SetPowerUpCount(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = true, Description = "Sets inventory charge count for specified power-up." },
            new EngineBuiltin { Name = "G_AddMissiles", Signature = "G_AddMissiles(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Adds secondary missile ammunition to player inventory." },
            new EngineBuiltin { Name = "G_UseMissile", Signature = "G_UseMissile()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Fires currently selected secondary missile weapon." },
            new EngineBuiltin { Name = "G_GetMissiles", Signature = "G_GetMissiles()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries remaining missile ammunition count." },
            new EngineBuiltin { Name = "G_GetUpgrade", Signature = "G_GetUpgrade()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries player primary weapon upgrade level (0..4)." },
            new EngineBuiltin { Name = "G_SetUpgrade", Signature = "G_SetUpgrade(type, level)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Sets weapon upgrade level directly." },
            new EngineBuiltin { Name = "ShowTutorialHint", Signature = "ShowTutorialHint(hintIdx)", ReturnType = "void", Category = "UI", IsSequelOnly = false, Description = "Displays radio transmission / tutorial banner hint message." },
            new EngineBuiltin { Name = "PlayerFreezeHealth", Signature = "PlayerFreezeHealth(freeze)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Toggles invulnerability / god mode on player helicopter." },
            new EngineBuiltin { Name = "PlayerDisableAction", Signature = "PlayerDisableAction(disable)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Freezes player input controls (for cutscenes and scripts)." },
            new EngineBuiltin { Name = "FreezeHealth", Signature = "FreezeHealth(entity, freeze)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Sets target entity invulnerable to damage." },
            new EngineBuiltin { Name = "IsMultiplayer", Signature = "IsMultiplayer()", ReturnType = "float", Category = "Game", IsSequelOnly = true, Description = "Returns 1 if running in 2-player cooperative mode, 0 for single player." },
            new EngineBuiltin { Name = "IsPlayerInGame", Signature = "IsPlayerInGame(playerIndex)", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Returns 1 if player 1 or player 2 helicopter is currently active." },
            new EngineBuiltin { Name = "GetMapPosOfs", Signature = "GetMapPosOfs()", ReturnType = "float", Category = "Environment", IsSequelOnly = true, Description = "Returns current flight path scroll offset along mission track." },
            new EngineBuiltin { Name = "GetPlayerAccel", Signature = "GetPlayerAccel(axis)", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Queries acceleration vector component of player aircraft." },
            new EngineBuiltin { Name = "GetPlayersDistance", Signature = "GetPlayersDistance()", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Calculates Euclidean distance to player aircraft or between co-op players." },
        };

        public static readonly Dictionary<string, EngineBuiltin> ByName =
            All.ToDictionary(b => b.Name, b => b, StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> SequelOnlyNames =
            new HashSet<string>(All.Where(b => b.IsSequelOnly).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        public static readonly Dictionary<string, string> KnownApiDocs =
            All.ToDictionary(b => b.Name, b => b.Description, StringComparer.OrdinalIgnoreCase);

        public static EngineBuiltin Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ByName.TryGetValue(name, out var b);
            return b;
        }

        public static IEnumerable<EngineBuiltin> Filter(string query, int engineFilter, bool sequelOnlyFilter = false)
        {
            var res = All.AsEnumerable();
            if (engineFilter == 1) // Original only
                res = res.Where(b => !b.IsSequelOnly);
            else if (engineFilter == 2 || sequelOnlyFilter) // Sequel only
                res = res.Where(b => b.IsSequelOnly);

            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                res = res.Where(b =>
                    b.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Signature.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return res;
        }
    }
}
