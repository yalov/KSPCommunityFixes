﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using UnityEngine;

namespace KSPCommunityFixes.BugFixes
{
    // Judging from the VesselSAS.GetTotalVesselTorque() implementation, the convention seems to be that both pos and neg must be positive values.
    // ModuleReactionWheel and ModuleGimbal follow that convention. ModuleRCS doesn't, but the implementation is 100% broken since forever and
    // results are garbage anyway.

    // This convention is NOT followed by the stock ModuleControlSurface.GetPotentialTorque() implementation, which will happily 
    // return negative values both for "pos" and "neg".

    // Quick review of how this is handled in the modding ecosystem :
    // - *It seems* Mechjeb doesn't care about a value being from "pos" or "neg", it assume a negative value from either of the vector3 is a negative
    //   torque component (ie, if "pos.x" or "neg.x" is negative, it add that as negative avaiable torque around x).
    //   Ref : https://github.com/MuMech/MechJeb2/blob/f5c1193813da7d2e2e347f963dd4ee4b7fb11a90/MechJeb2/VesselState.cs#L1073-L1076
    //   Ref2 : https://github.com/MuMech/MechJeb2/blob/f5c1193813da7d2e2e347f963dd4ee4b7fb11a90/MechJeb2/Vector6.cs#L82-L93
    // - kOS assume that the absolute value should be used.
    //   (side note : kOS reimplements ModuleReactionWheel.GetPotentialTorque() to get around the authority limiter bug)
    //   Ref : https://github.com/KSP-KOS/KOS/blob/7b7874153bc6c428404b3a1a913487b2fd0a9d99/src/kOS/Control/SteeringManager.cs#L658-L664
    // - TCA doesn't seem aware of the possibility of negative values, it assume they are positive.
    //   (side note : TCA apply the authority limiter to the stock ModuleReactionWheel.GetPotentialTorque() results)
    //   Ref : https://github.com/allista/ThrottleControlledAvionics/blob/b79a7372ab69616801f9953256b43ee872b90cf2/VesselProps/TorqueProps.cs#L167-L169
    // - Atmospheric Autopilot replace the stock module implementation by its own and doesn't use the interface at all (!!!)
    //   Ref : https://github.com/Boris-Barboris/AtmosphereAutopilot/blob/master/AtmosphereAutopilot/SyncModuleControlSurface.cs
    // - FAR implements a replacement for ModuleControlSurface and consequently has a custom GetPotentialTorque() implentation.
    //   The implementation will *always* return positive "pos" values and negative "neg" values
    //   Ref : https://github.com/dkavolis/Ferram-Aerospace-Research/blob/95e127ae140b4be9699da8783d24dd8db726d753/FerramAerospaceResearch/LEGACYferram4/FARControllableSurface.cs#L294-L300

    // I believe the only mod being "right" is kOS. The Mechjeb implementation works when used alongside FAR, but is wrong for stock. But the end, since
    // MechJeb only keep the maximum absolute value between pos/neg, that error might end up not 

    // All this being said, the implementation seems to output bery questionable results overall :
    // - The stock AIRBRAKE never report any available torque (so I guess the implementation doesn't work for the derived ModuleAeroSurface module)
    // - Placing an elevon parallel to the side of rocket (so it should be able to provide pitch or yaw torque) results in zero available torque

    // As for the neg/pos direction convention, we are consistent with the ModuleGimbal.GetPotentialTorque() implementation, which return a "pos" value
    // for a positive rotation around the axis (this is also consistent with the Unity left-handed coordinate system). ModuleControlSurface *seems* to
    // follow the convention too, but this is hard to validate with certainty.


    // - VERIFY THAT MODULEGIMBAL convention is to return the **actuation** torque (ie, check that a non-COM aligned engine return the same values as a COM aligned one)
    // - our issue with modulecontrolsurface seems to be that it returns the total torque, not just the "potential" additional torque

    class GetPotentialTorqueFixes : BasePatch
    {
        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleReactionWheel), nameof(ModuleReactionWheel.GetPotentialTorque)),
                this));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleRCS), nameof(ModuleRCS.GetPotentialTorque)),
                this));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleControlSurface), nameof(ModuleControlSurface.GetPotentialTorque)),
                this));

#if DEBUG
            patches.Add(new PatchInfo(
                PatchMethodType.Postfix,
                AccessTools.Method(typeof(ModuleGimbal), nameof(ModuleGimbal.GetPotentialTorque)),
                this));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(ModuleLiftingSurface), nameof(ModuleLiftingSurface.DestroyLiftAndDragArrows)),
                this));
#endif
        }

        #region ModuleReactionWheel

        // Fix reaction wheels reporting incorrect available torque when the "Wheel Authority" tweakable is set below 100%.
        static bool ModuleReactionWheel_GetPotentialTorque_Prefix(ModuleReactionWheel __instance, out Vector3 pos, out Vector3 neg)
        {
            if (__instance.moduleIsEnabled && __instance.wheelState == ModuleReactionWheel.WheelState.Active && __instance.actuatorModeCycle != 2)
            {
                float authorityLimiter = __instance.authorityLimiter * 0.01f;
                neg.x = pos.x = __instance.PitchTorque * authorityLimiter;
                neg.y = pos.y = __instance.RollTorque * authorityLimiter;
                neg.z = pos.z = __instance.YawTorque * authorityLimiter;
                return false;
            }

            pos = neg = Vector3.zero;
            return false;
        }

        #endregion

        #region ModuleRCS

        // The stock implementation is 100% broken, this is a complete replacement
        static bool ModuleRCS_GetPotentialTorque_Prefix(ModuleRCS __instance, out Vector3 pos, out Vector3 neg)
        {
            pos = Vector3.zero;
            neg = Vector3.zero;

            if (!__instance.moduleIsEnabled 
                || !__instance.rcsEnabled 
                || !__instance.rcs_active 
                || __instance.IsAdjusterBreakingRCS() 
                || __instance.isJustForShow 
                || __instance.flameout
                || (__instance.part.ShieldedFromAirstream && !__instance.shieldedCanThrust))
            {
                return false;
            }

            Vector3 enabledRotations = new Vector3(__instance.enablePitch ? 1f : 0f, __instance.enableRoll ? 1f : 0f, __instance.enableYaw ? 1f : 0f);

            if (enabledRotations == Vector3.zero)
                return false;

            float power = __instance.thrusterPower * __instance.thrustPercentage * 0.01f;
            Vector3 predictedCoM = __instance.vessel.CurrentCoM;

            for (int i = __instance.thrusterTransforms.Count - 1; i >= 0; i--)
            {
                Transform thruster = __instance.thrusterTransforms[i];

                if (thruster.position == Vector3.zero || !thruster.gameObject.activeInHierarchy)
                    continue;

                Vector3 thrusterPosFromCoM = thruster.position - predictedCoM;
                Vector3 thrustDirection = __instance.useZaxis ? thruster.forward : thruster.up;

                float thrusterPower = power;
                if (FlightInputHandler.fetch.precisionMode)
                {
                    if (__instance.useLever)
                    {
                        float leverDistance = __instance.GetLeverDistance(thruster, thrustDirection, predictedCoM);
                        if (leverDistance > 1f)
                        {
                            thrusterPower /= leverDistance;
                        }
                    }
                    else
                    {
                        thrusterPower *= __instance.precisionFactor;
                    }
                }

                Vector3 thrusterThrust = thrustDirection * thrusterPower;
                Vector3 thrusterTorque = Vector3.Cross(thrusterPosFromCoM, thrusterThrust);
                Vector3 vesselSpaceTorque = Vector3.Scale(__instance.vessel.ReferenceTransform.InverseTransformDirection(thrusterTorque), enabledRotations);

                if (vesselSpaceTorque.x > 0f)
                    pos.x += vesselSpaceTorque.x;
                else
                    neg.x -= vesselSpaceTorque.x;

                if (vesselSpaceTorque.y > 0f)
                    pos.y += vesselSpaceTorque.y;
                else
                    neg.y -= vesselSpaceTorque.y;

                if (vesselSpaceTorque.z > 0f)
                    pos.z += vesselSpaceTorque.z;
                else
                    neg.z -= vesselSpaceTorque.z;
            }

#if DEBUG
            TorqueUIModule ui = __instance.part.FindModuleImplementing<TorqueUIModule>();
            if (ui != null)
            {
                ui.pos = pos;
                ui.neg = neg;
            }
#endif
            return false;
        }

        #endregion

        #region ModuleControlSurface

        // The stock ModuleControlSurface.GetPotentialTorque() implementation has several issues and its results are overall very wrong :
        // - It doesn't take drag forces into account (only lift).
        // - It attempt to provide actuation torque (ie, the torque difference between pos/neg actuation and the neutral state) by substracting
        //   the neutral lift force vector to the actuated pos/neg force vectors. This is wrong and produce garbage results, it's the resulting
        //   torque from the neutral vector that should be substracted to the resulting torque from the pos/neg force vectors.
        // - It entirely fails to correct the raw torque results for the pitch/roll/yaw actuation inversion and clamping logic, resulting in
        //   wrongly negative values and random inversions of the neg / pos value.
        // - For reasons that escape my understanding entirely, it multiply the torque results by the vessel CoM to Part vector, resulting in 
        //   more non-sense sign inversions and a near squaring of the result magnitude.
        // - It generally doesn't handle correctly control surfaces in the deployed state.

        // This reimplementation fixes all the above issues.
        // It still has a few shortcomings. Notably, it partially reuse results from the previous FixedUpdate() and mix them with current values.
        // This mean the results are slightly wrong, but this saves quite a bit of extra processing in that already quite performance heavy method,
        // and the error magnitude shoudln't matter for potential applications of GetPotentialTorque().
        // It shall also be noted that the result magnitude is an approximation when the actuation is clamped by the module (ie, when the control
        // surface neutral state isn't aligned with the airflow), with the error being greater when the allowed actuation is lower.

        // Note that the results can still return negative components. The meaning of a negative value is that the actuation of that component will
        // induce a torque in the opposite direction. For example, a negative pos.x value mean that for a positive roll actuation (ctrlState.roll > 0),
        // the control surface will produce a torque incuding a negative roll, essentially reducing the total available torque is that direction.
        // This become usefull when summing the total torque available on the vessel, and this mean the summing should be done as follow :

        // foreach (ITorqueProvider torqueProvider)
        // {
        //   torqueProvider.GetPotentialTorque(out Vector3 pos, out Vector3 neg);
        //   posTorque += pos;
        //   negTorque += neg;
        // }
        // if (posTorque.x < 0f) posTorque.x = 0f;
        // if (posTorque.y < 0f) posTorque.y = 0f;
        // if (posTorque.z < 0f) posTorque.z = 0f;
        // if (negTorque.x < 0f) negTorque.x = 0f;
        // if (negTorque.y < 0f) negTorque.y = 0f;
        // if (negTorque.z < 0f) negTorque.z = 0f;

        static bool ModuleControlSurface_GetPotentialTorque_Prefix(ModuleControlSurface __instance, out Vector3 pos, out Vector3 neg)
        {
            pos = Vector3.zero;
            neg = Vector3.zero;

            if (__instance.Qlift == 0.0 || (__instance.ignorePitch && __instance.ignoreYaw && __instance.ignoreRoll))
                return false;

            if (__instance.displaceVelocity)
            {
                // This case is for handling "propeller blade" control surfaces. Those have a completely different control
                // scheme. This is the stock GetPotentialTorque() implementation for them, I've no idea how correct it is and
                // just don't have the motivation to investigate.
                Vector3 potentialForcePos = __instance.GetPotentialLift(true);
                Vector3 potentialForceNeg = __instance.GetPotentialLift(false);
                float magnitude = __instance.vesselBladeLiftReference.magnitude;
                pos = Vector3.Dot(potentialForcePos, __instance.vesselBladeLiftReference) * __instance.potentialBladeControlTorque / magnitude;
                neg = Vector3.Dot(potentialForceNeg, __instance.vesselBladeLiftReference) * __instance.potentialBladeControlTorque / magnitude;
            }
            else
            {
                // The stock method doesn't handle correctly the deployed state :
                // - It always apply `currentDeployAngle` in the AngleAxis() call, but that field is updated only if `mcs.deploy == true`, and
                //   it isn't reverted to 0 if deploy changes from true to false, resulting in the deployed angle still being applied after un-deploying.
                // - It always substract `baseLiftForce` which is always the non-deployed lift vector, resulting in the positive deflection being twice
                //   what it should be and the negative deflection being always zero.

                float deployAngle;
                Vector3 neutralForce;
                if (!__instance.deploy)
                {
                    deployAngle = 0f;
                    neutralForce = __instance.baseLiftForce * __instance.ctrlSurfaceArea;
                    neutralForce += GetDragForce(__instance, __instance.absDot, (float)__instance.part.machNumber);
                }
                else
                {
                    deployAngle = __instance.currentDeployAngle;
                    Vector3 rhsNeutral = Quaternion.AngleAxis(deployAngle, __instance.baseTransform.rotation * Vector3.right) * __instance.baseTransform.forward;
                    float dotNeutral = Vector3.Dot(__instance.nVel, rhsNeutral);
                    float absDotNeutral = Mathf.Abs(dotNeutral);
                    float machNumber = (float)__instance.part.machNumber;
                    neutralForce = __instance.GetLiftVector(rhsNeutral, dotNeutral, absDotNeutral, __instance.Qlift, machNumber) * __instance.ctrlSurfaceArea;
                    neutralForce += GetDragForce(__instance, absDotNeutral, machNumber);
                }

                Vector3 potentialForcePos = GetPotentialLiftAndDrag(__instance, deployAngle, true);
                Vector3 potentialForceNeg = GetPotentialLiftAndDrag(__instance, deployAngle, false);

                Vector3 currentCoM = __instance.vessel.CurrentCoM;
                Vector3 partPosition = __instance.part.Rigidbody.worldCenterOfMass - currentCoM;

                Vector3 posTorque = __instance.vessel.ReferenceTransform.InverseTransformDirection(Vector3.Cross(partPosition, potentialForcePos));
                Vector3 negTorque = __instance.vessel.ReferenceTransform.InverseTransformDirection(Vector3.Cross(partPosition, potentialForceNeg));

                Vector3 neutralTorque = __instance.vessel.ReferenceTransform.InverseTransformDirection(Vector3.Cross(partPosition, neutralForce));

                posTorque -= neutralTorque;
                negTorque -= neutralTorque;

                // At this point, we have raw torque results for two given actuations. However, GetPotentialTorque() is supposed to
                // represent the torque produced by pitch/roll/yaw requests. We need to determine which actuation is applied for a pos=(1,1,1)
                // and neg=(-1,-1,-1) ctrlState, then swap the raw torque components accordingly. Said otherwise, to take an example,
                // we need to answer : does a positive pitch request results in a positive or negative actuation ?
                // Additionally, ModuleControlSurface will clamp actuation magnitude depending on the surface orientation, and apply an
                // additional (weird) clamping for the deployed state.

                // The following code is essentially derived from ModuleControlSurface.FixedCtrlSurfaceUpdate(), which is the method responsible
                // for updating the control surface angle according to the vessel.ctrlState pitch/roll/yaw request.

                float deployAction;
                if (__instance.deploy)
                {
                    deployAction = __instance.usesMirrorDeploy
                        ? ((__instance.deployInvert ? (-1f) : 1f) * (__instance.partDeployInvert ? (-1f) : 1f) * (__instance.mirrorDeploy ? (-1f) : 1f))
                        : ((__instance.deployInvert ? (-1f) : 1f) * Mathf.Sign((Quaternion.Inverse(__instance.vessel.ReferenceTransform.rotation) * (__instance.baseTransform.position - currentCoM)).x));

                    deployAction *= -1f;
                }
                else
                {
                    deployAction = 0f;
                }

                Vector3 comRelPos = __instance.baseTransform.InverseTransformPoint(currentCoM);

#if DEBUG
                Vector3 posAction = Vector3.zero;
                Vector3 negAction = Vector3.zero;
#endif

                if (!__instance.ignorePitch)
                {
                    Vector3 pitchVector = __instance.vessel.ReferenceTransform.rotation * new Vector3(1f, 0f, 0f);
                    float pitchActionPos = Vector3.Dot(pitchVector, __instance.baseTransform.rotation * Vector3.right);
                    if (comRelPos.y < 0f)
                        pitchActionPos = -pitchActionPos;

                    float pitchActionNeg = -pitchActionPos;

                    if (__instance.deploy)
                    {
                        pitchActionPos = Mathf.Clamp(pitchActionPos + deployAction, -1.5f, 1.5f) - deployAction;
                        pitchActionNeg = Mathf.Clamp(pitchActionNeg + deployAction, -1.5f, 1.5f) - deployAction;
                    }

                    // I hope I got this right. TBH, this was mostly a trial and error job.
                    // - the control surface actuation direction depends on sign of the action
                    // - then we clamp and inverse the raw torque by the action magnitude
                    //   note that this direct scaling is a rough approximation, as the torque output vs actuation function isn't linear,
                    //   but the whole thing is computationally intensive (and complex) enough already...
                    if (pitchActionPos > 0f)
                    {
                        pos.x = negTorque.x * pitchActionPos;
                        neg.x = posTorque.x * pitchActionNeg;
                    }
                    else
                    {
                        pos.x = posTorque.x * pitchActionNeg;
                        neg.x = negTorque.x * pitchActionPos;
                    }
#if DEBUG
                    posAction.x = pitchActionPos;
                    negAction.x = pitchActionNeg;
#endif
                }

                if (!__instance.ignoreYaw)
                {
                    Vector3 yawVector = __instance.vessel.ReferenceTransform.rotation * new Vector3(0f, 0f, 1f);
                    float yawActionPos = Vector3.Dot(yawVector, __instance.baseTransform.rotation * Vector3.right);
                    if (comRelPos.y < 0f)
                        yawActionPos = -yawActionPos;

                    float yawActionNeg = -yawActionPos;

                    if (__instance.deploy)
                    {
                        yawActionPos = Mathf.Clamp(yawActionPos + deployAction, -1.5f, 1.5f) - deployAction;
                        yawActionNeg = Mathf.Clamp(yawActionNeg + deployAction, -1.5f, 1.5f) - deployAction;
                    }

                    if (yawActionPos > 0f)
                    {
                        pos.z = negTorque.z * yawActionPos;
                        neg.z = posTorque.z * yawActionNeg;
                    }
                    else
                    {
                        pos.z = posTorque.z * yawActionNeg;
                        neg.z = negTorque.z * yawActionPos;
                    }
#if DEBUG
                    posAction.z = yawActionPos;
                    negAction.z = yawActionNeg;
#endif
                }

                if (!__instance.ignoreRoll)
                {
                    // optimization note : we could get rollAction by doing `rollAction = mcs.roll / ctrlStateRoll` where
                    // ctrlStateRoll is the `vessel.ctrlState.roll` value from the last fixedUpdate(). 
                    // But implementing that would be a mess, and the value would be slightly wrong due to being a frame outdated
                    // (altough this won't matter much, the overall GetPotentialTorque() implementation already rely on a bunch of
                    // one-frame outdated values for performance optimization reasons)

                    Vector3 rhs = new Vector3(comRelPos.x, 0f, comRelPos.z);

                    float rollActionPos = Vector3.Dot(Vector3.right, rhs)
                                      * (1f - (Mathf.Abs(Vector3.Dot(rhs.normalized, Quaternion.Inverse(__instance.baseTransform.rotation) * __instance.vessel.ReferenceTransform.up)) * 0.5f + 0.5f))
                                      * Mathf.Sign(Vector3.Dot(__instance.baseTransform.up, __instance.vessel.ReferenceTransform.up))
                                      * Mathf.Sign(__instance.ctrlSurfaceRange)
                                      * -1f;

                    rollActionPos = Mathf.Clamp(rollActionPos, -1f, 1f);

                    float rollActionNeg = -rollActionPos;

                    if (__instance.deploy)
                    {
                        rollActionPos = Mathf.Clamp(rollActionPos + deployAction, -1.5f, 1.5f) - deployAction;
                        rollActionNeg = Mathf.Clamp(rollActionNeg + deployAction, -1.5f, 1.5f) - deployAction;
                    }

                    if (rollActionPos > 0f)
                    {
                        pos.y = negTorque.y * rollActionPos;
                        neg.y = posTorque.y * rollActionNeg;
                    }
                    else
                    {
                        pos.y = posTorque.y * rollActionNeg;
                        neg.y = negTorque.y * rollActionPos;
                    }
#if DEBUG
                    posAction.y = rollActionPos;
                    negAction.y = rollActionNeg;
#endif
                }

#if DEBUG
                TorqueUIModule ui = __instance.part.FindModuleImplementing<TorqueUIModule>();
                if (ui != null)
                {
                    ui.pos = pos;
                    ui.neg = neg;

                    ui.Fields["spos"].guiActive = true;
                    ui.spos = posTorque;

                    ui.Fields["sneg"].guiActive = true;
                    ui.sneg = negTorque;

                    ui.Fields["posAction"].guiActive = true;
                    ui.posAction = posAction;

                    ui.Fields["negAction"].guiActive = true;
                    ui.negAction = negAction;
                }


                if (__instance.liftArrow == null)
                    __instance.liftArrow = ArrowPointer.Create(__instance.baseTransform, __instance.part.CoLOffset, Vector3.zero, 0f, Color.blue, true);

                __instance.liftArrow.Direction = neutralForce;
                __instance.liftArrow.Length = neutralForce.magnitude * PhysicsGlobals.AeroForceDisplayScale;


                if (__instance.axisArrow == null)
                    __instance.axisArrow = ArrowPointer.Create(__instance.baseTransform, __instance.part.CoLOffset, Vector3.zero, 0f, Color.green, true);

                __instance.axisArrow.Direction = potentialForcePos;
                __instance.axisArrow.Length = potentialForcePos.magnitude * PhysicsGlobals.AeroForceDisplayScale;

                if (__instance.velocityArrow == null)
                    __instance.velocityArrow = ArrowPointer.Create(__instance.baseTransform, __instance.part.CoLOffset, Vector3.zero, 0f, Color.red, true);

                __instance.velocityArrow.Direction = potentialForceNeg;
                __instance.velocityArrow.Length = potentialForceNeg.magnitude * PhysicsGlobals.AeroForceDisplayScale;
#endif
            }

            return false;
        }

        public static Vector3 GetPotentialLiftAndDrag(ModuleControlSurface mcs, float deployAngle, bool positiveDeflection)
        {
            float deflectionDir = positiveDeflection ? 1f : -1f;
            float angle = deployAngle + (deflectionDir * mcs.ctrlSurfaceRange * mcs.authorityLimiter * 0.01f);
            Vector3 rhs = Quaternion.AngleAxis(angle, mcs.baseTransform.rotation * Vector3.right) * mcs.baseTransform.forward;
            float dot = Vector3.Dot(mcs.nVel, rhs);
            float absDot = Mathf.Abs(dot);
            float machNumber = (float) mcs.part.machNumber;
            Vector3 result = mcs.GetLiftVector(rhs, dot, absDot, mcs.Qlift, machNumber) * mcs.ctrlSurfaceArea;
            result += GetDragForce(mcs, absDot, machNumber);
            return result;
        }

        public static Vector3 GetDragForce(ModuleControlSurface mcs, float absDot, float machNumber)
        {
            if (!mcs.useInternalDragModel || (mcs.nodeEnabled && mcs.attachNode.attachedPart != null))
                return Vector3.zero;

            float dragScalar = mcs.dragCurve.Evaluate(absDot) * mcs.dragMachCurve.Evaluate(machNumber);
            dragScalar *= mcs.deflectionLiftCoeff;
            if (dragScalar != 0f && !float.IsNaN(dragScalar))
            {
                dragScalar = (float)mcs.Qdrag * dragScalar * PhysicsGlobals.LiftDragMultiplier;
                return -mcs.nVel * dragScalar * mcs.ctrlSurfaceArea;
            }

            return Vector3.zero;
        }

        #endregion

#if DEBUG

        static bool ModuleLiftingSurface_DestroyLiftAndDragArrows_Prefix() => false;

        static void ModuleGimbal_GetPotentialTorque_Postfix(ModuleGimbal __instance, ref Vector3 pos, ref Vector3 neg)
        {
            TorqueUIModule ui = __instance.part.FindModuleImplementing<TorqueUIModule>();
            ui.pos = pos;
            ui.neg = neg;
        }

#endif
    }

#if DEBUG

    public class TorqueUIModule : PartModule
    {
        [KSPField(guiActive = true, guiFormat = "F1")]
        public Vector3 pos;
        [KSPField(guiActive = true, guiFormat = "F1")]
        public Vector3 neg;
        [KSPField(guiActive = false, guiFormat = "F1")]
        public Vector3 spos;
        [KSPField(guiActive = false, guiFormat = "F1")]
        public Vector3 sneg;
        [KSPField(guiActive = false, guiFormat = "F1")]
        public Vector3 posAction;
        [KSPField(guiActive = false, guiFormat = "F1")]
        public Vector3 negAction;
        [KSPField(guiActive = false, guiFormat = "F1")]
        public Vector3 actionV;
    }

#endif
}
