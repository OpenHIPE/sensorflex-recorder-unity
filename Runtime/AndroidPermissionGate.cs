using System.Collections;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace SensorFlex.Recorder
{
    // Requests Android runtime permissions, then enables the components that need them
    // (e.g. ARCameraManager / AROcclusionManager on Android XR, and the recorder). Components
    // listed here should start disabled in the scene. On other platforms they are enabled at once.
    [AddComponentMenu("XR/SensorFlex/Android Permission Gate")]
    public sealed class AndroidPermissionGate : MonoBehaviour
    {
        // HAND_TRACKING: hand joints on Android XR. Without it only the system pinch/aim pose works.
        [SerializeField] string[]  m_Permissions = { "android.permission.CAMERA", "android.permission.SCENE_UNDERSTANDING_FINE", "android.permission.HAND_TRACKING" };
        [SerializeField] Behaviour[] m_EnableWhenGranted = new Behaviour[0];
        [Tooltip("Enable the components even if some permissions were denied.")]
        [SerializeField] bool m_EnableIfDenied = true;

        public bool Done { get; private set; }

        IEnumerator Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var missing = new System.Collections.Generic.List<string>();
            foreach (var p in m_Permissions)
                if (!Permission.HasUserAuthorizedPermission(p)) missing.Add(p);

            if (missing.Count > 0)
            {
                bool answered = false;
                var cb = new PermissionCallbacks();
                cb.PermissionGranted += _ => answered = AllAnswered(missing);
                cb.PermissionDenied  += p => { Debug.LogWarning("[SF-Recorder] Permission denied: " + p); answered = true; };
                Permission.RequestUserPermissions(missing.ToArray(), cb);
                float timeout = Time.realtimeSinceStartup + 120f;
                while (!answered && !AllAnswered(missing) && Time.realtimeSinceStartup < timeout)
                    yield return new WaitForSeconds(0.25f);
            }

            bool all = true;
            foreach (var p in m_Permissions) all &= Permission.HasUserAuthorizedPermission(p);
            if (!all && !m_EnableIfDenied) { Done = true; yield break; }
#endif
            foreach (var b in m_EnableWhenGranted)
                if (b) b.enabled = true;
            Done = true;
            yield break;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool AllAnswered(System.Collections.Generic.List<string> perms)
        {
            foreach (var p in perms) if (!Permission.HasUserAuthorizedPermission(p)) return false;
            return true;
        }
#endif
    }
}
