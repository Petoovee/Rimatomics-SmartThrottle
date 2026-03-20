using HarmonyLib;
using Rimatomics;
using Verse;

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

    /// <summary>
    /// Replaces EngageAutoThrottle entirely. Return false to skip the original method.
    /// </summary>
    [HarmonyPatch(typeof(ReactorControl), nameof(ReactorControl.EngageAutoThrottle))]
    public static class Patch_EngageAutoThrottle
    {
        public static bool Prefix()
        {
            Log.Error("Hello world");
            return false; // skip original
        }
    }
}
