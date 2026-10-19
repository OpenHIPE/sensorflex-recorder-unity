using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SensorFlex.Recorder
{
    // World-space Record/Stop panel for headsets. Put it on a world-space Canvas; it builds its
    // own content. Only uGUI is used here: hand/controller input comes from whatever the scene
    // provides (e.g. XRI's TrackedDeviceGraphicRaycaster + XRUIInputModule with poke/pinch).
    // While recording the panel collapses to just the Stop button and timer.
    //
    // The panel is world-locked: once head tracking starts, the anchor is placed in front of the
    // user and then left alone. If something else moves the anchor (e.g. a grab handle), the
    // panel turns about the vertical axis to keep facing the user.
    [RequireComponent(typeof(Canvas))]
    [AddComponentMenu("XR/SensorFlex/Recorder XR Panel")]
    public sealed class RecorderXrPanel : MonoBehaviour
    {
        [SerializeField] ARSensorFlexRecorder m_Recorder;

        [Tooltip("Transform that is placed and turned. Defaults to this panel.")]
        [SerializeField] Transform m_Anchor;
        [Tooltip("Initial placement relative to the head: forward distance (horizontal) and drop below eye height, metres.")]
        [SerializeField] float m_PlaceDistance = 0.45f;
        [SerializeField] float m_PlaceDrop     = 0.3f;

        static readonly Color k_Panel  = new Color(0.08f, 0.09f, 0.11f, 0.92f);
        static readonly Color k_Record = new Color(0.85f, 0.18f, 0.18f, 1f);
        static readonly Color k_Stop   = new Color(0.25f, 0.27f, 0.31f, 1f);
        static readonly Color k_Muted  = new Color(0.45f, 0.47f, 0.5f, 1f);

        const float k_Width = 360f, k_Pad = 16f, k_ButtonH = 72f, k_TitleH = 36f, k_StatusH = 52f;

        RectTransform _root, _details;
        Image         _buttonImage;
        Button        _button;
        Text          _buttonText, _status;
        float         _recordingSince;
        string        _last;
        float         _lastPress = float.NegativeInfinity;
        const float   k_PressCooldown = 0.8f;
        bool          _placed;
        Vector3       _lastAnchorPos;

        void Start()
        {
            if (!m_Anchor) m_Anchor = transform;
            Build();
            if (m_Recorder)
            {
                m_Recorder.RecordingStartedEvent   += _ => _recordingSince = Time.realtimeSinceStartup;
                m_Recorder.RecordingFinalizedEvent += p => _last = "Saved " + Path.GetFileName(p[0]);
                m_Recorder.RecordingFailedEvent    += e => _last = "Failed: " + e;
            }
        }

        void OnPress()
        {
            if (!m_Recorder || !m_Recorder.isActiveAndEnabled || !_button.interactable) return;
            // One press per poke: ignore re-entries from hand jitter at the panel surface.
            if (Time.unscaledTime - _lastPress < k_PressCooldown) return;
            _lastPress = Time.unscaledTime;
            if (m_Recorder.IsRecording) m_Recorder.StopRecording();
            else                        m_Recorder.StartRecording();
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (!cam || !m_Anchor) return;
            var head = cam.transform;

            if (!_placed)
            {
                // Wait for the first tracked head pose (it starts at the rig origin).
                if (head.localPosition == Vector3.zero && head.localRotation == Quaternion.identity) return;
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up);
                m_Anchor.position = head.position + fwd.normalized * m_PlaceDistance + Vector3.down * m_PlaceDrop;
                _placed = true;
                FaceHead(head);
                _lastAnchorPos = m_Anchor.position;
                return;
            }

            if ((m_Anchor.position - _lastAnchorPos).sqrMagnitude > 1e-6f)
            {
                FaceHead(head);
                _lastAnchorPos = m_Anchor.position;
            }
        }

        // uGUI is readable when the canvas +Z points away from the viewer.
        void FaceHead(Transform head)
        {
            var away = Vector3.ProjectOnPlane(m_Anchor.position - head.position, Vector3.up);
            if (away.sqrMagnitude > 1e-6f) m_Anchor.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        void Update()
        {
            if (_root == null) return;

            bool active    = m_Recorder && m_Recorder.isActiveAndEnabled;
            bool recording = active && m_Recorder.IsRecording;
            bool canStart  = active && !recording && !m_Recorder.IsFinalizing;

            string status;
            if (!active)                    status = "Waiting for permissions…";
            else if (m_Recorder.IsFinalizing) status = "Saving…";
            else                            status = m_Recorder.IsStandby ? "Ready" : "Preparing…";
            if (_last != null) status += "\n" + _last;
            _status.text = status;

            if (recording)
            {
                int s = Mathf.FloorToInt(Time.realtimeSinceStartup - _recordingSince);
                _buttonText.text  = $"■  Stop   {s / 60}:{s % 60:00}";
                _buttonImage.color = k_Stop;
            }
            else
            {
                _buttonText.text  = "●  Record";
                _buttonImage.color = canStart ? k_Record : k_Muted;
            }
            _button.interactable = recording || canStart;

            // Collapse to the button alone while recording.
            if (_details.gameObject.activeSelf == recording)
            {
                _details.gameObject.SetActive(!recording);
                _root.sizeDelta = new Vector2(k_Width, recording ? k_ButtonH + 2 * k_Pad : k_TitleH + k_StatusH + k_ButtonH + 3 * k_Pad);
            }
        }

        // ── Layout ─────────────────────────────────────────────────────────

        void Build()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            _root = (RectTransform)transform;
            _root.pivot     = new Vector2(0.5f, 0f);    // bottom-anchored: collapsing keeps the button in place
            _root.sizeDelta = new Vector2(k_Width, k_TitleH + k_StatusH + k_ButtonH + 3 * k_Pad);

            var bg = NewRect("Background", _root);
            Stretch(bg);
            bg.gameObject.AddComponent<Image>().color = k_Panel;

            // Button, pinned to the bottom so it stays put when the panel collapses.
            var btn = NewRect("Record Button", _root);
            btn.anchorMin = new Vector2(0f, 0f);
            btn.anchorMax = new Vector2(1f, 0f);
            btn.pivot     = new Vector2(0.5f, 0f);
            btn.offsetMin = new Vector2(k_Pad, k_Pad);
            btn.offsetMax = new Vector2(-k_Pad, k_Pad + k_ButtonH);
            _buttonImage = btn.gameObject.AddComponent<Image>();
            _button      = btn.gameObject.AddComponent<Button>();
            _button.targetGraphic = _buttonImage;
            // Act on press, not on click: a click also needs the release over the button, and a
            // fingertip poke often drifts or jitters while pulling back out of the panel.
            var trigger = btn.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => OnPress());
            trigger.triggers.Add(down);
            _buttonText = NewText(btn, font, 30, FontStyle.Bold, Color.white);

            _details = NewRect("Details", _root);
            _details.anchorMin = new Vector2(0f, 1f);
            _details.anchorMax = new Vector2(1f, 1f);
            _details.pivot     = new Vector2(0.5f, 1f);
            _details.offsetMin = new Vector2(k_Pad, -(k_Pad + k_TitleH + k_StatusH));
            _details.offsetMax = new Vector2(-k_Pad, -k_Pad);

            var title = NewText(_details, font, 26, FontStyle.Bold, Color.white);
            title.text = "SensorFlex Recorder";
            var tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
            tr.offsetMin = new Vector2(0f, -k_TitleH); tr.offsetMax = Vector2.zero;

            _status = NewText(_details, font, 20, FontStyle.Normal, new Color(0.8f, 0.82f, 0.85f));
            var sr = _status.rectTransform;
            sr.anchorMin = Vector2.zero; sr.anchorMax = new Vector2(1f, 0f); sr.pivot = new Vector2(0.5f, 0f);
            sr.offsetMin = Vector2.zero; sr.offsetMax = new Vector2(0f, k_StatusH);
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static Text NewText(Transform parent, Font font, int size, FontStyle style, Color color)
        {
            var rt = NewRect("Text", parent);
            Stretch(rt);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font; t.fontSize = size; t.fontStyle = style; t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            return t;
        }
    }
}
