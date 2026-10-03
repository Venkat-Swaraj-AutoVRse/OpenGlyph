using System;
using UnityEngine;

namespace LightSide
{
    [CreateAssetMenu(fileName = "ModRegisterConfig", menuName = "UniText/Mod Register Config")]
    public class ModRegisterConfig : ScriptableObject
    {
        /// <summary>
        /// Raised when the config changes: from the Inspector (editor) or when runtime code calls
        /// <see cref="NotifyChanged"/>. Subscribed components re-initialise their modifiers and rebuild.
        /// Available in players too.
        /// </summary>
        public event Action Changed;

        /// <summary>Signals that <see cref="modRegisters"/> was modified at runtime, so components using this config refresh.</summary>
        public void NotifyChanged() => Changed?.Invoke();

    #if UNITY_EDITOR
        // OnValidate does not exist in players; runtime code uses NotifyChanged().
        private void OnValidate()
        {
            NotifyChanged();
        }
    #endif

        [SerializeField]
        [Tooltip("Modifier/rule pairs that define how markup is parsed and applied (e.g., color, bold, links).")]
        public StyledList<ModRegister> modRegisters = new();
    }
}
