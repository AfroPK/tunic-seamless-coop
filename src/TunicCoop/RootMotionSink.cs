using System;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Sits next to the puppet's Animator. Because it implements OnAnimatorMove, Unity treats root motion as
    /// "handled by script": it is removed from the pose and handed to us, and we throw it away. Without this,
    /// root-motion clips (the roll) slide the mesh away from the puppet while the network also moves it.
    /// </summary>
    public class RootMotionSink : MonoBehaviour
    {
        public RootMotionSink(IntPtr ptr) : base(ptr) { }

        void OnAnimatorMove() { }
    }
}
