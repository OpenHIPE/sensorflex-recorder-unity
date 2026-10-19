using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace SensorFlex.Recorder
{
    // OpenXR support for headset recording: fills OpenXRRigBridge with
    //   • head pose at any recent CLOCK_MONOTONIC time (xrLocateSpace of a VIEW space), and
    //   • XrTime → CLOCK_MONOTONIC conversion (XR_KHR_convert_timespec_time).
    // Enable it under Project Settings → XR Plug-in Management → OpenXR → Android features.
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName = "SensorFlex Recorder Support",
        Desc = "Head pose at past timestamps and OpenXR/monotonic time conversion for SensorFlex headset recording.",
        Company = "SensorFlex",
        OpenxrExtensionStrings = "XR_KHR_convert_timespec_time",
        Version = "1.0.0",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        FeatureId = FeatureId)]
#endif
    public sealed class SensorFlexOpenXRFeature : OpenXRFeature
    {
        public const string FeatureId = "com.sensorflex.recorder.openxr";

        const int XR_SUCCESS = 0;
        const int XR_TYPE_REFERENCE_SPACE_CREATE_INFO = 37;
        const int XR_TYPE_SPACE_LOCATION = 42;
        const int XR_REFERENCE_SPACE_TYPE_VIEW = 1;
        const ulong XR_SPACE_LOCATION_ORIENTATION_VALID = 0x1, XR_SPACE_LOCATION_POSITION_VALID = 0x2;

        [StructLayout(LayoutKind.Sequential)] struct Timespec { public long sec; public long nsec; }
        [StructLayout(LayoutKind.Sequential)] struct XrPosef { public float qx, qy, qz, qw, px, py, pz; }
        [StructLayout(LayoutKind.Sequential)] struct XrReferenceSpaceCreateInfo { public int type; public IntPtr next; public int referenceSpaceType; public XrPosef pose; }
        [StructLayout(LayoutKind.Sequential)] struct XrSpaceLocation { public int type; public IntPtr next; public ulong locationFlags; public XrPosef pose; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_GetInstanceProcAddr(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_ConvertTimespecToTime(ulong instance, ref Timespec ts, out long xrTime);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_ConvertTimeToTimespec(ulong instance, long xrTime, out Timespec ts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_CreateReferenceSpace(ulong session, ref XrReferenceSpaceCreateInfo info, out ulong space);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_LocateSpace(ulong space, ulong baseSpace, long time, ref XrSpaceLocation location);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PFN_DestroySpace(ulong space);

        static ulong s_Instance, s_ViewSpace;
        static PFN_ConvertTimespecToTime s_ToTime;
        static PFN_ConvertTimeToTimespec s_ToTimespec;
        static PFN_CreateReferenceSpace  s_CreateRefSpace;
        static PFN_LocateSpace           s_Locate;
        static PFN_DestroySpace          s_DestroySpace;

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            s_Instance = xrInstance;
            var gipa = Marshal.GetDelegateForFunctionPointer<PFN_GetInstanceProcAddr>(xrGetInstanceProcAddr);
            s_ToTime         = Get<PFN_ConvertTimespecToTime>(gipa, "xrConvertTimespecTimeToTimeKHR");
            s_ToTimespec     = Get<PFN_ConvertTimeToTimespec>(gipa, "xrConvertTimeToTimespecTimeKHR");
            s_CreateRefSpace = Get<PFN_CreateReferenceSpace>(gipa, "xrCreateReferenceSpace");
            s_Locate         = Get<PFN_LocateSpace>(gipa, "xrLocateSpace");
            s_DestroySpace   = Get<PFN_DestroySpace>(gipa, "xrDestroySpace");
            if (s_ToTime == null || s_ToTimespec == null)
                Debug.LogWarning("[SF-Recorder] XR_KHR_convert_timespec_time unavailable — headset timestamps cannot be aligned.");
            return true;
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            if (s_CreateRefSpace == null) return;
            var info = new XrReferenceSpaceCreateInfo
            {
                type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO,
                referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW,
                pose = new XrPosef { qw = 1f },
            };
            int r = s_CreateRefSpace(xrSession, ref info, out s_ViewSpace);
            if (r != XR_SUCCESS) { Debug.LogWarning($"[SF-Recorder] xrCreateReferenceSpace(VIEW) failed: {r}"); s_ViewSpace = 0; return; }

            if (s_ToTime != null && s_ToTimespec != null && s_Locate != null)
            {
                OpenXRRigBridge.LocateHeadAtMonotonicNs = LocateHead;
                OpenXRRigBridge.XrTimeToMonotonicNs     = ToMonotonic;
            }
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            OpenXRRigBridge.LocateHeadAtMonotonicNs = null;
            OpenXRRigBridge.XrTimeToMonotonicNs     = null;
            if (s_ViewSpace != 0 && s_DestroySpace != null) s_DestroySpace(s_ViewSpace);
            s_ViewSpace = 0;
        }

        protected override void OnInstanceDestroy(ulong xrInstance) => s_Instance = 0;

        static long ToMonotonic(long xrTime)
        {
            return s_ToTimespec(s_Instance, xrTime, out var ts) == XR_SUCCESS ? ts.sec * 1_000_000_000L + ts.nsec : 0L;
        }

        // Head pose in the app (tracking) space at a monotonic time, converted to Unity
        // conventions: OpenXR is right-handed (−Z forward); Unity is left-handed (+Z forward).
        static (bool ok, Pose pose) LocateHead(long monoNs)
        {
            ulong appSpace = GetCurrentAppSpace();
            if (s_ViewSpace == 0 || appSpace == 0) return (false, Pose.identity);

            var ts = new Timespec { sec = monoNs / 1_000_000_000L, nsec = monoNs % 1_000_000_000L };
            if (s_ToTime(s_Instance, ref ts, out long xrTime) != XR_SUCCESS) return (false, Pose.identity);

            var loc = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
            if (s_Locate(s_ViewSpace, appSpace, xrTime, ref loc) != XR_SUCCESS) return (false, Pose.identity);
            const ulong valid = XR_SPACE_LOCATION_ORIENTATION_VALID | XR_SPACE_LOCATION_POSITION_VALID;
            if ((loc.locationFlags & valid) != valid) return (false, Pose.identity);

            var p = loc.pose;
            return (true, new Pose(new Vector3(p.px, p.py, -p.pz), new Quaternion(-p.qx, -p.qy, p.qz, p.qw)));
        }

        static T Get<T>(PFN_GetInstanceProcAddr gipa, string name) where T : Delegate
        {
            return gipa(s_Instance, name, out var fn) == XR_SUCCESS && fn != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;
        }
    }
}
