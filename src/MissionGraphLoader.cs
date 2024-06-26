﻿using Bundles;
using Game;
using Game.Map;
using HarmonyLib;
using Missions;
using Missions.Nodes;
using Missions.Nodes.Flow;
using Missions.Nodes.Sequenced;
using Modding;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using UnityEngine;
using TMPro;
using UI;
using Utility;
using XNode;
using System;
using Networking;
using Cameras;
using Game.UI.Chessboard;

namespace MissionGraphLoader
{
	public class MissionGraphLoader : IModEntryPoint
	{
		public static ulong UniqueID = 0;
		public static GameObject FacilityPrefab = null;
		public static List<string> FacilityInsertionTargets = new List<string>();
		public static Dictionary<string, ISkirmishBattlespaceInfo> BattlespaceMappings = new Dictionary<string, ISkirmishBattlespaceInfo>();

		public void PreLoad()
		{
			// we're patching BundleManager so the Harmony register has to happen in PreLoad
			Harmony harmony = new Harmony("nebulous.mission-graph-loader");
			harmony.PatchAll();
		}

		public void PostLoad()
		{
			bool loadedSelf = false;

			foreach (ModRecord mod in ModDatabase.Instance.MarkedForLoad)
			{
				if (mod.Info.ModName == "Mission Graph Loader")
				{
					MissionGraphLoader.UniqueID = mod.Info.UniqueIdentifier;
					//Debug.LogError(UniqueID);
					loadedSelf = true;
					break;
				}
			}

			if (!loadedSelf)
			{
				Debug.LogError("MissionGraphLoader: Could not find itself to initialize, or was not loaded properly");
			}

			// Thank you to SomeUsername6 for the below code!
			// Here we grab the Facility from Station Capture mode

			ScenarioGraph stationCapture = null;
			foreach (ScenarioGraph scenario in BundleManager.Instance.AllScenarios)
			{
				if (scenario.ScenarioName == "Station Capture")
				{
					stationCapture = scenario;
					break;
				}
			}
			if (stationCapture == null)
			{
				Debug.LogError("MissionGraphLoader: Could not find ScenarioGraph by the name of Station Capture");
				return;
			}

			GameObject facilityPrefab = null;
			foreach (Node node in stationCapture.nodes)
			{
				CreateCapturePoint createCapturePoint = node as CreateCapturePoint;
				if (createCapturePoint != null)
				{
					facilityPrefab = createCapturePoint.Prefab;
					break;
				}
			}
			if (facilityPrefab == null)
			{
				Debug.LogError("MissionGraphLoader: ScenarioGraph does not contain a CreateCapturePoint node");
				return;
			}

			MissionGraphLoader.FacilityPrefab = facilityPrefab;
		}
	}

	// Data types used for the Harmony patches later
	public class MissionLoaderManifest : BundleManifest
	{
		public MissionLoaderManifest()
		{

		}

		public List<MissionGraphEntry> MissionGraphMappings;
		
		public class MissionGraphEntry
		{
			[XmlAttribute]
			public string MissionName;

			[XmlAttribute]
			public string GraphAddress;

			public MissionGraphEntry()
			{

			}
		}

		public List<MissionBattlespaceEntry> MissionBattlespaceMappings;

		public class MissionBattlespaceEntry
		{
			[XmlAttribute]
			public string MissionName;

			[XmlAttribute]
			public string BattlespaceName;

			public MissionBattlespaceEntry()
			{

			}
		}

		public List<string> MissionsWithStations;
	}

	// Reimplementation of this function to allow for the static loading of mission graphs and inserting default battlespaces into missions
	[HarmonyPatch(typeof(BundleManager), "ProcessAssetBundle")]
	class Patch_BundleManager_ProcessAssetBundle
	{
		static bool Prefix(ref BundleManager __instance, AssetBundle bundle, ModInfo fromMod)
		{
			if (fromMod != null && fromMod.Dependencies != null && fromMod.Dependencies.Length > 0 && fromMod.Dependencies.Contains(MissionGraphLoader.UniqueID))
			{
				Debug.Log("Processing asset bundle " + bundle.name);
				BundleManifest manifest = bundle.ReadXMLTextAsset<BundleManifest>("manifest.xml");
				bool flag = manifest == null;
				if (flag)
				{
					Debug.LogError("Could not process asset bundle.  No manifest found");
				}
				else
				{
					bool flag2 = !string.IsNullOrEmpty(manifest.ResourceFile);
					if (flag2)
					{
						ResourceDefinitions.ResourceFile resources = bundle.ReadXMLTextAsset<ResourceDefinitions.ResourceFile>(manifest.ResourceFile);
						bool flag3 = resources != null;
						if (flag3)
						{
							ResourceDefinitions.Instance.LoadResources(resources);
						}
						else
						{
							Debug.Log("Failed to read resources file '" + manifest.ResourceFile + "'");
						}
					}

					List<MissionSet> missionSets = (List<MissionSet>)Utilities.GetPrivateField(__instance, "_missionSets");

					MethodInfo method = __instance.GetType().GetMethod("LoadListEntries", BindingFlags.NonPublic | BindingFlags.Instance);
					MethodInfo generic = method.MakeGenericMethod(typeof(MissionSet));
					generic.Invoke(__instance, new object[] { manifest, manifest.MissionSets, bundle, missionSets, fromMod });

					// Keep the bundle loaded after the normal function is run, so we can access the missions from it in the postfix
					//bundle.Unload(false);
				}

				return false;
			}

			return true;
		}

		static void Postfix(ref BundleManager __instance, AssetBundle bundle, ModInfo fromMod)
		{
			if (fromMod != null && fromMod.Dependencies != null && fromMod.Dependencies.Length > 0 && fromMod.Dependencies.Contains(MissionGraphLoader.UniqueID))
			{
				Debug.Log("MissionGraphLoader - evaluating asset bundle " + bundle.name);

				MissionLoaderManifest missionSettings = bundle.ReadXMLTextAsset<MissionLoaderManifest>("missions.xml");

				if (missionSettings == null)
				{
					Debug.Log("MissionGraphLoader - not required for mod " + fromMod.ModName);

					return;
				}

				LoadMissionNodeGraphs(__instance, bundle, fromMod, missionSettings);

				List<ISkirmishBattlespaceInfo> battlespaces = __instance.AllMaps.ToList();
				foreach (MissionLoaderManifest.MissionBattlespaceEntry entry in missionSettings.MissionBattlespaceMappings)
				{
					MissionGraphLoader.BattlespaceMappings.Add(entry.MissionName, battlespaces.Find(x => x.MapName == entry.BattlespaceName));
				}

				MissionGraphLoader.FacilityInsertionTargets.AddRange(missionSettings.MissionsWithStations);
			}
		}

		private static void LoadMissionNodeGraphs(BundleManager __instance, AssetBundle bundle, ModInfo fromMod, MissionLoaderManifest missionSettings)
		{
			List<BundleManifest.Entry> missionGraphEntries = new List<BundleManifest.Entry>();

			foreach (MissionLoaderManifest.MissionGraphEntry mapping in missionSettings.MissionGraphMappings)
			{
				BundleManifest.Entry entry = new BundleManifest.Entry();
				entry.Name = mapping.MissionName;
				entry.Address = mapping.GraphAddress;

				missionGraphEntries.Add(entry);
			}

			Debug.Log("MissionGraphLoader - nodegraph mappings found: " + missionGraphEntries.Count);

			List<MissionGraph> missionGraphs = new List<MissionGraph>();

			MethodInfo method = __instance.GetType().GetMethod("LoadListEntries", BindingFlags.NonPublic | BindingFlags.Instance);
			MethodInfo generic = method.MakeGenericMethod(typeof(MissionGraph));
			generic.Invoke(__instance, new object[] { missionSettings, missionGraphEntries, bundle, missionGraphs, fromMod });
			//Utilities.CallPrivateVoidMethod(__instance, "LoadListEntries", new object[] { manifest, missionGraphEntries, bundle, missionGraphs, fromMod });
			//this.LoadListEntries<ScenarioGraph>(manifest, manifest.Scenarios, bundle, missionGraphs, fromMod);

			Debug.Log("MissionGraphLoader - nodegraphs found: " + missionGraphs.Count);

			if (missionGraphs.Count == 0)
			{
				return;
			}

			foreach (MissionSet missionSet in __instance.MissionSets)
			{
				Debug.Log("MissionGraphLoader - iterating MissionSet: " + missionSet.CampaignName);

				foreach (Mission mission in missionSet.Missions)
				{
					Debug.Log("MissionGraphLoader - iterating Mission: " + mission.MissionName);

					for (int i = 0; i < missionSettings.MissionGraphMappings.Count; i++)
					{
						MissionLoaderManifest.MissionGraphEntry mapping = missionSettings.MissionGraphMappings[i];

						Debug.Log("MissionGraphLoader - checking match with MissionGraph: " + mapping.MissionName);

						if (mission.MissionName == mapping.MissionName)
						{
							MissionGraph graph = missionGraphs[i];
							Utilities.SetPrivateField(mission, "_loadedGraph", graph);

							Debug.Log("MissionGraphLoader - assigned nodegraph successfully: " + (mission.Graph != null));
							break;
						}
					}
				}
			}
		}
	}

	// Additional logging to make developing and debugging mission graphs easier
	[HarmonyPatch(typeof(BaseMissionNode), "ExecuteStepSequence")]
	class Patch_BaseMissionNode_ExecuteStepSequence
	{
		static bool Prefix(ref BaseMissionNode __instance)
		{
			Debug.Log("Beginning Sequence from Flow Node " + __instance.name + " of Type " + __instance.GetType().FullName);

			return true;
		}
	}

	// Additional logging to make developing and debugging mission graphs easier
	[HarmonyPatch(typeof(SequencedNode), "GetNextStep")]
	class Patch_SequencedNode_GetNextStep
	{
		static bool Prefix(ref SequencedNode __instance)
		{
			Debug.Log("Executed Sequence Node " + __instance.name + " of Type " + __instance.GetType().FullName);

			NodePort port = __instance.GetOutputPort("NextStep");
			if (port == null)
			{
				Debug.Log("Failed to find output port");
				return true;
			}

			NodePort connection = port.Connection;
			if (connection == null)
			{
				Debug.Log("Failed to find valid connection");
				return true;
			}

			SequencedNode next = ((connection != null) ? connection.node : null) as SequencedNode;
			if (connection == null)
			{
				Debug.Log("Failed to find following node");
				return true;
			}

			Debug.Log("Proceeding to execute following node " + next.name);

			return true;
		}
	}

	// Reimplementation of this function to retrieve battlespaces from storage and load them for custom missions which cannot define battlespaces normally
	[HarmonyPatch(typeof(SkirmishGameManager), "StateLoadingMap")]
	class Patch_SkirmishGameManager_StateLoadingMap
	{
		static bool Prefix(ref SkirmishGameManager __instance)
		{
			LoadMapMessage? loadMapInstructions = (LoadMapMessage?)Utilities.GetPrivateField(__instance, "_loadMapInstructions");
			Mission mission = (Mission)Utilities.GetPrivateField(__instance, "_mission");

			if (loadMapInstructions == null || loadMapInstructions.Value.LoadFromMission == false)
			{
				return true;
			}

			if (loadMapInstructions != null && (__instance.LocalPlayer != null || __instance.IsDedicatedServer))
			{
				if (loadMapInstructions.Value.LoadFromMission)
				{
					ISkirmishBattlespaceInfo mapInfo = mission.Graph.GetMapToLoad();
					if (mapInfo == null)
					{
						MissionGraphLoader.BattlespaceMappings.TryGetValue(mission.MissionName, out mapInfo);
						Debug.Log("Could not find map in mission, falling back on configured map " + mapInfo.MapName);
					}
					Utilities.SetPrivateField(__instance, "_loadedMapInfo", mapInfo);
				}
				else
				{
					Utilities.SetPrivateField(__instance, "_loadedMapInfo", BundleManager.Instance.GetMap(loadMapInstructions.Value.LoadKey));
				}
				Utilities.SetPrivateField(__instance, "_loadMapInstructions", null);

				ISkirmishBattlespaceInfo loadedMapInfo = (ISkirmishBattlespaceInfo)Utilities.GetPrivateField(__instance, "_loadedMapInfo");
				bool flag2 = loadedMapInfo == null;
				if (flag2)
				{
					Debug.LogError("Could not find map to load");
					__instance.Shutdown(true);
				}

				SkirmishGameManager instance = __instance;

				Transform levelGeoRoot = (Transform)Utilities.GetPrivateField(__instance, "_levelGeoRoot");
				loadedMapInfo.InstantiateMap(levelGeoRoot).Done(delegate (Battlespace map)
				{
					Debug.Log("Map instantiation completed");
					Utilities.SetPrivateField(instance, "_loadedMap", map);

					MissionStartNode startNode = mission.Graph.nodes.FirstOrDefault((Node x) => x is MissionStartNode) as MissionStartNode;
					if (startNode != null)
					{
						startNode.MapGeo = map;
						Debug.Log("Inserted map into mission");
					}

					Battlespace loadedMap = (Battlespace)Utilities.GetPrivateField(instance, "_loadedMap");
					GameObject defaultLighting = (GameObject)Utilities.GetPrivateField(instance, "_defaultLighting");
					SpacePartitioner spacePartition = (SpacePartitioner)Utilities.GetPrivateField(instance, "_spacePartition");
					ChessboardManager chessboard = (ChessboardManager)Utilities.GetPrivateField(instance, "_chessboard");
					ImprovedOrbitCamera cameraRig = (ImprovedOrbitCamera)Utilities.GetPrivateField(instance, "_cameraRig");

					bool hasLighting = loadedMap.HasLighting;
					if (hasLighting)
					{
						UnityEngine.Object.Destroy(defaultLighting);
					}
					instance.ApplyNormalSkybox();
					spacePartition.Build(loadedMap);
					chessboard.SetMapRadius(loadedMap.Radius);
					cameraRig.OverideModeMaxZoom(1, new float?(loadedMap.Radius * 3f));
					bool flag3 = instance.SkirmishLocalPlayer != null;
					if (flag3)
					{
						instance.SkirmishLocalPlayer.ReportMapLoaded();
					}
				}, delegate (Exception exception)
				{
					Debug.LogError("InstantiateMap call failed: " + ((exception != null) ? exception.ToString() : null));
					instance.Shutdown(true);
				});
			}

			return false;
		}
	}

	// Disable the unloading of the nodegraph after mission exit
	// (which if unloaded would cause errors if player wanted to replay the same mission)
	[HarmonyPatch(typeof(Mission), "Unload")]
	class Patch_Mission_Unload
	{
		static bool Prefix(ref Mission __instance)
		{
			// do not run this function
			return false;
		}
	}
	
	// Renames the button on the main menu from Campaign/Tutorial to Campaign
	[HarmonyPatch(typeof(MainMenu), "SingleplayerButton")]
	class Patch_MainMenu_SingleplayerButton
	{
		static void Postfix(ref MainMenu __instance)
		{
			GameObject submenu = (GameObject)Utilities.GetPrivateField(__instance, "_singleplayerSubmenu");

			foreach (TextMeshProUGUI text in submenu.GetComponentsInChildren<TextMeshProUGUI>())
			{
				if (text.text == "Campaign/Tutorial")
				{
					text.text = "Campaign";

					return;
				}
			}
		}
	}

	// Copy the Facility model from the basegame Station Capture into the specified missions
	[HarmonyPatch(typeof(MissionGraph), "InitializeLobby")]
	class Patch_MissionGraph_InitializeLobby
	{
		static bool Prefix(ref MissionGraph __instance)
		{
			Mission mission = (Mission)Utilities.GetPrivateField(GameManager.Instance, "_mission");

			if (mission != null
			&& mission.Graph.name == __instance.name 
			&& MissionGraphLoader.FacilityInsertionTargets.Contains(mission.MissionName))
			{
				if (MissionGraphLoader.FacilityPrefab == null)
				{
					Debug.LogError("MissionGraphLoader: FacilityPrefab was not correctly set prior to mission dynamic load");
					return true;
				}

				foreach (Node node in __instance.nodes)
				{
					if (node != null)
					{
						Debug.Log(node.GetType());
						CreateCapturePoint createCapturePoint = node as CreateCapturePoint;
						if (createCapturePoint != null)
						{
							createCapturePoint.Prefab = MissionGraphLoader.FacilityPrefab;
						}
					}
					else
					{
						Debug.LogError("MissionGraphLoader: Found null node while scanning for prefab insertion in mission graph: " + __instance.name);
					}
				}
			}

			return true;
		}
	}

	// Enables a mission's specified badge to override the player's set custom badge
	[HarmonyPatch(typeof(MissionGraph), "SetupPlayer")]
	class Patch_MissionGraph_SetupPlayer
	{
		static void Postfix(ref MissionGraph __instance, IPlayer player)
		{
			SkirmishLobbyPlayer lobbyPlayer = player as SkirmishLobbyPlayer;

			if (lobbyPlayer != null)
			{
				MissionStartNode startNode = (MissionStartNode)Utilities.GetPrivateField(__instance, "_startNode");

				if (startNode != null)
				{
					if (startNode.HumanPlayer.Badge != null)
					{
						lobbyPlayer.SetBadge(HullBadge.GetBadge(startNode.HumanPlayer.Badge.name));
					}
				}
			}
		}
	}

	// Enables missions to assign badges to ally and enemy bots
	[HarmonyPatch(typeof(SkirmishLobbyPlayer), "InitializePlayerWith")]
	class Patch_SkirmishLobbyPlayer_InitializePlayerWith
	{
		static bool Prefix(ref SkirmishLobbyPlayer __instance, ref HullBadge badge)
		{
			if (badge != null)
			{
				if (badge.Texture != null)
				{
					if (badge.Texture.name != null)
					{
						badge = HullBadge.GetBadge(badge.Texture.name);
					}
				}
			}

			return true;
		}
	}

	// Copies the mission definition's preview image over to the map recon image panel
	// nonfunctional, no way to convert a texture into a rawimage, would be difficult to dynamic or static load it
	/*
	[HarmonyPatch(typeof(SkirmishLobbyManager), "HostPrivateGame")]
	class Patch_SkirmishLobbyManager_HostPrivateGame
	{
		static void Postfix(ref SkirmishLobbyManager __instance, HostSkirmishLobbyData data)
		{
			if (data.Mission != null)
			{
				SkirmishMissionMenu missionMenu = (SkirmishMissionMenu)Utilities.GetPrivateField(__instance, "_lobbyMenu");

				if (missionMenu != null)
				{
					Debug.LogError("setting mission image");
					Utilities.SetPrivateField(missionMenu, "_missionImage", new RawImage(data.Mission.Screenshot.texture));
				}
			}
		}
	}
	*/
}
