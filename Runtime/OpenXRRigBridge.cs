using System;
using UnityEngine;
using UnityEngine.XR.Management;

namespace SensorFlex.Recorder
{
    // Hooks into the OpenXR runtime for headset recording, filled in by the optional
    // SensorFlex.Recorder.OpenXR assembly (SensorFlexOpenXRFeature) when OpenXR is installed
    // and the feature is enabled. The core recorder never references OpenXR directly.
    public static class OpenXRRigBridge
    {
        // Head (OpenXR VIEW space) pose at a CLOCK_MONOTONIC time, in the app's tracking space,
        // already converted to Unity conventions (left-handed, +Z forward).
        public static Func<long, (bool ok, Pose pose)> LocateHeadAtMonotonicNs;

        // OpenXR XrTime → CLOCK_MONOTONIC nanoseconds (0 when the conversion fails).
        public static Func<long, long> XrTimeToMonotonicNs;

        public static bool IsAvailable => LocateHeadAtMonotonicNs != null && XrTimeToMonotonicNs != null;
    }

    internal static class XrLoaderInfo
    {
        public static bool IsOpenXRActive
        {
            get
            {
                var loader = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager?.activeLoader : null;
                return loader != null && loader.GetType().Name.Contains("OpenXR");
            }
        }
    }
}
