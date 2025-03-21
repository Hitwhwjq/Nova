using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nova
{
    public class InputManager : MonoBehaviour
    {
        public static string InputFilesDirectory => Path.Combine(Application.persistentDataPath, "Input");
        private static string BindingsFilePath => Path.Combine(InputFilesDirectory, "bindings.json");

        public InputActionAsset defaultActionAsset;

        private ActionAssetData _actionAsset;

        public ActionAssetData actionAsset
        {
            get => _actionAsset;
            set
            {
                _actionAsset = value;
                _actionAsset.data.Enable();
            }
        }

        private void Awake()
        {
            Init();
        }

        private void OnDestroy()
        {
            Flush();
        }

        /// <summary>
        /// Must be called before accessing any other members.
        /// </summary>
        public void Init()
        {
            if (actionAsset != null)
            {
                return;
            }

            if (File.Exists(BindingsFilePath))
            {
                var json = File.ReadAllText(BindingsFilePath);
                actionAsset = new ActionAssetData(InputActionAsset.FromJson(json));
            }
            else
            {
                actionAsset = new ActionAssetData(defaultActionAsset.Clone());
            }
        }

        public void Flush()
        {
            var json = actionAsset.data.ToJson();
            Directory.CreateDirectory(InputFilesDirectory);
            File.WriteAllText(BindingsFilePath, json);
        }

        /// <summary>
        /// Checks whether an abstract key is triggered.
        /// Only activates once. To check whether a key is held, use <see cref="IsPressed"/>.
        /// </summary>
        public bool IsTriggered(AbstractKey key)
        {
            if (isRebinding)
            {
                return false;
            }

#if !UNITY_EDITOR
            if (ActionAssetData.IsEditorOnly(key))
            {
                return false;
            }
#endif

            if (!actionAsset.TryGetAction(key, out var action))
            {
                Debug.LogError($"Nova: Missing action key: {key}");
                return false;
            }

            return action.triggered;
        }

        public bool IsPressed(AbstractKey key)
        {
            if (isRebinding)
            {
                return false;
            }

#if !UNITY_EDITOR
            if (ActionAssetData.IsEditorOnly(key))
            {
                return false;
            }
#endif

            if (!actionAsset.TryGetAction(key, out var action))
            {
                Debug.LogError($"Nova: Missing action key: {key}");
                return false;
            }

            return action.IsPressed();
        }

        // When the input is disabled, the user can still close alert box, hide button ring, show dialogue box,
        // step forward, and trigger global shortcuts
        // inputEnabled and fastForwardShortcutEnabled are not in RestoreData, because the user cannot save when the input is disabled
        private bool _inputEnabled = true;
        private bool _fastForwardShortcutEnabled = true;

        public bool inputEnabled
        {
            get => _inputEnabled;
            set
            {
                _inputEnabled = value;
                _fastForwardShortcutEnabled = value;
            }
        }

        public bool fastForwardShortcutEnabled
        {
            get => _fastForwardShortcutEnabled;
            set
            {
                if (inputEnabled)
                {
                    Debug.LogWarning($"Nova: fastForwardShortcutEnabled can be set only when inputEnabled == false");
                }
                else
                {
                    _fastForwardShortcutEnabled = value;
                }
            }
        }

        [HideInInspector] public bool isRebinding;
    }
}
