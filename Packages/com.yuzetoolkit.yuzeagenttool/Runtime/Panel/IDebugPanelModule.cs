#nullable enable
#if YUZE_USE_UNITY_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace YuzeToolkit.Agent
{
    public interface IDebugPanelModule
    {
        int SortOrder { get; }

#if YUZE_USE_UNITY_INPUT_SYSTEM
        Key ToggleKey { get; }
#endif

        void Initialize(DebugPanelContext context);

        void SetVisible(bool visible);

        void Tick();

        void Shutdown();
    }
}
