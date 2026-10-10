using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Threading.Tasks;
using Core.Dependency;
using Game.Core.Research;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Game.Core.Coordinates;

// Uses the installed game's public IMod and map APIs; no hooks or third-party runtime.
public sealed class ShapezAutomationMod : IMod
{
    private readonly GameObject host;
    private readonly bool previousRunInBackground;
    public ShapezAutomationMod(Core.Logging.ILogger logger)
    {
        SupportedBuild.Require();
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        var ipcRoot = Path.Combine(Application.persistentDataPath, "mods", "ShapezAutomationLocal", "ipc");
        Directory.CreateDirectory(ipcRoot);
        File.WriteAllText(Path.Combine(ipcRoot, "startup.json"), new JObject {
            ["adapter"] = "ShapezAutomationLocal", ["assembly"] = typeof(ShapezAutomationMod).Assembly.Location,
            ["utc"] = DateTime.UtcNow.ToString("O"), ["game"] = Application.version
        }.ToString(Formatting.None));
        Debug.Log("ShapezAutomationLocal: constructor entered; local file bridge starting");
        host = new GameObject("ShapezAutomationLocalFileBridge");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<AutomationPump>();
    }
    public void Dispose() { Application.runInBackground = previousRunInBackground; if (host != null) UnityEngine.Object.Destroy(host); }
}

public sealed class AutomationPump : MonoBehaviour
{
    private string inbox, outbox;
    private readonly Dictionary<string, BuildingModel> owned = new Dictionary<string, BuildingModel>();
    private readonly Dictionary<string, IslandModel> ownedIslands = new Dictionary<string, IslandModel>();
    private string session;
    private bool executing;
    private Task transition;
    private string selectedMode;
    private string authorizedTestUid;
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static Game.Orchestration.GameOrchestrator Orchestrator() => (Game.Orchestration.GameOrchestrator)typeof(Game.Orchestration.GameBootstrapper).GetField("GameOrchestrator", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
    private static Game.Orchestration.Game GameState() => (Game.Orchestration.Game)Field(Orchestrator(), "Game");
    private IGameData Data()
    {
        var o = Orchestrator();
        var child = Field(o, "CurrentSubOrchestrator");
        var container = (DependencyContainer)Field(child, "DependencyContainer");
        return container.Resolve<IGameData>();
    }
    private static HUDIntroduction AssignmentDialog() => UnityEngine.Object.FindObjectsByType<HUDIntroduction>(FindObjectsSortMode.None).FirstOrDefault(d => d.gameObject.activeInHierarchy);
    private JObject GlobalState() => new JObject { ["game_state"] = GameState().CurrentState.ToString(), ["active_context"] = StaticGameCoreAccessor.HasInstance(), ["transition_pending"] = transition != null && !transition.IsCompleted, ["transition_error"] = transition?.Exception?.GetBaseException().Message, ["selected_mode"] = selectedMode, ["run_in_background"] = Application.runInBackground, ["focused"] = Application.isFocused, ["assignment_confirmation"] = AssignmentDialog() != null };
    private void Awake()
    {
        var root = Path.Combine(Application.persistentDataPath, "mods", "ShapezAutomationLocal", "ipc");
        inbox = Path.Combine(root, "requests"); outbox = Path.Combine(root, "responses");
        Directory.CreateDirectory(inbox); Directory.CreateDirectory(outbox);
    }
    private void Update()
    {
        Application.runInBackground = true;
        if (executing) return;
        var next = Directory.EnumerateFiles(inbox, "*.json").OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault(x => !File.Exists(Path.Combine(outbox, Path.GetFileName(x))));
        if (next == null) return;
        executing = true;
        try {
            var op = (string)JObject.Parse(File.ReadAllText(next))["op"];
            if (new [] { "place_belt", "place", "place_island", "delete_owned", "delete_owned_island", "pause", "cancel", "main_menu" }.Contains(op) && GameState().IsGameInSession(out _)) {
                var actions = (PlayerActionManager)Field(Field(Orchestrator(),"CurrentSubOrchestrator"),"PlayerActions");
                if (!actions.TryScheduleAction(new BridgeAction(StaticGameCoreAccessor.G.LocalPlayer, () => ProcessFile(next)))) throw new InvalidOperationException("action_schedule_rejected");
                return;
            }
        } catch (Exception e) { WriteResponse(next,new JObject { ["ok"]=false,["error"]=e.Message }); return; }
        ProcessFile(next);
    }
    private void WriteResponse(string next, JObject response) {
        try {
            var target=Path.Combine(outbox,Path.GetFileName(next));
            File.WriteAllText(target+".tmp",response.ToString(Formatting.None));File.Move(target+".tmp",target);
        } finally { executing=false; }
    }
    private void ProcessFile(string next) {
        JObject response;
        try
        {
            var info = new FileInfo(next);
            if (info.Length > 8192) throw new InvalidOperationException("request_too_large");
            var request = JObject.Parse(File.ReadAllText(next));
            var id = (long?)request["id"] ?? throw new InvalidOperationException("missing_id");
            if (Path.GetFileName(next) != id.ToString("D20") + ".json") throw new InvalidOperationException("id_filename_mismatch");
            response = new JObject { ["id"] = id, ["ok"] = true, ["result"] = Execute(request) };
        }
        catch (Exception e) { response = new JObject { ["ok"] = false, ["error"] = e.Message }; }
        WriteResponse(next,response);
    }
    private JToken Execute(JObject q)
    {
        var op = (string)q["op"];
        if (op == "ping") return new JObject { ["adapter"] = "ShapezAutomationLocal", ["game"] = Application.version, ["status"] = GlobalState() };
        if (op == "global_state") return GlobalState();
        if (op == "modes") return new JArray(Data().GameModeIds.Select(id => id.ToString()));
        if (op == "select_mode") { var id = (string)q["mode"]; Data().GetGameModeDefinition(new GameModeId(id)); selectedMode = id; return GlobalState(); }
        if (op == "start_new")
        {
            if(transition != null && !transition.IsCompleted) throw new InvalidOperationException("transition_pending");
            if (!GameState().IsGameInMainMenu()) throw new InvalidOperationException("main_menu_required");
            if ((bool?)q["test_only"] != true || selectedMode == null) throw new InvalidOperationException("selected_test_mode_required");
            var config = GameParameters.DefaultForMode(Data().GetGameModeDefinition(new GameModeId(selectedMode)));
            config.Seed = RequiredInt(q, "seed"); config.SavegameName = "ShapezAutomationLocal test " + config.Seed;
            authorizedTestUid = Guid.NewGuid().ToString();
            transition = Orchestrator().LoadSession(new GameStartOptionsStartNew(config, false, authorizedTestUid));
            return GlobalState();
        }
#pragma warning disable CS0618
        if (!StaticGameCoreAccessor.HasInstance()) throw new InvalidOperationException("no_active_session");
        var core = StaticGameCoreAccessor.G;
#pragma warning restore CS0618
        var map = core.LocalPlayer.CurrentMap;
        var save = core.Savegame.InternalUuid;
        if (session != save) { owned.Clear(); ownedIslands.Clear(); session = save; }
        if (op == "catalog") return new JObject {
            ["buildings"] = new JArray(new [] { core.Mode.Buildings.ForwardBelt, core.Mode.Buildings.ShapeMiner, core.Mode.Buildings.BeltPortSender, core.Mode.Buildings.BeltPortReceiver, core.Mode.Buildings.GetDefinition(new BuildingDefinitionId("BeltDefaultLeftInternalVariant")), core.Mode.Buildings.GetDefinition(new BuildingDefinitionId("BeltDefaultLeftInternalVariantMirrored")) }.Select(d => new JObject {
                ["id"] = d.Id.ToString(), ["connectors"] = new JArray(d.ConnectorData.AllBuildingConnectors.Select(c => new JObject { ["type"] = c.GetType().Name, ["pivot"] = c.Pivot().ToString() })) })),
            ["islands"] = new JArray(core.Mode.Islands.AllDefinitions.Select(d => new JObject { ["id"] = d.Id.ToString(), ["chunks"] = new JArray(d.Layout.GetChunkPositions().Select(c => c.ToString())) })),
            ["miner_island"] = core.Mode.Islands.ShapeMinerIslandCollection.Extractor.Id.ToString(),
            ["research_goals"] = new JArray(core.Research.LevelManager.CurrentLevel.Costs.OfType<ResearchCostShapes>().Select(c => new JObject { ["shape"] = c.ShapeHash, ["amount"] = c.Amount }))
        };
        if (op == "resources")
        {
            int radius = RequiredInt(q, "radius"); if (radius < 0 || radius > 8) throw new InvalidOperationException("radius_0_to_8_required");
            var points = new JArray();
            for (int yy = -radius; yy <= radius; yy++) for (int xx = -radius; xx <= radius; xx++) {
                var p = new GlobalChunkCoordinate(xx, yy, 0);
                if (map.ResourcesAccessor.GetResourceAt_GC(p) is IShapeMapResourceSource source && source.ShapeLookup_G.TryGetValue(p, out var def))
                    points.Add(new JObject { ["x"] = xx, ["y"] = yy, ["shape"] = def.Hash, ["tile_x"] = p.ToOrigin_G().x, ["tile_y"] = p.ToOrigin_G().y });
            }
            return points;
        }
        if (op == "floor") {
            var points = new JArray(); var stride = new GlobalChunkCoordinate(1,0,0).ToOrigin_G().x;
            foreach(var i in map.Layout.Islands) foreach(var c in i.Definition.Layout.GetChunkPositions()) {
                var origin = (c * i.Transform).ToOrigin_G(); var query = new IslandLayoutQuery(i.Definition,i.Transform,3);
                for(int yy=0;yy<stride;yy++) for(int xx=0;xx<stride;xx++) {
                    var p = new GlobalTileCoordinate(origin.x+xx,origin.y+yy,0);
                    if(query.IsValidAndFilledTile_G(p)) points.Add(new JObject { ["x"]=p.x,["y"]=p.y,["occupied"]=map.Layout.TryGetBuilding(p,out _),["notch"]=query.GetNotchFlag_G(p)?.ToString() });
                }
            } return points;
        }
        if (op == "state") { var result = Snapshot(core, map); result["context"] = GlobalState(); return result; }
        if ((string)q["save_id"] != save) throw new InvalidOperationException("save_changed_or_missing_save_id");
        if (!GameState().IsGameInSession(out var options)) throw new InvalidOperationException("real_game_session_required");
        if(options.Uid != authorizedTestUid) throw new InvalidOperationException("bridge_created_test_session_required");
        if (op == "main_menu")
        {
            if (owned.Count != 0 || ownedIslands.Count != 0) throw new InvalidOperationException("clear_owned_buildings_before_exit");
            transition = Orchestrator().LoadMainMenu(); return GlobalState();
        }
        if (op == "accept_assignment") {
            if ((bool?)q["accept"] != true) throw new InvalidOperationException("explicit_assignment_accept_required");
            var dialog=AssignmentDialog(); if(dialog == null) return new JObject { ["confirmation_visible"]=false,["accepted"]=false };
            var toggles=(HUDToggleControl[])Field(dialog,"UIToggles");foreach(var toggle in toggles) toggle.Value=true;
            if(!toggles.All(t => t.Value)) throw new InvalidOperationException("confirmation_toggles_not_set");
            typeof(HUDIntroduction).GetMethod("Continue",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,null);
            return new JObject { ["accepted"]=true,["toggles_checked"]=toggles.Length };
        }
        if (op == "camera") {
            float cameraX=RequiredFloat(q,"x"),cameraY=RequiredFloat(q,"y"),zoom=RequiredFloat(q,"zoom");
            if(Math.Abs(cameraX)>10000 || Math.Abs(cameraY)>10000 || zoom<.1f || zoom>1000) throw new InvalidOperationException("camera_out_of_range");
            var container=(DependencyContainer)Field(Field(Orchestrator(),"CurrentSubOrchestrator"),"DependencyContainer");
            var controller=container.Resolve<CameraController>();
            var waypoint=PlayerWaypoint.From(core.Viewport,"ShapezAutomationLocal camera",null);
            waypoint.PositionX=cameraX;waypoint.PositionY=cameraY;waypoint.Zoom=zoom;
            controller.RequestMoveToViewport(waypoint);
            return Snapshot(core,map);
        }
        if (op == "screenshot") {
            var name = (string)q["name"];
            if (name == null || !System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-zA-Z0-9_-]{1,64}$")) throw new InvalidOperationException("safe_screenshot_name_required");
            var root = Path.Combine(Application.persistentDataPath,"mods","ShapezAutomationLocal","ipc","screenshots");Directory.CreateDirectory(root);
            var target = Path.Combine(root,name+".png");if(File.Exists(target))throw new InvalidOperationException("screenshot_already_exists");
            ScreenCapture.CaptureScreenshot(target);return new JObject { ["accepted"]=true,["path"]=target };
        }
        if (op == "cancel") { core.EntityPlacementRunner.StopCurrentPlacement(); core.LocalPlayer.InteractionState.StopAllPlacement(); return Snapshot(core, map); }
        if (op == "pause") { core.SimulationSpeed.IsPaused = (bool?)q["paused"] ?? true; return Snapshot(core, map); }
        int x = RequiredInt(q, "x"), y = RequiredInt(q, "y"), z = RequiredInt(q, "z");
        if (z < short.MinValue || z > short.MaxValue) throw new InvalidOperationException("layer_out_of_range");
        var pos = new GlobalTileCoordinate(x, y, (short)z);
        var key = x + "," + y + "," + z;
        if (op == "place_belt" || op == "place")
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(typeof(IBuildingPlacementRequirement).Assembly.Location))
            {
                var current = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (current != "e29260fff4ecf62e3de244f956f14a1095f34e5b8a7d54f35acac1f8ef806a59") throw new InvalidOperationException("unsupported_game_assembly");
            }
            int rotation = RequiredInt(q, "rotation");
            if (rotation < 0 || rotation > 3) throw new InvalidOperationException("rotation_out_of_range");
            if (map.Layout.TryGetBuilding(pos, out _)) throw new InvalidOperationException("occupied");
            if (!map.Layout.TryGetIsland(pos.ToChunkCoordinate(), out var island)) throw new InvalidOperationException("no_island");
            if ((bool?)q["test_only"] != true) throw new InvalidOperationException("test_only_required");
            var kind = op == "place_belt" ? "belt" : (string)q["kind"];
            var definition = kind == "belt" ? core.Mode.Buildings.ForwardBelt : kind == "miner" ? core.Mode.Buildings.ShapeMiner : kind == "sender" ? core.Mode.Buildings.BeltPortSender : kind == "receiver" ? core.Mode.Buildings.BeltPortReceiver : kind == "left" ? core.Mode.Buildings.GetDefinition(new BuildingDefinitionId("BeltDefaultLeftInternalVariant")) : kind == "right" ? core.Mode.Buildings.GetDefinition(new BuildingDefinitionId("BeltDefaultLeftInternalVariantMirrored")) : throw new InvalidOperationException("unsupported_test_building");
            var transform = new GlobalTileTransform(pos, GridRotation.RotationsInClockwiseOrder[rotation]);
            definition.TryCreateConfiguration(out var configuration);
            var descriptor = new BuildingDescriptor(definition, transform, configuration, default);
            var islandDescriptor = new IslandDescriptor(island);
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(typeof(GameSessionOrchestrator).Assembly.Location))
            {
                var current = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (current != "d4760d581d8dd588b28c57bbadd865c29f89e211348b8d31a48dbb5ccab4192f") throw new InvalidOperationException("unsupported_orchestration_assembly");
            }
            var floorCheckType = typeof(GameSessionOrchestrator).Assembly.GetType("BuildingShouldBeAllowedInNotchesPlacementRequirement", true);
            var floorCheck = (IBuildingPlacementRequirement)Activator.CreateInstance(floorCheckType, true);
            if (!floorCheck.Check(map, descriptor, islandDescriptor))
                throw new InvalidOperationException("invalid_floor_or_notch");
            if (kind == "miner" && !new OnlyOnShapeResourcePatchPlacementRequirement().Check(map, descriptor, islandDescriptor)) throw new InvalidOperationException("shape_resource_required");
            if (!new BuildingUnlockedPlacementRequirement(core.InteractionMode, core.LocalPlayer).Check(map, descriptor, islandDescriptor))
                throw new InvalidOperationException("building_locked");
            // Restricted test adapter. These public checks are not claimed to cover the entire
            // normal player placement pipeline; live exact-build verification is still required.
            var placed = map.CreateBuilding(definition, transform, configuration);
            if (!placed.IsValid) throw new InvalidOperationException("creation_returned_invalid_building");
            owned.Add(key, placed);
            return Snapshot(core, map);
        }
        if (op == "place_island") {
            if ((bool?)q["test_only"] != true || ownedIslands.Count >= 32) throw new InvalidOperationException("restricted_test_island_limit");
            var definitionId = (string)q["definition"];
            if(!new [] { "Layout_ShapeMiner", "Foundation_1x1", "Layout_SpaceBeltNode" }.Contains(definitionId)) throw new InvalidOperationException("restricted_test_island_definition");
            var d = core.Mode.Islands.GetDefinition(new IslandDefinitionId(definitionId));
            var t = new GlobalChunkTransform(new GlobalChunkCoordinate(x,y,(short)z),GridRotation.RotationsInClockwiseOrder[RequiredInt(q,"rotation")]);
            foreach(var c in d.Layout.GetChunkPositions()) if(map.Layout.TryGetIsland(c*t,out _)) throw new InvalidOperationException("island_occupied");
            d.TryCreateConfiguration(out var config); var descriptor = new IslandDescriptor(d,t,config,default);
            if(!new IslandUnlockedPlacementRequirement(core.InteractionMode,core.LocalPlayer).Check(map,descriptor)) throw new InvalidOperationException("island_locked");
            if(d == core.Mode.Islands.ShapeMinerIslandCollection.Extractor && !new IslandOriginOnShapeResourcePlacementRequirement().Check(map,descriptor)) throw new InvalidOperationException("shape_resource_required");
            var island = map.CreateIsland(d,t,config); ownedIslands.Add(key,island); return Snapshot(core,map);
        }
        if (op == "delete_owned_island") {
            if(!ownedIslands.TryGetValue(key,out var island)) throw new InvalidOperationException("island_not_owned_by_bridge");
            if(island.BuildingsCount != 0) throw new InvalidOperationException("island_has_buildings");
            map.DeleteIsland(island.Id);ownedIslands.Remove(key);return Snapshot(core,map);
        }
        if (op == "delete_owned")
        {
            if (!owned.TryGetValue(key, out var building)) throw new InvalidOperationException("not_owned_by_bridge");
            if (!building.IsValid) throw new InvalidOperationException("owned_building_no_longer_valid");
            map.DeleteBuilding(building.Id); owned.Remove(key); return Snapshot(core, map);
        }
        throw new InvalidOperationException("unknown_op");
    }
    private static float RequiredFloat(JObject q,string key) {
        if(q[key]?.Type != JTokenType.Integer && q[key]?.Type != JTokenType.Float) throw new InvalidOperationException("number_required_"+key);
        var v=(float)q[key];if(float.IsNaN(v)||float.IsInfinity(v)) throw new InvalidOperationException("finite_number_required_"+key);return v;
    }
    private static int RequiredInt(JObject q, string key)
    {
        if (q[key]?.Type != JTokenType.Integer) throw new InvalidOperationException("integer_required_" + key);
        return checked((int)q[key]);
    }
    private static JObject Snapshot(IGameSessionManagers core, IMapModel map)
    {
        var buildings = map.Layout.Buildings.Select(b => new JObject {
            ["x"] = b.Transform.Position.x, ["y"] = b.Transform.Position.y, ["z"] = b.Transform.Position.z,
            ["definition"] = b.Definition.Id.ToString(), ["rotation"] = b.Transform.Rotation.ToString()
        }).OrderBy(b => (int)b["z"]).ThenBy(b => (int)b["y"]).ThenBy(b => (int)b["x"]).ToArray();
        var layout = new JArray(buildings);
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(layout.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
        return new JObject { ["save_id"] = core.Savegame.InternalUuid, ["buildings"] = layout, ["simulation_count"] = map.Simulator.Simulations.Count(),
            ["islands"] = new JArray(map.Layout.Islands.Select(i => new JObject { ["x"] = i.Position.x, ["y"] = i.Position.y, ["definition"] = i.Definition.Id.ToString(), ["rotation"] = i.Rotation.ToString(), ["tile_x"] = i.Position.ToOrigin_G().x, ["tile_y"] = i.Position.ToOrigin_G().y })),
            ["stored_shapes"] = new JArray(core.Research.ShapeStorage.StoredShapes.Select(k => new JObject { ["shape"] = k.Key.ToString(), ["amount"] = k.Value })),
            ["camera"] = new JObject { ["x"] = core.Viewport.Position.x,["y"] = core.Viewport.Position.y,["zoom"] = core.Viewport.Zoom,["target_zoom"] = core.Viewport.TargetZoom },
            ["layout_hash"] = hash, ["paused"] = core.SimulationSpeed.IsPaused,
            ["simulation_time"] = core.SimulationSpeed.SimulationTime_G,
            ["placing"] = core.LocalPlayer.InteractionState.PlacingAnything,
            ["capabilities"] = new JArray("ping", "global_state", "modes", "select_mode", "start_new", "state", "catalog", "resources", "floor", "cancel", "pause", "place_belt", "place", "place_island", "delete_owned", "delete_owned_island", "main_menu", "accept_assignment", "camera", "screenshot"),
            ["placement_enabled"] = true, ["verification_receipt_required"] = true };
    }
}

// The game's queue executes after FinalizeLogicUpdate and before the next simulation update.
public sealed class BridgeAction : IPlayerAction {
    private readonly Action action;
    public Player Executor { get; }
    public PlayerActionMode Mode => PlayerActionMode.Ignored;
    public BridgeAction(Player player, Action action) { Executor=player;this.action=action; }
    public bool IsPossible(IInteractionMode mode) => true;
    public bool TryExecute_INTERNAL(out IPlayerAction undo, IInteractionMode mode, bool unused) { undo=null;action();return true; }
}

// Fail closed before attaching a pump on a different game build.
internal static class SupportedBuild {
    public static void Require() {
        Check(typeof(IBuildingPlacementRequirement).Assembly,"e29260fff4ecf62e3de244f956f14a1095f34e5b8a7d54f35acac1f8ef806a59");
        Check(typeof(GameSessionOrchestrator).Assembly,"d4760d581d8dd588b28c57bbadd865c29f89e211348b8d31a48dbb5ccab4192f");
    }
    private static void Check(Assembly assembly,string expected) {
        using(var sha=SHA256.Create()) using(var stream=File.OpenRead(assembly.Location)) {
            var actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if(actual!=expected) throw new InvalidOperationException("unsupported_shapez2_build; this adapter requires the verified 1.2.0-rc3 DLLs");
        }
    }
}
