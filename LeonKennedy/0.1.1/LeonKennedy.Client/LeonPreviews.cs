using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using EFT;
using EFT.Animations;
using EFT.Customization;
using HarmonyLib;
using UnityEngine;

namespace LeonKennedy.Previews;

[BepInPlugin("com.ikta.leonkennedy.re4", "Ikta-LeonKennedyRE4R", "0.1.1")]
public sealed class LeonPreviews : BaseUnityPlugin
{
    private const string LeonJacketId = "ac2bb2cd66298f430f65d7e0";
    private static readonly HashSet<string> CustomizationIds = new()
    {
        "d5e6a2adcc35a7e6c4880d57", LeonJacketId,
        "9446de91f3827c498970c961", "0933acb7abf9fe57e674f147"
    };
    private static int previewRevision = 13;
    private static ManualLogSource? adsLog;
    private static readonly ConditionalWeakTable<ProceduralWeaponAnimation, AimOffset> ActiveAimOffsets = new();

    private sealed class AimOffset
    {
        public Transform Bone = null!;
        public Vector3 OriginalPosition;
    }

    private void Awake()
    {
        var revisionPath = Path.Combine(Path.GetDirectoryName(Info.Location)!, "asset-revision.txt");
        if (File.Exists(revisionPath))
            previewRevision = unchecked((int)Convert.ToUInt32(File.ReadAllText(revisionPath).Trim(), 16));
        var harmony = new Harmony(Info.Metadata.GUID);
        harmony.Patch(AccessTools.Method(typeof(IconsHash), "GetClothingHash"),
            postfix: new HarmonyMethod(typeof(LeonPreviews), nameof(RefreshClothingHash)));
        harmony.Patch(AccessTools.Method(typeof(PlayerIconRequest), "GetHashSum"),
            postfix: new HarmonyMethod(typeof(LeonPreviews), nameof(RefreshPortraitHash)));
        harmony.Patch(AccessTools.Method(typeof(ClothingIconCreator), "RenderModel"),
            prefix: new HarmonyMethod(typeof(LeonPreviews), nameof(WrapLeonSpriteFactory)));
        harmony.Patch(AccessTools.Method(typeof(BaseSpeaker), nameof(BaseSpeaker.PlayExternal)),
            prefix: new HarmonyMethod(typeof(LeonPreviews), nameof(SkipStockFrostbiteForLeon)));
        adsLog = Logger;
        harmony.Patch(AccessTools.PropertySetter(typeof(ProceduralWeaponAnimation),
            nameof(ProceduralWeaponAnimation.IsAiming)),
            prefix: new HarmonyMethod(typeof(LeonPreviews), nameof(CenterSaigaAim)));
        Logger.LogInfo($"Leon thumbnail fixes enabled; asset revision {previewRevision:X8}; original async renderer preserved.");
    }

    // The frostbite event supplies its own stock bank outside the selected voice.
    private static bool SkipStockFrostbiteForLeon(BaseSpeaker __instance, EPhraseTrigger __1) =>
        __1 != EPhraseTrigger.Frozen ||
        !string.Equals(__instance.PlayerVoice, "Leon", StringComparison.Ordinal);

    private static void CenterSaigaAim(ProceduralWeaponAnimation __instance, bool __0,
        IFirearmAnimationDataRepresenter ____firearmAnimationData)
    {
        if (ActiveAimOffsets.TryGetValue(__instance, out var prior))
        {
            if (prior.Bone != null)
                prior.Bone.localPosition = prior.OriginalPosition;
            ActiveAimOffsets.Remove(__instance);
        }
        if (!__0 || ____firearmAnimationData is not Player.FirearmController firearm ||
            firearm.Weapon.TemplateId.ToString() is not
            ("576165642459773c7a400233" or "674fe9a75e51f1c47c04ec23"))
            return;

        var player = firearm._player;
        var customization = player?.PlayerBody?.BodyCustomization ?? player?.Profile?.Customization;
        if (customization == null || !customization.TryGetValue(EBodyModelPart.Body, out var body) ||
            body.ToString() != LeonJacketId)
            return;

        var scope = __instance.ScopeAimTransforms.Count > 0 ? __instance.CurrentScope : null;
        var aim = scope?.Bone;
        if (aim == null)
            return;
        var sightId = scope!.Mod?.Item?.TemplateId.ToString();
        if (aim.name != "aim_camera" && sightId != "57a9b9ce2459770ee926038d")
            return;

        var root = __instance.HandsContainer.WeaponRootAnim;
        var bore = root != null ? TransformTools.FindTransformRecursive(root, "fireport", false) : null;
        if (bore == null || bore.parent == null || aim.parent == null)
            return;

        // The rear sight can sit under a separate mount hierarchy.
        var weaponSpace = bore.parent;
        var position = weaponSpace.InverseTransformPoint(aim.position);
        var correction = bore.localPosition.x - position.x;
        adsLog?.LogInfo($"Leon Saiga ADS {firearm.Weapon.TemplateId} / {aim.name}: lateral correction {correction * 1000:F2} mm");
        if (Mathf.Abs(correction) < 0.0001f)
            return;
        ActiveAimOffsets.Add(__instance, new AimOffset { Bone = aim, OriginalPosition = aim.localPosition });
        position.x = bore.localPosition.x;
        aim.position = weaponSpace.TransformPoint(position);
    }

    private static int RevisedHash(int original) => unchecked((original * 397) ^ previewRevision);

    private static void RefreshClothingHash(CustomizationClothing __0, ref int __result)
    {
        if (CustomizationIds.Contains(__0.Id.ToString())) __result = RevisedHash(__result);
    }

    private static void RefreshPortraitHash(PlayerIconRequest __instance, ref int __result)
    {
        if (__instance.customization != null && __instance.customization.Values.Any(id => CustomizationIds.Contains(id.ToString())))
            __result = RevisedHash(__result);
    }

    private static void WrapLeonSpriteFactory(
        CustomizationClothing clothing,
        ref IconCreatorBase<CustomizationClothing, ClothingIcon>.SpriteFactory spriteFactory)
    {
        if (!CustomizationIds.Contains(clothing.Id.ToString())) return;
        var originalFactory = spriteFactory;
        // Keep the game's async state machine and job scheduling untouched.
        // Its original callback and returned task are invoked exactly once.
        spriteFactory = (model, pivot) =>
        {
            CopyLeonPreviewMaterials(model);
            return originalFactory(model, pivot);
        };
    }

    private static void CopyLeonPreviewMaterials(GameObject model)
    {
        var sources = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>();
        foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = filter.sharedMesh;
            if (mesh == null) continue;
            var target = filter.GetComponent<MeshRenderer>();
            var source = sources.FirstOrDefault(candidate => candidate.sharedMesh == mesh && IsLeonMesh(mesh))
                ?? sources.FirstOrDefault(candidate => candidate.sharedMesh != null
                    && IsLeonMesh(candidate.sharedMesh) && candidate.sharedMesh.vertexCount == mesh.vertexCount);
            if (target != null && source != null) target.sharedMaterials = source.sharedMaterials;
        }
    }

    private static bool IsLeonMesh(Mesh mesh) =>
        mesh.name.StartsWith("leon_", StringComparison.OrdinalIgnoreCase)
        || mesh.name.StartsWith("Leon ", StringComparison.OrdinalIgnoreCase);
}
