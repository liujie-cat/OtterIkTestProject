using UnityEngine;

namespace OtterLocomotion
{
    /// <summary>
    /// The sea: still water filling the unit cube this sits on (scaled to the sea's size and depth) up to its top
    /// face. The one place the water's height is kept: what floats in the water or marks it reads the surface here.
    /// </summary>
    [DisallowMultipleComponent]
    public class Water : MonoBehaviour
    {
        /// <summary>Height of the water's surface (m, world): the box's top face.</summary>
        public float SurfaceY => transform.TransformPoint(0f, 0.5f, 0f).y;
    }
}
