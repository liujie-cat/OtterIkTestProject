using System.Linq;
using UnityEngine;

namespace OtterLocomotion
{
    public enum LimbId { FL, FR, HL, HR }

    /// <summary>Naming conventions of the sea otter rig (built on the cat skeleton) that the locomotion code relies on.</summary>
    public static class OtterRig
    {
        public const int LimbCount = 4;
        public const string Hip = "root.x", Chest = "spine_03.x", Head = "head.x", TailRoot = "c_tail_00.x";

        public static bool IsFore(LimbId limb) => limb == LimbId.FL || limb == LimbId.FR;

        /// <summary>-1 for the anatomical left limbs, +1 for the right.</summary>
        public static int Side(LimbId limb) => limb == LimbId.FL || limb == LimbId.HL ? -1 : 1;

        /// <summary>Forelimb chains are the "_dupli_001" copies of the cat's leg bones; sides end in .l / .r.</summary>
        public static bool TryClassify(AdvancedTwoBoneIK ik, out LimbId limb)
        {
            limb = default;
            if (ik == null || ik.root == null || ik.root.bone == null) return false;
            string bone = ik.root.bone.name;
            bool right = bone.EndsWith(".r");
            if (!right && !bone.EndsWith(".l")) return false;
            limb = bone.Contains("dupli")
                ? (right ? LimbId.FR : LimbId.FL)
                : (right ? LimbId.HR : LimbId.HL);
            return true;
        }

        public static Transform FindActiveBone(Transform root, string name) =>
            root.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == name && t.gameObject.activeInHierarchy);
    }
}
