using System;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace RackBuilderMod;

public static class NPCBuilder
{
	private static MelonLogger.Instance Log => Melon<RackBuilderCore>.Logger;

	public static void QueueBuildJobs(Rack targetRack, System.Collections.Generic.List<RackBuilderCore.ItemChoice> items, System.Collections.Generic.List<int> slotIndices)
	{
		TechnicianManager techMgr = FindTechnicianManager();
		MainGameManager gameMgr = FindMainGameManager();
		if (IsMissing(techMgr) || IsMissing(gameMgr)) return;

		Technician technician = GetTechnicianOrSpawn(techMgr, gameMgr);
		if (IsMissing(technician)) return;

		Log.Msg($"Using technician: {technician.technicianName} (ID: {technician.technicianID})");
		int jobCount = Math.Min(items.Count, slotIndices.Count);
		for (int i = 0; i < jobCount; i++)
		{
			QueueSingleBuildJob(techMgr, gameMgr, targetRack, items[i], slotIndices[i]);
		}
	}

	private static TechnicianManager FindTechnicianManager()
	{
		TechnicianManager techMgr = UnityEngine.Object.FindObjectOfType<TechnicianManager>();
		if (IsMissing(techMgr)) Log.Error("TechnicianManager not found");
		return techMgr;
	}

	private static MainGameManager FindMainGameManager()
	{
		MainGameManager gameMgr = UnityEngine.Object.FindObjectOfType<MainGameManager>();
		if (IsMissing(gameMgr)) Log.Error("MainGameManager not found");
		return gameMgr;
	}

	private static Technician GetTechnicianOrSpawn(TechnicianManager techMgr, MainGameManager gameMgr)
	{
		Technician technician = FindAvailableTechnician(techMgr);
		if (!IsMissing(technician)) return technician;

		Log.Msg("No available technician — trying to spawn one");
		technician = SpawnTechnician(techMgr, gameMgr);
		if (IsMissing(technician)) Log.Error("Could not find or spawn a technician");
		return technician;
	}

	private static Technician FindAvailableTechnician(TechnicianManager techMgr)
	{
		if (techMgr.technicians == null) return null;

		var enumerator = techMgr.technicians.GetEnumerator();
		while (enumerator.MoveNext())
		{
			Technician current = enumerator.Current;
			if (!IsMissing(current) && !current.isBusy) return current;
		}

		return null;
	}

	private static void QueueSingleBuildJob(TechnicianManager techMgr, MainGameManager gameMgr, Rack targetRack, RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		if (!TryGetRackPosition(targetRack, slotIndex, out RackPosition rackPosition)) return;

		try
		{
			GameObject prefab = GetPrefab(gameMgr, itemChoice);
			if (IsMissing(prefab)) return;

			GameObject instance = InstantiateInRack(prefab, rackPosition);
			QueueInstallTasks(techMgr, targetRack, rackPosition, instance, itemChoice, slotIndex);
		}
		catch (Exception ex)
		{
			Log.Error("Build job failed for " + itemChoice.name + ": " + ex.Message);
		}
	}

	private static bool TryGetRackPosition(Rack targetRack, int slotIndex, out RackPosition rackPosition)
	{
		rackPosition = null;
		if (slotIndex < 0) return false;
		if (targetRack.positions == null) return false;
		if (slotIndex >= ((Il2CppArrayBase<RackPosition>)(object)targetRack.positions).Length) return false;

		rackPosition = ((Il2CppArrayBase<RackPosition>)(object)targetRack.positions)[slotIndex];
		return !IsMissing(rackPosition);
	}

	private static GameObject GetPrefab(MainGameManager gameMgr, RackBuilderCore.ItemChoice itemChoice)
	{
		return (GameObject)(itemChoice.category switch
		{
			"server" => gameMgr.GetServerPrefab(itemChoice.prefabIndex),
			"switch" => gameMgr.GetSwitchPrefab(itemChoice.prefabIndex),
			"patchpanel" => gameMgr.GetPatchPanelPrefab(itemChoice.prefabIndex),
			_ => null,
		});
	}

	private static GameObject InstantiateInRack(GameObject prefab, RackPosition rackPosition)
	{
		GameObject instance = UnityEngine.Object.Instantiate<GameObject>(prefab);
		ResetCableState(instance);
		AttachToRackPosition(instance, rackPosition);
		DisablePhysics(instance);
		return instance;
	}

	private static void ResetCableState(GameObject instance)
	{
		foreach (CableLink cableLink in instance.GetComponentsInChildren<CableLink>())
		{
			if (!IsMissing(cableLink)) cableLink.cableIDsOnLink = 0;
		}
	}

	private static void AttachToRackPosition(GameObject instance, RackPosition rackPosition)
	{
		instance.transform.SetParent(((Component)rackPosition).transform);
		instance.transform.localPosition = Vector3.zero;
		instance.transform.localRotation = Quaternion.identity;
	}

	private static void DisablePhysics(GameObject instance)
	{
		Rigidbody rb = instance.GetComponent<Rigidbody>();
		if (IsMissing(rb)) return;

		rb.isKinematic = true;
		rb.useGravity = false;
	}

	private static void QueueInstallTasks(TechnicianManager techMgr, Rack targetRack, RackPosition rackPosition, GameObject instance, RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		bool queued = QueueServerInstall(techMgr, targetRack, rackPosition, instance, itemChoice, slotIndex);
		queued |= QueueSwitchInstall(techMgr, targetRack, rackPosition, instance, itemChoice, slotIndex);
		if (!queued) Log.Msg($"Placed {itemChoice.name} at U{slotIndex + 1}; no technician task was required");
	}

	private static bool QueueServerInstall(TechnicianManager techMgr, Rack targetRack, RackPosition rackPosition, GameObject instance, RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		Server server = instance.GetComponent<Server>();
		if (IsMissing(server)) return false;

		server.ServerID = CreateBuildId();
		server.serverType = itemChoice.prefabIndex;
		((UsableObject)server).prefabID = itemChoice.prefabIndex;
		server.isBroken = true;
		server.isOn = false;
		ApplyRackMetadata(targetRack, rackPosition, instance, itemChoice, slotIndex);
		techMgr.SendTechnician((NetworkSwitch)null, server);
		LogQueuedTask(itemChoice, slotIndex);
		return true;
	}

	private static bool QueueSwitchInstall(TechnicianManager techMgr, Rack targetRack, RackPosition rackPosition, GameObject instance, RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		NetworkSwitch networkSwitch = instance.GetComponent<NetworkSwitch>();
		if (IsMissing(networkSwitch)) return false;

		networkSwitch.switchId = CreateBuildId();
		networkSwitch.switchType = itemChoice.prefabIndex;
		networkSwitch.isBroken = true;
		networkSwitch.isOn = false;
		ApplyRackMetadata(targetRack, rackPosition, instance, itemChoice, slotIndex);
		techMgr.SendTechnician(networkSwitch, (Server)null);
		LogQueuedTask(itemChoice, slotIndex);
		return true;
	}

	private static void ApplyRackMetadata(Rack targetRack, RackPosition rackPosition, GameObject instance, RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		targetRack.MarkPositionAsUsed(slotIndex, itemChoice.sizeInU);
		UsableObject usableObject = instance.GetComponent<UsableObject>();
		if (IsMissing(usableObject)) return;

		usableObject.currentRackPosition = rackPosition;
		usableObject.rackPositionUID = rackPosition.rackPosGlobalUID;
		usableObject.storedPosition = slotIndex;
		usableObject.sizeInU = itemChoice.sizeInU;
	}

	private static string CreateBuildId()
	{
		return "Build_" + Guid.NewGuid().ToString().Substring(0, 8);
	}

	private static void LogQueuedTask(RackBuilderCore.ItemChoice itemChoice, int slotIndex)
	{
		Log.Msg($"Sent technician to install {itemChoice.name} at U{slotIndex + 1}");
	}

	private static Technician SpawnTechnician(TechnicianManager techMgr, MainGameManager mgr)
	{
		if (mgr.techniciansPrefabs == null || ((Il2CppArrayBase<GameObject>)(object)mgr.techniciansPrefabs).Length == 0)
		{
			Log.Error("No technician prefabs available");
			return null;
		}
		try
		{
			GameObject technicianPrefab = ((Il2CppArrayBase<GameObject>)(object)mgr.techniciansPrefabs)[0];
			GameObject instance = UnityEngine.Object.Instantiate<GameObject>(technicianPrefab);
			Technician technician = instance.GetComponent<Technician>();
			if (IsMissing(technician))
			{
				Log.Error("Spawned technician has no Technician component");
				return null;
			}
			technician.technicianID = techMgr.technicians != null ? techMgr.technicians.Count : 0;
			technician.technicianName = "Builder";
			AssignTechnicianTransforms(techMgr, technician);
			if (!IsMissing(technician.transformIdle)) instance.transform.position = technician.transformIdle.position;
			techMgr.AddTechnician(technician);
			Log.Msg($"Spawned technician: {technician.technicianName} (ID: {technician.technicianID})");
			return technician;
		}
		catch (Exception ex)
		{
			Log.Error("Spawn technician failed: " + ex.Message);
			return null;
		}
	}

	private static void AssignTechnicianTransforms(TechnicianManager techMgr, Technician technician)
	{
		technician.transformIdle = FirstOrDefault(techMgr.transformIdle);
		technician.transformContainer = FirstOrDefault(techMgr.transformContainer);
		technician.transformDumpster = FirstOrDefault(techMgr.transformDumpster);
		technician.transformDeviceSpawnPosition = FirstOrDefault(techMgr.transformDeviceSpawnPosition);
	}

	private static Transform FirstOrDefault(Il2CppArrayBase<Transform> transforms)
	{
		return transforms != null && transforms.Length > 0 ? transforms[0] : null;
	}

	private static bool IsMissing(object value)
	{
		return (UnityEngine.Object)value == (UnityEngine.Object)null;
	}
}
