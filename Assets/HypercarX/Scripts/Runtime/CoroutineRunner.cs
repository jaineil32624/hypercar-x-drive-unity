using UnityEngine;

namespace HypercarX
{
    /// <summary>Hosts coroutines that must survive GameManager.StopAllCoroutines (e.g. the self-test).</summary>
    public class CoroutineRunner : MonoBehaviour { }
}
