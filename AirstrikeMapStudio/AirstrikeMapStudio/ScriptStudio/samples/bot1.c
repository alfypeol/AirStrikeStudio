// AS3D script source (scrsrc)
slots 25;
entry init = r0;
entry main = r251;
entry damaged = r31;
entry touched = r14;
cash "helic_dead", "helic_campfire";
globals self, other, frametime, g_map_pos, camera, player, l_waterlevel;
functions AttachDeactivate, random, Damage, PushPlayer, AttachActivate, move, sin, abs, TerrainHeight, rotatez, vec_sub, Shoot, create, remove;
var vec v20;

routine r0 {
    self.f14[2] = 180;
    self.f5[2] = 100;
    AttachDeactivate("CAMPFIRE_ALMOSTDEAD");
    v16 = random() * 360;
}

routine r14 {
    if (self.f4) {
        return;
    }
    if (self.f5[2] < 100) {
        return;
    }
    Damage(other, self.f34 / 7);
    Damage(self, 200);
    PushPlayer();
}

routine r31 {
    if (self.f34 < 50) {
        AttachActivate("CAMPFIRE_ALMOSTDEAD");
    }
}

routine r37 {
    v17 = 50 * frametime;
    if (self.f17[0] > 0) {
        self.f17[0] -= v17;
        if (self.f17[0] < 0) {
            self.f17[0] = 0;
        }
    } else if (self.f17[0] < 0) {
        self.f17[0] += v17;
        if (self.f17[0] > 0) {
            self.f17[0] = 0;
        }
    }
    if (self.f17[0] > 100) {
        self.f17[0] = 100;
    }
    if (self.f17[0] < -(100)) {
        self.f17[0] = -(100);
    }
    self.f14[1] = self.f17[0] / 100 * 30;
    self.f14[0] = self.f17[1] / 120 * -(20);
    move(self.f17);
}

routine r102 {
    if (self.f5[1] - g_map_pos < 280) {
        return;
    }
    if (self.f4) {
        return;
    }
    self.f17[1] = -(75);
    r37();
}

routine r117 {
    if (self.f4) {
        return;
    }
    if (self.f17[1] >= camera.f6[1]) {
        self.f17[1] = camera.f6[1];
        return;
    }
    self.f17[1] += 70 * frametime;
    r37();
}

routine r138 {
    if (self.f4) {
        return;
    }
    self.f17[1] = camera.f6[1] + 48 * sin(90 * self.f1);
    if (self.f5[0] - player.f5[0] > 64) {
        v18 = -(200);
    } else if (self.f5[0] - player.f5[0] < -(64)) {
        v18 = 200;
    } else {
        v18 = 120 * sin(self.f1 * 60 + v16);
    }
    self.f17[0] += v18 * frametime;
    r37();
}

routine r195 {
    if (self.f4) {
        return;
    }
    if (abs(self.f17[1]) < 120) {
        self.f17[1] -= 70 * frametime;
    }
    r37();
}

routine r210 {
    if (self.f5[2] - 5 <= TerrainHeight(self.f5[0], self.f5[1])) {
        return;
    }
    self.f17[2] -= 40 * frametime;
    rotatez(90 * self.f14[1] / 30);
    move(self.f17);
}

routine r237 {
    v19 = arg0;
    vec_sub(player.f5, self.f5, v20);
    v20.f1 += 32;
    v20.f2 += 32;
    Shoot("e_wpn_laser_orange_small", v19, v20);
}

routine r251 {
    latent r117();
    latent r102();
    self.f1 = 0;
    v24 = 10;
    while (v24 > 0) {
        if (self.f4) {
            break;
        }
        latent r138(2);
        r237("tag_gun1");
        r237("tag_gun2");
        latent r138(0.2);
        if (self.f4) {
            break;
        }
        r237("tag_gun1");
        r237("tag_gun2");
        latent r138(0.2);
        if (self.f4) {
            break;
        }
        v24 -= 1;
    }
    if (!self.f4) {
        latent r195();
    }
    if (random() < 0.5) {
        create("expl1_helic", self.f5);
        AttachDeactivate("CAMPFIRE_ALMOSTDEAD");
        remove(self);
    } else {
        latent r210();
        create("expl1", self.f5);
        create("helic_dead", self.f5);
        if (self.f5[2] > l_waterlevel) {
            create("helic_campfire", self.f5);
        }
        AttachDeactivate("CAMPFIRE_ALMOSTDEAD");
        remove(self);
    }
}

