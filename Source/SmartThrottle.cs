using System;
using System.Reflection.Emit;
using HarmonyLib;
using Rimatomics;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimatomicsSmartThrottle
{
    [StaticConstructorOnStartup]
    public static class SmartThrottleMod
    {
        static SmartThrottleMod()
        {
            new Harmony("petoovee.RimatomicsSmartThrottle").PatchAll();
        }
    }

    [HarmonyPatch(typeof(ReactorControl), "Tick")]
    public static class Patch_ReactorControl_Tick
    {
        private static readonly SoundDef SoundOverheat = SoundDef.Named("OverheatSiren");
        private static readonly SoundDef SoundMeltdown = SoundDef.Named("MeltdownSiren");
        private static readonly SoundDef SoundIntegrity = SoundDef.Named("IntegritySiren");

        // Non-virtual call to ThingWithComps.Tick() — mirrors the original IL "call ThingWithComps::Tick()"
        private static readonly Action<ReactorControl> BaseTick = CreateBaseTick();

        private static Action<ReactorControl> CreateBaseTick()
        {
            var method = AccessTools.Method(typeof(ThingWithComps), "Tick");
            var dm = new DynamicMethod("SmartThrottle_BaseTick", null, new[] { typeof(ReactorControl) }, typeof(Patch_ReactorControl_Tick));
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, method); // non-virtual — no recursive dispatch
            il.Emit(OpCodes.Ret);
            return (Action<ReactorControl>)dm.CreateDelegate(typeof(Action<ReactorControl>));
        }

        public static bool Prefix(ReactorControl __instance)
        {
            BaseTick(__instance);

            if (__instance.CoreLink == null)
            {
                __instance.CoreLink = GenCollection.FirstOrDefault<reactorCore>(
                    __instance.LoomNet.Cores,
                    (Predicate<reactorCore>)((reactorCore z) => z.GreekID == __instance.GreekID));
                if (__instance.CoreLink != null)
                    __instance.UpdateGraphicsCache();
            }

            if (__instance.CoreLink != null && !((Thing)__instance.CoreLink).Spawned)
            {
                __instance.AutoThrottle = false;
                __instance.CoreLink = null;
            }

            if (__instance.CoreLink != null && __instance.CoreLink.LoomNet != __instance.LoomNet)
            {
                __instance.AutoThrottle = false;
                __instance.CoreLink = null;
            }

            if (__instance.CoreLink == null || !__instance.powerComp.PowerOn)
                return false;

            bool meltdown = __instance.CoreLink.postReturnTemp > 2000f;
            bool overheat = __instance.CoreLink.postReturnTemp > 500f;
            bool integrityLow = __instance.CoreLink.coreIntegrity < 0.25f;

            if (overheat && Time.realtimeSinceStartup > __instance.overheatTimer)
            {
                SoundStarter.PlayOneShot(SoundOverheat,
                    (SoundInfo)new TargetInfo(((Thing)__instance).Position, ((Thing)__instance).Map, false));
                __instance.overheatTimer = Time.realtimeSinceStartup + 2.35f;
            }

            if (meltdown && Time.realtimeSinceStartup > __instance.meltdownTimer)
            {
                SoundStarter.PlayOneShot(SoundMeltdown,
                    (SoundInfo)new TargetInfo(((Thing)__instance).Position, ((Thing)__instance).Map, false));
                __instance.meltdownTimer = Time.realtimeSinceStartup + 1.9f;
            }

            if (integrityLow && Time.realtimeSinceStartup > __instance.integrityTimer)
            {
                SoundStarter.PlayOneShot(SoundIntegrity,
                    (SoundInfo)new TargetInfo(((Thing)__instance).Position, ((Thing)__instance).Map, false));
                __instance.integrityTimer = Time.realtimeSinceStartup + 5f;
            }

            if (__instance.AutoThrottle &&
                (__instance.CoreLink.IsShutdown || !__instance.CoreLink.powerComp.PowerOn || !__instance.powerComp.PowerOn))
            {
                __instance.AutoThrottle = false;
                __instance.CoreLink.TargetControlRodTo = __instance.CoreLink.TargetControlRodPosition;
            }

            if (!__instance.AutoThrottle ||
                !Gen.IsHashIntervalTick((Thing)(object)__instance, 10) ||
                !(Math.Abs(__instance.CoreLink.TargetControlRodTo - __instance.CoreLink.TargetControlRodPosition) <= 0.001f))
                return false;


            float netWdPerTick = __instance.powerComp.PowerNet.CurrentEnergyGainRate();
            float netWatts = netWdPerTick / CompPower.WattsToWattDaysPerTick;

            float totalBatteryCapacity = 0f;
            float totalBatteryStored = 0f;
            foreach (CompPowerBattery bat in __instance.powerComp.PowerNet.batteryComps)
            {
                totalBatteryCapacity += bat.Props.storedEnergyMax;
                totalBatteryStored += bat.StoredEnergy;
            }
            // How far from full the batteries are, in Wd
            float batteryMargin = totalBatteryCapacity - totalBatteryStored;
            // Ticks until batteries are full at current net gain (Wd / Wd/tick = ticks)
            float ticksToFill = netWdPerTick > 0f ? batteryMargin / netWdPerTick : float.PositiveInfinity;

            float rodDelta;
            if (__instance.CoreLink.overheating > 0f)
            {
                rodDelta = -0.02f;
            }
            else if (ticksToFill > 3000f)
            {
                // Batteries draining — ramp up output
                rodDelta = 0.02f;
            }
            else if (netWatts > 15000f) rodDelta = -0.01f;
            else if (netWatts > 10000f) rodDelta = -0.001f;
            else if (netWatts > 5000f) rodDelta = -0.0005f;
            else rodDelta = 0f;

            if (rodDelta == 0f)
                __instance.CoreLink.TargetControlRodTo = __instance.CoreLink.TargetControlRodPosition;
            else
                __instance.CoreLink.TargetControlRodTo =
                    Mathf.Clamp01(__instance.CoreLink.TargetControlRodTo + rodDelta);

            return false;
        }
    }
}
