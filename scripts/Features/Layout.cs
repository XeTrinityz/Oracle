namespace CeZeY.Oracle;

/// <summary>
/// The whole menu in one place: every page, sub-tab, group and row, in display order. To move a feature, move
/// its id here. "+id" repeats a row already placed on an earlier page. "PAGE/TAB" makes a sub-tab. Every page starts with a group header, so pages read alike. A registered
/// feature missing from this list still appears, on an OTHER page, so nothing can vanish.
/// </summary>
internal static class Layout
{
    private readonly record struct Section(string Group, params string[] Ids);

    private static readonly (string Page, Section[] Sections)[] Map =
    {
        ("PLAYER/STATUS", new Section[]
        {
            new("SURVIVAL", "god_mode", "cant_die", "restore_health", "damage_taken", "health_regen"),
            new("ARMOUR", "full_armour", "restore_armour"),
            new("HITS", "no_stagger", "no_stuns"),
            new("STEALTH", "silent_running", "drone_invisible", "thug_invisible"),
            new("DISPLAY", "status_readout"),
        }),
        ("PLAYER/COMBAT", new Section[]
        {
            new("AUTOMATIC", "auto_fight", "auto_counter", "auto_dodge_red", "gun_miss"),
            new("FIGHTING", "combo_lock", "combo_multiplier", "upgraded_batsuit", "freeflow_focus", "npc_group_slowmo", "counter_icons"),
            new("YOUR HITS", "one_hit_ko", "npc_ragdoll_hits", "npc_helmets_off", "disarm_first_hit", "npc_no_stun_stars"),
            new("TAKEDOWNS", "always_takedown", "silent_takedowns", "env_takedowns", "predator_combos", "double_shockwave", "friendly_dual_takedowns"),
        }),
        ("PLAYER/MOVEMENT", new Section[]
        {
            new("ON FOOT", "move_speed", "tunnel_speed"),
            new("GLIDING", "glide_speed", "infinite_glide", "glide_climb"),
            new("GRAPPLE", "grapple_speed", "grapple_boost", "line_launcher_speed"),
            new("FALLING", "fall_gravity"),
        }),
        ("PLAYER/GADGETS", new Section[]
        {
            new("USE", "give_gadgets", "gadget_no_cooldown", "gadget_anywhere"),
            new("DISRUPTOR", "disruptor_unlimited", "disruptor_all_targets"),
            new("HACKING", "auto_password_hack"),
            new("EFFECTS", "smoke_duration", "jammed_blast"),
        }),
        ("PLAYER/CHARACTER", new Section[]
        {
            new("SWITCH", "play_as", "switch_character", "back_to_batman"),
            new("SUIT", "suit", "apply_suit", "cancel_suit"),
            new("APPEARANCE", "hide_cape", "batman_size", "battle_damage", "apply_battle_damage"),
        }),
        ("PLAYER/PROGRESS", new Section[]
        {
            new("EXPERIENCE", "xp_multiplier"),
            new("CHANGES YOUR SAVE", "upgrade_points", "give_xp", "unlock_upgrades", "unlock_all", "unlock_achievements", "save_now"),
            new("RIDDLER", "riddler_map", "riddler_finale"),
        }),
        ("BATMOBILE/DRIVING", new Section[]
        {
            new("ACCESS", "call_batmobile", "rescue_batmobile", "force_exit_batmobile", "batmobile_repair"),
            new("AUTOPILOT", "autopilot_waypoint", "autopilot_objective", "autopilot_stop", "autopilot_boost"),
            new("REMOTE CONTROL", "remote_control", "remote_in_fights"),
            new("PROTECTION", "batmobile_invincible", "batmobile_emp_proof", "batmobile_self_right"),
            new("PERFORMANCE", "infinite_vehicle_boost", "batmobile_engine", "batmobile_grip"),
            new("BATTLE MODE", "batmobile_battle_speed", "batmobile_double_dodge"),
        }),
        ("BATMOBILE/STUNTS", new Section[]
        {
            new("JUMP", "batmobile_jump", "batmobile_jump_power"),
            new("AIR", "batmobile_air_gravity", "batmobile_super_eject"),
        }),
        ("BATMOBILE/WEAPONS", new Section[]
        {
            new("AUTO COMBAT", "batmobile_auto_combat", "batmobile_shoot_down", "batmobile_combat_range", "batmobile_combat_rate"),
            new("ARMAMENT", "infinite_power", "no_cooldowns", "batmobile_no_overheat", "perfect_aim"),
            new("ARSENAL", "batmobile_all_weapons", "batmobile_missile_targets"),
            new("PURSUIT MISSILE", "pursuit_instant_lock", "pursuit_full_hits", "pursuit_damage"),
            new("ON FOOT", "batmobile_takedown_ready"),
        }),
        ("BATMOBILE/DASHBOARD", new Section[]
        {
            new("SPEEDOMETER", "batmobile_speedometer", "batmobile_speed_units", "batmobile_trip_stats", "batmobile_trip_reset"),
            new("RADAR", "vehicle_radar_on_foot"),
        }),
        ("BATMOBILE/SKIN", new Section[]
        {
            new("SHOWCASE SKINS", "batmobile_skin", "apply_batmobile_skin", "cancel_batmobile_skin"),
            new("SOUND", "batmobile_engine_sound"),
        }),
        ("TELEPORT/MAP", new Section[]
        {
            new("WAYPOINT", "teleport_waypoint", "teleport_instant", "waypoint_crosshair", "waypoint_clear"),
            new("MISSION", "teleport_objective"),
            new("RETURN", "teleport_back", "teleport_cancel"),
        }),
        ("TELEPORT/PLACES", new Section[]
        {
            new("WORLD MAP", "teleport_place_group", "teleport_place", "teleport_to_place", "teleport_place_near", "teleport_place_waypoint"),
            new("DISTRICTS", "teleport_district", "teleport_to_district"),
            new("SURPRISE ME", "teleport_random"),
        }),
        ("TELEPORT/SAVED", new Section[]
        {
            new("SLOTS", "teleport_slot", "teleport_slot_go", "teleport_slot_save", "teleport_slot_waypoint", "teleport_slot_clear"),
        }),
        ("TELEPORT/QUICK", new Section[]
        {
            new("BLINK", "teleport_crosshair", "teleport_dash"),
            new("VERTICAL", "teleport_up", "teleport_ground", "teleport_roof"),
            new("TUNING", "teleport_blink_range", "teleport_dash_length", "teleport_sky_height"),
        }),
        ("TELEPORT/TARGETS", new Section[]
        {
            new("BATMOBILE", "teleport_batmobile", "teleport_bring_batmobile"),
            new("COLLECTIBLES", "teleport_trophy"),
        }),
        ("TELEPORT/OPTIONS", new Section[]
        {
            new("ARRIVAL", "teleport_fade", "teleport_no_fade_range", "teleport_arrival_height", "teleport_face"),
            new("POSITION", "teleport_coords", "teleport_log_position"),
        }),
        ("WORLD/ENEMIES", new Section[]
        {
            new("CONTROL", "freeze_enemies", "enemy_size", "npc_full_health", "npc_disarm", "npc_revive"),
            new("MOVE", "teleport_enemy", "teleport_pull_enemies", "npc_launch", "npc_pull_range", "npc_pull_max"),
            new("BEHAVIOUR", "enemy_health", "npc_aggressive", "npc_no_revives", "npc_no_charge_up", "npc_no_dda"),
            new("ALLEGIANCE", "npc_pacify", "npc_turncoat", "npc_gang_war", "npc_free_for_all", "npc_release_turncoats", "npc_turncoat_hits"),
            new("WEAPONS", "npc_weapon_kind", "npc_arm", "npc_arm_turncoats"),
            new("TANKS", "npc_hack_tanks", "npc_hack_time", "npc_release_tanks"),
            new("TURRETS", "npc_disable_turrets", "npc_kill_sentries"),
            new("INSTANT", "ko_all", "ko_all_but_one", "destroy_weapons", "kill_tanks", "npc_clear_thugs"),
        }),
        ("WORLD/FRIENDLIES", new Section[]
        {
            new("CONTROL", "friendly_freeze", "friendly_size", "friendly_full_health", "friendly_disarm", "friendly_revive"),
            new("MOVE", "friendly_teleport", "friendly_pull", "friendly_launch", "+npc_pull_range", "+npc_pull_max"),
        }),
        ("WORLD/CITY", new Section[]
        {
            new("STREETS", "city_empty", "+npc_clear_thugs"),
            new("RIOTS", "force_riots", "rioters_fight"),
            new("SPAWN", "city_apc", "city_batmobile_mob", "city_getaway_car"),
            new("JOKER", "joker_city", "joker_thugs", "joker_sightings"),
            new("VEHICLES", "npc_flip_cars"),
            new("TIME", "game_speed"),
            new("INFO", "city_threats", "npc_count"),
        }),
        ("WORLD/ATMOSPHERE", new Section[]
        {
            new("LOOK", "atmosphere", "atmo_hue", "atmo_strength", "atmo_desat"),
            new("LIGHT", "fx_exposure", "fx_bloom"),
            new("FOG", "atmo_fog", "fx_fog_distance", "fx_fog_height"),
            new("LENS", "fx_motion_blur", "fx_grain", "fx_dof_focus", "fx_dof_blur"),
            new("SCREEN", "post_process"),
        }),
        ("WORLD/WEATHER", new Section[]
        {
            new("RAIN", "rain", "rain_wetness", "rain_area"),
            new("TOXIN", "rain_toxin", "rain_toxin_colour"),
            new("STREETS", "rain_puddles"),
            new("EFFECTS", "rain_no_mist", "rain_no_far"),
            new("LIGHTNING", "lightning_strike", "lightning_aim", "lightning_frequency", "lightning_brightness"),
            new("WIND", "wind_strength", "wind_direction", "wind_gusts"),
        }),
        ("WORLD/EDITOR", new Section[]
        {
            new("EDITOR MODE", "ed_mode", "ed_fly_speed", "ed_mode_stay"),
            new("SELECT", "ed_pick_aimed", "ed_target", "ed_pick_from", "ed_clear"),
            new("GO", "ed_goto", "ed_bring"),
            new("GRAB", "ed_grab", "ed_grab_distance"),
            new("PLACE", "ed_crosshair", "ed_ground"),
            new("NUDGE", "ed_nudge_forward", "ed_nudge_side", "ed_nudge_up", "ed_nudge_step"),
            new("TURN AND SIZE", "ed_yaw", "ed_pitch", "ed_roll", "ed_scale"),
            new("STATE", "ed_freeze", "ed_remove", "ed_ghost"),
            new("ACTIONS", "ed_launch", "ed_flip", "ed_heal", "ed_destroy"),
            new("CHARACTERS", "ed_disarm", "ed_revive", "ed_turn"),
            new("UNDO", "ed_restore", "ed_restore_all"),
        }),
        ("WORLD/ESP", new Section[]
        {
            new("SHOW", "esp_enemies", "esp_friendlies", "esp_vehicles", "esp_turrets", "esp_props", "esp_movers", "esp_pickups"),
            new("STYLE", "esp_boxes", "esp_tracers", "esp_details", "esp_range", "esp_max"),
        }),
        ("CAMERA/GENERAL", new Section[]
        {
            new("PRESETS", "fov_preset", "cam_reset"),
            new("FIELD OF VIEW", "fov_world", "fov_aim"),
            new("EFFECTS", "cam_steady", "cam_no_shake", "cam_no_collision", "cam_tilt"),
            new("SCREEN", "cam_hide_hud", "cam_bars", "fov_readout"),
        }),
        ("CAMERA/ON FOOT", new Section[]
        {
            new("POSITION", "cam_distance", "cam_height", "cam_shoulder", "cam_pivot", "cam_swap_shoulder"),
            new("FIRST PERSON", "cam_first_person", "cam_fp_forward"),
        }),
        ("CAMERA/GLIDING", new Section[]
        {
            new("GLIDE VIEW", "glide_fov", "glide_distance", "glide_kick"),
        }),
        ("CAMERA/BATMOBILE", new Section[]
        {
            new("DRIVING VIEW", "car_view", "car_view_next", "car_fov", "car_distance", "car_kick"),
        }),
        ("CAMERA/FREE CAMERA", new Section[]
        {
            new("FLY", "cam_free", "cam_free_speed", "cam_free_smooth", "cam_free_look", "cam_free_freeze"),
            new("TRIPOD", "cam_tripod", "cam_tripod_track"),
            new("OPTIONS", "cam_free_hud"),
        }),
        ("MUSIC", new Section[]
        {
            new("NOW PLAYING", "music_track", "music_play", "music_next", "music_prev", "music_stop"),
            new("OPTIONS", "music_volume", "music_order", "music_pause_with_game"),
            new("LIBRARY", "music_rescan", "music_folder"),
            new("GAME SOUNDS", "sound_refresh", "sound_filter", "sound_event", "sound_play", "sound_stop", "sound_use_teleport", "sound_teleport", "sound_dump"),
        }),
        ("SYSTEM/GENERAL", new Section[]
        {
            new("BEHAVIOUR", "remember_cheats", "apply_delay", "game_debug_keys"),
            new("RESET", "all_off", "auto_fight_forget"),
        }),
        ("SYSTEM/SAVES", new Section[]
        {
            new("BROWSE", "save_show", "save_story", "save_pick", "save_refresh"),
            new("SELECTED SAVE", "save_load", "save_keep", "save_delete"),
            new("QUICK", "save_now_keep", "+save_now", "save_restart"),
            new("NEW GAME +", "ngp_start_now", "new_game_plus_plus"),
            new("FOLDERS", "save_open_kept", "save_open_backups"),
        }),
        ("SYSTEM/MENU", new Section[]
        {
            new("APPEARANCE", "menu_size", "menu_position", "menu_color", "menu_font", "rainbow_speed", "menu_preview", "menu_effects", "low_end_mode", "active_list"),
        }),
        ("SYSTEM/RADIAL", new Section[]
        {
            new("FAVOURITES", "radial_enabled", "radial_arrange", "radial_clear"),
            new("CONTROLS", "radial_key", "radial_mode", "radial_mouse", "radial_pad"),
            new("LOOK", "radial_slots", "radial_size"),
        }),
        ("SYSTEM/CONTROLLER", new Section[]
        {
            new("XBOX CONTROLLER", "pad_enabled", "pad_hold"),
        }),
        ("SYSTEM/ASSETS", new Section[]
        {
            new("GAME ART", "asset_refresh", "asset_dump", "asset_filter", "asset_texture", "asset_preview"),
        }),
        ("SYSTEM/CREDITS", new Section[]
        {
            new("ORIGINAL MOD", "credit_original"),
            new("TRINITY EDITION", "credit_trinity"),
        }),
    };

    /// <summary>Creates a page. A path like "WORLD/ENEMIES" makes it a sub-tab of WORLD.</summary>
    private static MenuPage Place(string path)
    {
        int slash = path.IndexOf('/');
        if (slash < 0)
        {
            var top = new MenuPage { Title = path };
            Catalog.Pages.Add(top);
            return top;
        }

        string parentTitle = path.Substring(0, slash);
        MenuPage? parent = Catalog.Pages.Find(p => p.Title == parentTitle);
        if (parent == null)
        {
            parent = new MenuPage { Title = parentTitle };
            Catalog.Pages.Add(parent);
        }

        var child = new MenuPage { Title = path.Substring(slash + 1) };
        parent.Children.Add(child);
        return child;
    }

    /// <summary>Builds the menu pages from the registered features.</summary>
    public static void Apply()
    {
        Catalog.Pages.Clear();
        var placed = new HashSet<Feature>();
        foreach ((string title, Section[] sections) in Map)
        {
            MenuPage page = Place(title);
            foreach (Section section in sections)
            {
                page.Rows.Add(new MenuRow { Header = section.Group });

                foreach (string entry in section.Ids)
                {
                    // "+id" shows a row already placed on an earlier page here too, for a setting two pages share.
                    bool again = entry.StartsWith('+');
                    string id = again ? entry.Substring(1) : entry;
                    if (Catalog.ById.TryGetValue(id, out Feature? f) && (again ? placed.Contains(f) : placed.Add(f)))
                        page.Rows.Add(new MenuRow { Item = f });
                    else
                        Log.Info("Menu layout: " + id + (again ? " isn't placed on an earlier page." : " is unknown or listed twice."));
                }
            }
        }

        List<Feature> unplaced = Catalog.All.FindAll(f => !placed.Contains(f));
        if (unplaced.Count == 0)
            return;

        var other = new MenuPage { Title = "OTHER" };
        foreach (Feature f in unplaced)
            other.Rows.Add(new MenuRow { Item = f });
        Catalog.Pages.Add(other);
        Log.Info("Menu layout: " + unplaced.Count + " features aren't listed, showing them under OTHER.");
    }
}
