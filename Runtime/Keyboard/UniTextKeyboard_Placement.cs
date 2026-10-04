using UnityEngine;

namespace LightSide
{
    // Placement: below the field, at an anchor, or following the head.
    public partial class UniTextKeyboard
    {
        /// <summary>Whether the keyboard lives in 3D (world keys, or a World Space canvas), so tilt and metre offsets apply.</summary>
        private bool IsSpatial
        {
            get
            {
                if (isWorld) return true;
                var c = RootCanvas;
                return c != null && c.renderMode == RenderMode.WorldSpace;
            }
        }

        /// <summary>Moves the keyboard per <see cref="Placement"/> for <paramref name="field"/> (also called when it opens).</summary>
        public void Place(UniTextInputField field)
        {
            EnsureBuilt();
            switch (m_Placement)
            {
                case KeyboardPlacement.BelowField:
                    if (field != null) PlaceBelow((RectTransform)field.transform);
                    break;
                case KeyboardPlacement.Transform:
                    if (m_Anchor != null)
                        transform.SetPositionAndRotation(m_Anchor.TransformPoint(m_AnchorOffset), m_Anchor.rotation);
                    break;
                case KeyboardPlacement.FollowHead:
                    if (HeadTransform != null && TargetHeadPose(out var p, out var r)) transform.SetPositionAndRotation(p, r);
                    break;
            }
        }

        private void PlaceBelow(RectTransform field)
        {
            var fieldCanvas = field.GetComponentInParent<Canvas>();
            var fieldRoot = fieldCanvas != null ? fieldCanvas.rootCanvas : null;
            var fieldSpatial = fieldRoot == null || fieldRoot.renderMode == RenderMode.WorldSpace;
            var spatial = IsSpatial;
            // A world keyboard cannot sit next to a Screen Space field (and the other way round).
            if (spatial != fieldSpatial) return;
            if (!spatial && RootCanvas != fieldRoot) return;

            field.GetWorldCorners(cornerScratch);
            var bottom = (cornerScratch[0] + cornerScratch[3]) * 0.5f;
            var fieldRot = field.rotation;
            var up = fieldRot * Vector3.up;
            var towardViewer = fieldRot * Vector3.back;
            var rot = spatial ? fieldRot * Quaternion.Euler(m_Tilt, 0f, 0f) : fieldRot;
            var rt = (RectTransform)transform;
            var rect = rt.rect;
            var s = rt.lossyScale;
            var gap = spatial ? m_FieldGap : m_KeySize * 0.2f * Mathf.Abs(s.y);
            var top = bottom - up * gap + (spatial ? towardViewer * m_TowardViewer : Vector3.zero);
            // The point of the keyboard's top edge centre goes to 'top'.
            var pos = top - rot * new Vector3(rect.center.x * s.x, rect.yMax * s.y, 0f);
            transform.SetPositionAndRotation(pos, rot);
        }

        private Transform HeadTransform
        {
            get
            {
                if (m_Head != null) return m_Head;
                var cam = Camera.main;
                return cam != null ? cam.transform : null;
            }
        }

        private bool TargetHeadPose(out Vector3 pos, out Quaternion rot)
        {
            var h = HeadTransform;
            pos = default;
            rot = Quaternion.identity;
            if (h == null) return false;
            var fwd = h.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(h.up, Vector3.up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            pos = h.position + fwd * m_FollowDistance + Vector3.up * m_FollowHeight;
            // UI faces -forward, so the keyboard looks away from the head; tilt the bottom towards it.
            rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(m_Tilt, 0f, 0f);
            return true;
        }

        /// <summary>Lazy follow: starts moving when the keyboard leaves the view cone (or drifts in distance), stops when it arrives.</summary>
        private void UpdateFollow(float dt)
        {
            var h = HeadTransform;
            if (h == null || !TargetHeadPose(out var tp, out var tr)) return;
            if (!following)
            {
                var fwd = h.forward;
                fwd.y = 0f;
                var to = transform.position - h.position;
                var dist = to.magnitude;
                to.y = 0f;
                var angle = fwd.sqrMagnitude > 1e-6f && to.sqrMagnitude > 1e-6f ? Vector3.Angle(fwd, to) : 0f;
                if (angle > m_FollowAngle || Mathf.Abs(dist - Vector3.Distance(tp, h.position)) > 0.15f) following = true;
                else return;
            }
            var t = 1f - Mathf.Exp(-m_FollowSpeed * dt);
            var p = Vector3.Lerp(transform.position, tp, t);
            var r = Quaternion.Slerp(transform.rotation, tr, t);
            transform.SetPositionAndRotation(p, r);
            if ((p - tp).sqrMagnitude < 1e-4f && Quaternion.Angle(r, tr) < 1f) following = false;
        }

        /// <summary>Whether a lazy follow is moving the keyboard.</summary>
        public bool IsFollowing => following;
    }
}
