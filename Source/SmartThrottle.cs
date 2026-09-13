using System;
using System.Collections.Generic;
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

        // reactorCore.overheating is a Mathf.SmoothDamp'd value with no snap-to-zero, so after a
        // spike (a solar flare kills every WaterStation, which zeroes CoolingNet.CoolingCapacity and
        // sends every turbine's UncooledWater to full) it decays asymptotically and needs ~50k ticks
        // — most of an in-game day — before it underflows to exactly 0f. Testing it with "> 0f" like
        // vanilla Rimatomics does means the overheat branch stays latched for that whole day and the
        // throttle winds the rods to 0% without ever looking at grid load. 1 degree out of a 315
        // degree standard core temp is noise; treat anything under that as not overheating.
        private const float OverheatDeadband = 1f;

        // PowerNet.CurrentEnergyGainRate() only sums comps whose PowerOn is true, so a browned-out
        // grid — every consumer switched off, which is exactly the state a solar flare leaves behind —
        // reports its load as zero and looks like a huge surplus. Split the net into what consumers
        // want (including the switched-off ones that would run if fed) and what generators put out.
        private static void GridDemandAndGeneration(PowerNet net, out float demandW, out float generationW)
        {
            demandW = 0f;
            generationW = 0f;
            List<CompPowerTrader> comps = net.powerComps;
            for (int i = 0; i < comps.Count; i++)
            {
                CompPowerTrader trader = comps[i];
                if (trader.PowerOn)
                {
                    float output = trader.PowerOutput;
                    if (output >= 0f)
                        generationW += output;
                    else
                        demandW -= output;
                    continue;
                }

                float draw = trader.Props.PowerConsumption;
                if (draw > 0f && FlickUtility.WantsToBeOn(trader.parent) && !trader.parent.IsBrokenDown())
                    demandW += draw;
            }
        }

        // Thermal watts this core would make per unit of rod position at standard temperature.
        // Item_NuclearFuel.BasePowerLevel = PwerCurve(fuelLevel) * chainReaction * FinalFlux, and
        // MaxPowerLevel = MaxPwerCurve(1) * chainReaction, so dividing one by the other recovers the
        // flux-free chain-reaction factor. Works while the core is cold or rods are at zero.
        private static float ThermalPerFlux(reactorCore core)
        {
            float total = 0f;
            List<Item_NuclearFuel> grid = core.FuelGrid;
            for (int i = 0; i < grid.Count; i++)
            {
                Item_NuclearFuel rod = grid[i];
                float maxCurve = rod.MaxPwerCurveGetter(core);
                if (maxCurve <= 0f)
                    continue;
                total += rod.PwerCurveGetter(core) * (rod.MaxPowerLevel / maxCurve);
            }
            return total;
        }


        // ReactorControl.Tick's disengage check clears AutoThrottle the moment CoreLink.IsShutdown
        // goes true, so a FlareScram silently disarms auto-throttle and the reactor stays down after
        // the flare passes. Remember which consoles were armed when the flare hit, hold AutoThrottle
        // through it, and restart the core once the sky clears. Keyed by thingIDNumber; the entry is
        // rewritten every tick of the flare, so a manual SCRAM (which disarms AutoThrottle first)
        // drops out on its own and is never auto-restarted.
        private static readonly HashSet<int> FlareArmed = new HashSet<int>();

        // Last solve per console, surfaced on the inspect pane in dev mode so the tuning constants
        // can be judged against what the controller actually saw.
        public static readonly Dictionary<int, string> LastSolve = new Dictionary<int, string>();

        // Returns true while this console is inside a flare / flare-recovery window, during which the
        // normal "shutdown or unpowered means disarm" rule is suspended.
        private static bool HandleFlareHold(ReactorControl rc)
        {
            reactorCore core = rc.CoreLink;
            if (core == null)
                return false;

            Map map = ((Thing)rc).Map;
            int id = ((Thing)rc).thingIDNumber;

            if (map != null && map.GameConditionManager.ElectricityDisabled(map))
            {
                if (rc.AutoThrottle)
                    FlareArmed.Add(id);
                else
                    FlareArmed.Remove(id);
                return true;
            }

            if (!FlareArmed.Contains(id))
                return false;

            // Flare is over. If the player disarmed in the meantime, respect that and stand down.
            if (!rc.AutoThrottle)
            {
                FlareArmed.Remove(id);
                return false;
            }

            if (core.IsShutdown)
            {
                // AutoScram can also have fired during the flare (no cooling means postReturnTemp
                // climbs fast). Stay armed but keep the core down until it is back in a sane band, so
                // we never restart into a genuine overheat.
                if (core.postReturnTemp > 500f)
                    return true;

                // Set the field rather than calling Startup(), which is a [SyncMethod] and would be
                // re-broadcast if this ran under Rimworld Multiplayer. Tick is already deterministic.
                core.IsShutdown = false;
                Messages.Message(
                    "Reactor " + greekAlpha.getAlpha(rc.GreekID) + " restarting after solar flare, auto-throttle still engaged.",
                    new LookTargets((Thing)rc), MessageTypeDefOf.PositiveEvent, false);
            }

            // Hold until the grid has actually come back, so the post-flare brownout can't disarm us.
            if (core.powerComp.PowerOn && rc.powerComp.PowerOn)
                FlareArmed.Remove(id);
            return true;
        }

        // Batteries at or above this fraction are treated as full: track load exactly, no charge bias.
        private const float BatteryFullFrac = 0.99f;
        // Time constant for closing the battery deficit. The charge bias is deficit / ChargeTicks, so
        // it saturates at the plant ceiling while the bank is well down (full-capacity charging) and
        // shrinks in proportion as the bank fills — the "brake" is this taper. It has to be a few
        // times ResponseLagTicks or the output still in the pipe when the bias reaches zero would
        // overshoot; at ~4x the loop is damped and the taper only spans the last ~(ceiling - load) *
        // ChargeTicks / 60000 Wd, about a battery or two at 100 kW of headroom.
        private const float ChargeTicks = 900f;
        // Fraction of the solved correction applied per update, to damp out measurement lag.
        private const float SolveGain = 0.6f;
        // Settling time of the rod -> flux -> steam -> RPM cascade for a step command. The three
        // stages are FInterpTo at 2%/tick (tau ~50), SmoothDamp at ~3.1%/tick (tau ~32) and the
        // turbine's SmoothDamp at ~3.2%/tick (tau ~31); a three-lag cascade needs roughly twice the
        // summed time constants to effectively settle.
        private const float ResponseLagTicks = 225f;
        // How much earlier than strictly necessary to start the burn.
        private const float BurnSafetyFactor = 1.5f;
        // Fraction of the cooling ceiling to aim for while actually overheating.
        private const float OverheatBackoff = 0.9f;

        // reactorCore.Interps/Item_NuclearFuel.UpdateFluxShape make the plant exactly linear in flux:
        // BasePowerLevel = PwerCurve(FuelLevel) * ChainReaction * FinalFlux, summed into
        // MaxPowerForFuelLevel, then ThermalEnergy = MaxPowerForFuelLevel * RealTempPct. Downstream,
        // SteamLoopRatio = thermal / turbineCapacity and RPM tracks Clamp01 of it, so grid watts come
        // out as min(thermal, turbineCapacity, transformerCapacity). That inverts, which means the rod
        // setting for a wanted output can be solved outright instead of nudged towards.
        private struct PlantModel
        {
            public float ThermalPerFlux;   // W of core thermal per unit of FinalFlux
            public float CurrentOutput;    // W this reactor's transformers are putting on the grid now
            public float DeliveryCap;      // W the turbines + transformers can actually put on the grid
            public float SafeThermalMax;   // W of thermal beyond which we gain nothing or start cooking
            public float FluxSlewPerTick;  // fastest FinalFlux can move, this core, right now
            public bool CoolingKnown;      // false when every cooling net reads as unpowered/dry

            public static PlantModel For(reactorCore core)
            {
                PlantModel m = default(PlantModel);

                // Summed per rod rather than MaxPowerForFuelLevel / flux: that ratio is noise near
                // zero flux, and its numerator is refreshed one fuel rod per tick, so mid-ramp it is
                // divided by a flux it was not computed at. The per-rod form has neither problem.
                m.ThermalPerFlux = ThermalPerFlux(core);
                if (m.ThermalPerFlux < 1f)
                    m.ThermalPerFlux = Mathf.Max(core.MaxPowerPossible, 1f);

                float turbineCap = 0f;
                float transformerCap = 0f;
                float coolingLimitRatio = float.PositiveInfinity;

                SteamNet steam = core.SteamNet;
                if (steam != null && steam.Turbines != null)
                {
                    // Cooling and high-voltage capacity are per-net, so group rather than summing
                    // per-turbine, which would count a shared net once for every turbine on it.
                    Dictionary<CoolingNet, float> genPerCoolingNet = new Dictionary<CoolingNet, float>();
                    HashSet<HighVoltageNet> hvNets = new HashSet<HighVoltageNet>();

                    foreach (Turbine turbine in steam.Turbines)
                    {
                        float cap = turbine.GenerationCapacity;
                        turbineCap += cap;

                        CoolingNet cooling = turbine.CoolingNet;
                        if (cooling != null)
                        {
                            float sum;
                            genPerCoolingNet.TryGetValue(cooling, out sum);
                            genPerCoolingNet[cooling] = sum + cap;
                        }

                        HighVoltageNet hv = turbine.highVoltageNet;
                        if (hv != null && hv.Trannys != null && hvNets.Add(hv))
                        {
                            float hvCap = 0f;
                            foreach (Transformer tranny in hv.Trannys)
                                hvCap += tranny.Capacity;
                            transformerCap += hvCap;
                            // Transformer.Tick sets PowerOutput = Capacity * Clamp01(TurbineWatts /
                            // total capacity), so the net's whole contribution is just this.
                            m.CurrentOutput += Mathf.Min(hv.TurbineWatts, hvCap);
                        }
                    }

                    // Turbine.Tick only starts making UncooledWater once a cooling net's load ratio
                    // reaches 1, and that ratio is SteamLoopRatio * (gen capacity on the net) /
                    // CoolingCapacity. Invert for the steam ratio that keeps every net just under.
                    foreach (KeyValuePair<CoolingNet, float> pair in genPerCoolingNet)
                    {
                        float capacity = pair.Key.CoolingCapacity;
                        // Rimatomics ignores capacities at or below 1 itself; here that means the
                        // coolers are unpowered or dry — during a post-flare blackout, for instance.
                        // Treating that as a zero ceiling would refuse to make the power that brings
                        // the coolers back, so read it as unknown and let the overheat branch and
                        // AutoScram act as the backstop instead.
                        if (capacity > 1f && pair.Value > 0f)
                            coolingLimitRatio = Mathf.Min(coolingLimitRatio, capacity / pair.Value);
                    }
                }

                if (turbineCap <= 0f)
                    turbineCap = Mathf.Max(core.MaxPowerPossible, 1f);
                if (transformerCap <= 0f)
                    transformerCap = float.PositiveInfinity;

                m.DeliveryCap = Mathf.Min(turbineCap, transformerCap);

                // Past turbineCap the steam ratio is clamped at 1 and buys no more power, so that is
                // the ceiling even when cooling would allow more.
                m.CoolingKnown = !float.IsPositiveInfinity(coolingLimitRatio);
                float coolingCap = m.CoolingKnown
                    ? turbineCap * coolingLimitRatio
                    : float.PositiveInfinity;
                m.SafeThermalMax = Mathf.Min(m.DeliveryCap, coolingCap);

                // RealControlRodPosition is SmoothDamp(.., smoothTime 1, maxSpeed ControlRodSpeed,
                // dt 0.016). Its per-tick step tops out at maxSpeed * smoothTime * (1 - exp), and
                // ControlRodSpeed itself falls off above 500 degrees, so a hot core genuinely moves
                // its rods slower — which is what the burn timing has to account for.
                m.FluxSlewPerTick = core.ControlRodSpeed * SmoothDampStepFraction;
                return m;
            }
        }

        // 1 - exp for Mathf.SmoothDamp with smoothTime 1 and deltaTime 0.016: x = 2 * 0.016 = 0.032,
        // exp = 1 / (1 + x + 0.48x^2 + 0.235x^3).
        private const float SmoothDampStepFraction = 0.0309f;

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

            bool flareHold = HandleFlareHold(__instance);

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

            if (!flareHold && __instance.AutoThrottle &&
                (__instance.CoreLink.IsShutdown || !__instance.CoreLink.powerComp.PowerOn || !__instance.powerComp.PowerOn))
            {
                __instance.AutoThrottle = false;
                __instance.CoreLink.TargetControlRodTo = __instance.CoreLink.TargetControlRodPosition;
            }

            // Vanilla also waits for TargetControlRodPosition to catch up to TargetControlRodTo
            // before stepping again, because it nudges by fixed increments and would otherwise stack
            // them faster than the rods can move. The solve below is absolute rather than
            // incremental — it recomputes the wanted flux from measurements every time — so it
            // cannot wind up, and holding it back would only cost ~300 ticks of response.
            if (!__instance.AutoThrottle ||
                __instance.CoreLink.IsShutdown ||
                !Gen.IsHashIntervalTick((Thing)(object)__instance, 10))
                return false;


            reactorCore core = __instance.CoreLink;
            PowerNet net = __instance.powerComp.PowerNet;

            float demandW, generationW;
            GridDemandAndGeneration(net, out demandW, out generationW);
            float netWatts = generationW - demandW;
            float netWdPerTick = netWatts * CompPower.WattsToWattDaysPerTick;

            float batteryCapacity = 0f;
            float batteryStored = 0f;
            foreach (CompPowerBattery bat in net.batteryComps)
            {
                batteryCapacity += bat.Props.storedEnergyMax;
                batteryStored += bat.StoredEnergy;
            }

            PlantModel plant = PlantModel.For(core);

            // ---- Solve for the flux that balances the grid -------------------------------------
            // What the transformers are putting out right now, and therefore the output level at
            // which the net would sit at exactly zero. This holds even with other generators on the
            // net, because moving our output moves the net one-for-one — and because it is measured
            // rather than predicted, it stays correct mid-ramp instead of chasing its own tail.
            float demandWatts = plant.CurrentOutput - netWatts;

            // At or above BatteryFullFrac just track the load. Below it, add a bias proportional to
            // the deficit measured from that line (continuous through it, so there is no step to
            // chatter across); the solve clamps this to the plant ceiling, which is what gives
            // full-capacity charging on an empty bank and the proportional taper on the way up.
            float chargeBias = 0f;
            float deficitWd = (batteryCapacity * BatteryFullFrac) - batteryStored;
            if (deficitWd > 0f)
                chargeBias = deficitWd / ChargeTicks / CompPower.WattsToWattDaysPerTick;

            float targetThermal = Mathf.Clamp(demandWatts + chargeBias, 0f, plant.SafeThermalMax);
            float solvedFlux = Mathf.Clamp01(targetThermal / plant.ThermalPerFlux);

            // ---- Suicide burn ------------------------------------------------------------------
            // The rod chain is three first-order lags in series, so commanding exactly the flux we
            // need means approaching it asymptotically. If the batteries will hit empty before that
            // settles, command the full safe flux instead: overshooting the command makes the early
            // part of the ramp steeper, so generation crosses demand sooner. We drop back to the
            // solved value as soon as there is margin again.
            float flux = core.RealControlRodPosition;
            bool burning = false;
            if (netWdPerTick < 0f && solvedFlux > flux)
            {
                float ticksToEmpty = batteryStored / -netWdPerTick;
                float ticksToRespond = ResponseLagTicks
                    + (solvedFlux - flux) / Mathf.Max(plant.FluxSlewPerTick, 1E-06f);
                if (ticksToEmpty < ticksToRespond * BurnSafetyFactor)
                {
                    solvedFlux = Mathf.Clamp01(plant.SafeThermalMax / plant.ThermalPerFlux);
                    burning = true;
                }
            }

            if (core.overheating > OverheatDeadband)
            {
                // SafeThermalMax should keep us out of here, but if we are already cooking, cap the
                // solve. With a readable cooling capacity there is a number to aim at, so sit under
                // it rather than ratcheting to zero and browning the colony out on the way down;
                // with no reading there is nothing to aim at, so walk the actual flux down instead.
                float relief = plant.CoolingKnown
                    ? Mathf.Clamp01(plant.SafeThermalMax * OverheatBackoff / plant.ThermalPerFlux)
                    : Mathf.Max(0f, flux - 0.05f);
                solvedFlux = Mathf.Min(solvedFlux, relief);
                burning = false;
            }

            float rodTarget = core.TargetControlRodTo;
            if (burning)
            {
                rodTarget = solvedFlux;
            }
            else
            {
                // Damped so that measurement lag (ThermalEnergy refreshes every 10 ticks, turbine RPM
                // trails by ~30) cannot set up a ring.
                rodTarget += (solvedFlux - rodTarget) * SolveGain;
            }

            // Unless we are overheating, never reduce control rods below 1% to prevent shutdown
            if (core.overheating <= OverheatDeadband)
                rodTarget = Mathf.Max(rodTarget, 0.01f);

            core.TargetControlRodTo = Mathf.Clamp01(rodTarget);

            if (Prefs.DevMode)
            {
                LastSolve[((Thing)__instance).thingIDNumber] =
                    "A/T load " + (demandWatts / 1000f).ToString("0.0") + "kW"
                    + " bias " + (chargeBias / 1000f).ToString("0.0") + "kW"
                    + " cap " + (plant.SafeThermalMax / 1000f).ToString("0") + "kW"
                    + " W/flux " + (plant.ThermalPerFlux / 1000f).ToString("0") + "k"
                    + " solve " + solvedFlux.ToString("0.000")
                    + (burning ? " BURN" : "")
                    + " deficit " + deficitWd.ToString("0") + "Wd";
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ReactorControl), "GetInspectString")]
    public static class Patch_ReactorControl_GetInspectString
    {
        public static void Postfix(ReactorControl __instance, ref string __result)
        {
            if (!Prefs.DevMode || !__instance.AutoThrottle)
                return;
            string line;
            if (Patch_ReactorControl_Tick.LastSolve.TryGetValue(((Thing)__instance).thingIDNumber, out line))
                __result = __result + "\n" + line;
        }
    }

    // Vanilla Rimatomics refuses to engage auto-throttle unless the power net is currently sitting
    // within +/-5000W of balance ("AutothrotRange"). That is the one moment you most want to hand
    // control over, and after a flare the net is nowhere near balanced. Widen the window to infinite
    // by rewriting the two comparison constants; everything else about the button is untouched.
    [HarmonyPatch(typeof(Dialog_ReactorConsole), "DrawRods")]
    public static class Patch_Dialog_ReactorConsole_DrawRods
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int patched = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float)
                {
                    float value = (float)ins.operand;
                    if (value == 5000f)
                    {
                        patched++;
                        yield return new CodeInstruction(OpCodes.Ldc_R4, float.PositiveInfinity).WithLabels(ins.labels).WithBlocks(ins.blocks);
                        continue;
                    }
                    if (value == -5000f)
                    {
                        patched++;
                        yield return new CodeInstruction(OpCodes.Ldc_R4, float.NegativeInfinity).WithLabels(ins.labels).WithBlocks(ins.blocks);
                        continue;
                    }
                }
                yield return ins;
            }

            if (patched != 2)
                Log.Warning("[SmartThrottle] Dialog_ReactorConsole.DrawRods: expected 2 auto-throttle range constants, rewrote " + patched + ". Rimatomics may have changed.");
        }
    }
}
