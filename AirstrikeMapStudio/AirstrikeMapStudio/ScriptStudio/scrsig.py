"""Facts about the engine's script interface, derived from 391 scripts (see scrcensus.py / scrdec.py).
VECTOR_FIELDS: entity field numbers that hold 3-vectors (they have components; passed to engine calls by reference).
All other fields are scalars (passed by value).  RETURNS: engine functions whose result register is used as a value."""
VECTOR_FIELDS = {3, 5, 6, 8, 14, 17, 20, 28}      # field 0 is a scalar (entity handle): passed by value, e.g. callback(v, 1, self.f0, 0)
RETURNS = {'atan2', 'copysign', 'atan', 'floor2', 'fmod', 'IsMultiplayer', 'GetPlayersDistance', 'getentity', 'G_GetMissiles', 'random', 'max', 'G_GetUpgrade', 'sin', 'GetWaypointDelay', 'G_GetPowerUp', 'abs', 'crandom', 'G_UseMissile', 'ClearFlag', 'TerrainHeight', 'vec_length', 'G_UsePowerUp', 'cos', 'IsValidTarget', 'SetFlag', 'LockTarget', 'min', 'create'}

# Argument counts observed per engine function across verified scripts (a call outside these is probably a mistake)
ARITY = {'random': [0, 1], 'AttachDeactivate': [1], 'crandom': [0], 'move': [1], 'rotate': [1], 'TerrainHeight': [2], 'SetFlag': [2], 'ClearFlag': [2], 'PlayerDisableAction': [1], 'sleep': [1], 'EndLevel': [0], 'sin': [1], 'rotatez': [1], 'movey': [1], 'create': [2], 'remove': [1], 'MoveToNextWP': [1], 'lMoveToNextWP': [1], 'lRotateToNextWP': [0], 'AttachEntity': [4], 'FreezeHealth': [2], 'deactivate': [1], 'activate': [1], 'AttachCallback': [4], 'CameraQuake': [1], 'ParentCallback': [3], 'lRotateTo': [2], 'RotateToClamp': [4], 'vec_sub': [3], 'Shoot': [3], 'lRotateToClamp': [4], 'RotateTo': [2], 'AttachActivate': [1], 'rotatex': [1], 'PlaceLight': [3], 'StartSound': [1], 'vec_copy': [2], 'rotatey': [1], 'RadialDamage': [3], 'movez': [1], 'Damage': [2], 'PushPlayer': [0], 'abs': [1], 'TraceLineDamage': [3], 'movex': [1], 'AnglesToAxis': [2], 'vec_scale': [2], 'IsValidTarget': [1], 'ClearAxis': [1], 'LockTarget': [0], 'Lightning': [2], 'callback': [4], 'G_AddPowerUp': [2], 'G_GetUpgrade': [1], 'min': [2], 'G_SetUpgrade': [2], 'G_AddMissiles': [2], 'ShowTutorialHint': [1], 'GetWaypointDelay': [1], 'G_UseMissile': [0], 'G_UsePowerUp': [0], 'G_GetMissiles': [0], 'G_GetPowerUp': [0], 'SetModel': [1], 'max': [2], 'vec_ma': [4], 'vec_length': [1], 'vec_setlen': [2], 'StopLoopingSound': [1], 'RespawnPlayer': [0], 'PlayerFreezeHealth': [1]}

# Added for Airstrike 2 / Gulf Thunder (argument counts observed in 1294 verified scripts; * = new compared with Air Strike 3D)
ARITY.update({
    'DetachEntity': [1], 'G_SetPowerUpCount': [2], 'GameOver': [0], 'GetPlayerAccel': [1], 'GetPlayersDistance': [0],
    'IsMultiplayer': [0], 'RadialDamagePlayer': [3], 'TerraMorph': [2], 'atan': [1], 'atan2': [2], 'copysign': [2],
    'cos': [1], 'floor2': [2], 'fmod': [2], 'getentity': [1], 'vec_add': [3], 'vec_norm': [1], 'vec_toangles': [2],
})
ARITY['sleep'] = [0, 1]        # AS2 also has a bare `latent sleep()` form
ARITY['Lightning'] = [1, 2]    # AS2 calls it with one argument as well
