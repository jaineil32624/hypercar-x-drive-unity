using UnityEngine;

namespace HypercarX
{
    public class Checkpoint : MonoBehaviour
    {
        public int index;
        void OnTriggerEnter(Collider other)
        {
            var car = other.attachedRigidbody ? other.attachedRigidbody.GetComponent<CarController>() : null;
            if (car && GameManager.I) GameManager.I.OnCheckpoint(car, index);
        }
    }
}
