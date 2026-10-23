using UnityEngine;
using UnityEngine.EventSystems;

namespace HypercarX
{
    /// <summary>On-screen touch button that reports whether it is currently held. Each button
    /// tracks its own pointer, so steering and throttle can be pressed at the same time.</summary>
    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public bool held;
        public void OnPointerDown(PointerEventData e) { held = true; }
        public void OnPointerUp(PointerEventData e) { held = false; }
        public void OnPointerExit(PointerEventData e) { held = false; }
        void OnDisable() { held = false; }
    }
}
